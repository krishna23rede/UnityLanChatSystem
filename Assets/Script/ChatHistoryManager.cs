// =============================================================================
// ChatHistoryManager.cs
// Persistent chat history using append-only NDJSON (newline-delimited JSON).
//
// WHY NDJSON INSTEAD OF A JSON ARRAY?
//   The old approach: maintain a full list in memory, reserialise the ENTIRE
//   array on quit. Problems:
//     1. On quit, serialising 10,000 messages blocks the main thread for
//        potentially hundreds of milliseconds.
//     2. If the app crashes, the write never happens and ALL history is lost.
//     3. Full rewrite grows as O(N) with message count.
//
//   NDJSON fix:
//     - Each message is ONE line of JSON, e.g.:
//         {"timestamp":"2024-01-01 12:00","username":"Alice","content":"Hi"}
//     - Appending a new message = one StreamWriter.WriteLine call.
//     - Zero in-memory accumulation needed for persistence.
//     - Crash-safe: every sent message is on disk immediately.
//     - Reading back = parse lines top-to-bottom. O(N) but done once at startup.
//
// ASYNC WRITES
//   File.AppendAllText blocks the calling thread. For mobile, where storage is
//   an eMMC flash chip with variable latency, this can spike 10–50 ms.
//   We use an async StreamWriter with FileOptions.Asynchronous so the write
//   is handed to the OS async I/O subsystem. The main thread returns immediately.
//
// IN-MEMORY CACHE
//   We keep a List<ChatMessage> in memory purely so ChatUI can populate itself
//   from history on startup without re-reading the file. After startup, the
//   list is the UI's backing store; the file is the durable log.
//   The list is capped at MaxCachedMessages to bound memory usage.
//
// THREAD SAFETY
//   AppendAsync is called from the main thread only (triggered by user send or
//   network receive — both dispatched to main thread). No lock needed.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public sealed class ChatHistoryManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------

    [Tooltip("Maximum messages kept in memory for UI population. " +
             "Older messages are dropped from memory but remain on disk.")]
    [SerializeField] private int maxCachedMessages = 200;

    // -------------------------------------------------------------------------
    // File path
    // -------------------------------------------------------------------------

    // Application.dataPath = Assets/ in Editor, <AppName>_Data/ in build.
    // We resolve this in Awake (main thread) because Application.dataPath
    // must not be called from a background thread.
    private string _filePath;

    // -------------------------------------------------------------------------
    // In-memory cache
    // -------------------------------------------------------------------------

    // Exposed as IReadOnlyList so consumers can't mutate it.
    private readonly List<ChatMessage> _cache = new List<ChatMessage>();
    public IReadOnlyList<ChatMessage> Cache => _cache;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        _filePath = Path.Combine(Application.dataPath, "chat_history.ndjson");
        LoadFromFile();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Appends <paramref name="message"/> to the in-memory cache and
    /// asynchronously writes it as one NDJSON line to disk.
    ///
    /// GC: JsonUtility.ToJson allocates one string.
    ///     StreamWriter.WriteLineAsync allocates a Task state machine.
    ///     Both are single allocs at human message rates — totally acceptable.
    /// </summary>
    public void Append(ChatMessage message)
    {
        // --- Update in-memory cache ---
        _cache.Add(message);

        // Cap memory: remove from front (oldest) when over limit.
        // RemoveAt(0) on a List<T> is O(N) due to element shifting.
        // For 200 messages this is negligible. If you need 10 000+, replace
        // with a Queue<ChatMessage> or CircularBuffer.
        if (_cache.Count > maxCachedMessages)
            _cache.RemoveAt(0);

        // --- Async disk append ---
        AppendToDiskAsync(message).Forget();
    }

    /// <summary>
    /// Deletes the history file and clears the in-memory cache.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        Debug.Log("[ChatHistoryManager] History cleared.");
    }

    // -------------------------------------------------------------------------
    // Disk I/O
    // -------------------------------------------------------------------------

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath))
        {
            Debug.Log($"[ChatHistoryManager] No history file at {_filePath}");
            return;
        }

        try
        {
            // ReadAllLines allocates one string per line. For 200 messages this
            // is ~200 small strings — fine for a startup-time operation.
            string[] lines = File.ReadAllLines(_filePath);
            int start = Math.Max(0, lines.Length - maxCachedMessages);

            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                ChatMessage msg = TryParseJson(lines[i]);
                if (msg != null)
                    _cache.Add(msg);
            }

            Debug.Log($"[ChatHistoryManager] Loaded {_cache.Count} message(s) from {_filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError("[ChatHistoryManager] Load failed: " + ex.Message);
        }
    }

    private async Task AppendToDiskAsync(ChatMessage message)
    {
        try
        {
            // One JSON line per message.
            // ToJson allocates one string — unavoidable.
            string line = JsonUtility.ToJson(message);

            // FileMode.Append + FileOptions.Asynchronous = OS-level async append.
            // No full file rewrite. No main thread block.
            using var fs = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            using var writer = new StreamWriter(fs);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ChatHistoryManager] Append failed: " + ex.Message);
        }
    }

    private static ChatMessage TryParseJson(string line)
    {
        try   { return JsonUtility.FromJson<ChatMessage>(line); }
        catch { return null; }
    }
}
