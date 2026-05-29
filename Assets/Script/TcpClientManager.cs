// =============================================================================
// TcpClientManager.cs
// Owns the TCP connection lifecycle. Sends and receives framed packets.
// Parses raw JSON payload into ChatMessage models.
// Dispatches to the main thread via MainThreadDispatcher.
//
// RESPONSIBILITIES (Single Responsibility Principle)
//   ✔ Connect / disconnect
//   ✔ Send framed packets
//   ✔ Receive framed packets (background thread)
//   ✔ Parse payload → ChatMessage
//   ✔ Dispatch to main thread
//   ✗ Does NOT touch Unity UI
//   ✗ Does NOT write history
//   ✗ Does NOT know about ChatUI
//
// WIRE FORMAT
//   Each payload is a JSON-serialised ChatMessage:
//     { "timestamp":"...", "username":"...", "content":"..." }
//   Using a structured payload (vs raw text) means the receiver always has
//   all fields without re-parsing colour tags or splitting on delimiters.
//   JsonUtility is used for speed; it avoids reflection on IL2CPP.
//
// ASYNC / THREADING MODEL
//   ConnectAsync and ReceiveLoopAsync are async. Their continuations may run
//   on the thread pool (ConfigureAwait(false)). They NEVER call Unity APIs
//   directly. All Unity-side work is posted to MainThreadDispatcher.
//
// CANCELLATION
//   A CancellationTokenSource is created on connect and cancelled on
//   disconnect/destroy. This cleanly stops the receive loop without
//   Thread.Abort (which is deprecated in .NET 5+ and unreliable on Mono).
//
// RECONNECTION
//   A simple exponential back-off reconnect loop is included. Mobile networks
//   drop frequently; silent reconnect keeps UX smooth.
// =============================================================================

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class TcpClientManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------

    [Header("Connection")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int    port = 5000;

    [Header("Reconnect")]
    [SerializeField] private bool reconnectOnDisconnect = true;
    [SerializeField] private float reconnectDelaySeconds = 2f;
    [SerializeField] private float maxReconnectDelaySeconds = 30f;

    // -------------------------------------------------------------------------
    // Events — subscribe from other MonoBehaviours on the main thread
    // -------------------------------------------------------------------------

    /// <summary>Fired on main thread when a fully-parsed message arrives.</summary>
    public event Action<ChatMessage> OnMessageReceived;

    /// <summary>Fired on main thread when connection state changes.</summary>
    public event Action<bool> OnConnectionStateChanged;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private TcpClient        _client;
    private NetworkStream    _stream;
    private PacketWriter     _writer;
    private PacketReader     _reader;
    private CancellationTokenSource _cts;

    private bool _isConnected;
    public bool IsConnected => _isConnected;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Start()
    {
        _writer = new PacketWriter();
        _reader = new PacketReader();
        ConnectWithRetryAsync().Forget(); // fire-and-forget, errors are logged inside
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    // -------------------------------------------------------------------------
    // Connection
    // -------------------------------------------------------------------------

    private async Task ConnectWithRetryAsync()
    {
        float delay = reconnectDelaySeconds;

        while (true) // outer retry loop
        {
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;

            try
            {
                _client = new TcpClient();

                // ConnectAsync has no built-in timeout; wrap with a linked token.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked     = CancellationTokenSource.CreateLinkedTokenSource(
                                           ct, timeoutCts.Token);

                await _client.ConnectAsync(host, port).ConfigureAwait(false);
                // Note: TcpClient.ConnectAsync doesn't accept a CT in older Unity/.NET
                // versions. If yours does, pass linked.Token above.

                _stream = _client.GetStream();

                // Socket options for low-latency chat on mobile:
                //   NoDelay = true  → disables Nagle algorithm, sends small packets
                //                     immediately instead of buffering. Critical for
                //                     sub-100ms perceived latency on LAN.
                _client.NoDelay = true;

                // SO_KEEPALIVE lets the OS detect dead connections without app-level
                // pings. Helpful on mobile where connections silently drop.
                _client.Client.SetSocketOption(SocketOptionLevel.Socket,
                                                SocketOptionName.KeepAlive, true);

                SetConnected(true);
                delay = reconnectDelaySeconds; // reset back-off

                Debug.Log($"[TcpClientManager] Connected to {host}:{port}");

                // Run receive loop — blocks here until disconnected or error
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Triggered by Disconnect() — do not retry
                Debug.Log("[TcpClientManager] Connection cancelled.");
                break;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TcpClientManager] Connection error: {ex.Message}");
            }
            finally
            {
                CleanupSocket();
                SetConnected(false);
            }

            if (!reconnectOnDisconnect) break;
            if (_cts.IsCancellationRequested) break;

            Debug.Log($"[TcpClientManager] Reconnecting in {delay:F1}s…");
            await Task.Delay(TimeSpan.FromSeconds(delay), _cts.Token)
                      .ConfigureAwait(false);

            // Exponential back-off — avoids hammering a dead server on mobile
            delay = Math.Min(delay * 2f, maxReconnectDelaySeconds);
        }
    }

    // -------------------------------------------------------------------------
    // Receive loop — runs on a background thread
    // -------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ReadOneAsync blocks until a COMPLETE packet arrives.
            // This is the correct TCP framing pattern.
            string json = await _reader.ReadOneAsync(_stream, ct).ConfigureAwait(false);

            if (json == null) // clean EOF
            {
                Debug.Log("[TcpClientManager] Server closed connection.");
                break;
            }

            // Parse on the background thread — JsonUtility is safe to call
            // off the main thread (it uses no Unity API internally).
            ChatMessage msg = ParseJson(json);
            if (msg == null) continue;

            // Marshal to main thread before firing the event.
            // Capture msg in a local to avoid closure-over-loop-variable bug.
            ChatMessage captured = msg;
            MainThreadDispatcher.Instance.Enqueue(
                () => OnMessageReceived?.Invoke(captured));
        }
    }

    // -------------------------------------------------------------------------
    // Send — safe to call from main thread only
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialises <paramref name="message"/> to JSON and sends a framed packet.
    /// Fire-and-forget; errors are logged, not rethrown.
    /// </summary>
    public void Send(ChatMessage message)
    {
        if (!_isConnected || _stream == null) return;

        // Serialise to JSON.  JsonUtility.ToJson allocates one string — fine at
        // human message rates.
        string json = JsonUtility.ToJson(message);

        // Run the async send without blocking the main thread.
        SendInternalAsync(json).Forget();
    }

    private async Task SendInternalAsync(string json)
    {
        try
        {
            await _writer.SendAsync(_stream, json, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TcpClientManager] Send error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetConnected(bool connected)
    {
        _isConnected = connected;
        // Post connection state change to main thread so subscribers can
        // safely update UI.
        MainThreadDispatcher.Instance.Enqueue(
            () => OnConnectionStateChanged?.Invoke(connected));
    }

    private static ChatMessage ParseJson(string json)
    {
        try
        {
            return JsonUtility.FromJson<ChatMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TcpClientManager] Bad packet JSON: {ex.Message}");
            return null;
        }
    }

    private void CleanupSocket()
    {
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
    }

    /// <summary>Closes the connection and cancels the receive loop.</summary>
    public void Disconnect()
    {
        _cts?.Cancel();
        CleanupSocket();
    }
    public string GetConnectStatus()
    {
        return _isConnected ? $"Connected to {host} :: {port}" : "Disconnected";
    }
}


// =============================================================================
// TaskExtensions.cs (inline — small enough not to warrant a separate file)
// Forget() suppresses the "async method not awaited" compiler warning for
// fire-and-forget Tasks while still logging any unhandled exceptions.
// =============================================================================
internal static class TaskExtensions
{
    public static async void Forget(this Task task)
    {
        try   { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex) { Debug.LogError($"[Task] Unhandled: {ex}"); }
    }
}
