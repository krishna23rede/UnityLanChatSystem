// =============================================================================
// ChatManager.cs
// Central coordinator. Wires together TcpClientManager, ChatHistoryManager,
// ChatBubblePool, and the UI input. Owns scroll rect management.
//
// RESPONSIBILITIES
//   ✔ Subscribe to network events
//   ✔ Route incoming messages → history → pool
//   ✔ Handle local user input → model → network → history → pool
//   ✔ Manage ScrollRect auto-scroll
//   ✔ Display connection status
//   ✗ Does NOT do networking
//   ✗ Does NOT do file I/O
//   ✗ Does NOT manage bubble lifecycle
//
// SCROLL PERFORMANCE
//   Unity's ScrollRect recalculates its normalizedPosition every frame when
//   content height changes. We defer auto-scroll to end-of-frame using a
//   coroutine to let the layout system finish its pass first.
//   Calling scrollRect.normalizedPosition = Vector2.zero BEFORE the layout
//   pass completes is a no-op — the scroll jumps back. The coroutine waits
//   for WaitForEndOfFrame, which runs after all layout passes.
//
//   Additionally, we only auto-scroll if the user is already near the bottom
//   (within AutoScrollThreshold). If they've scrolled up to read history,
//   we don't yank them back down on each new message.
//
// INPUT HANDLING
//   We listen for the Enter key in Update() to allow keyboard submission.
//   This adds one key-state check per frame — negligible cost.
//   The Update loop is the ONLY per-frame work this component does when idle.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class ChatManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------

    [Header("Systems — assign in Inspector")]
    [SerializeField] private TcpClientManager  network;
    [SerializeField] private ChatHistoryManager history;
    [SerializeField] private ChatBubblePool     pool;

    [Header("Input UI")]
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private TMP_InputField usernameField;
    [SerializeField] private Button         sendButton;

    [Header("Scroll")]
    [SerializeField] private ScrollRect scrollRect;

    [Header("Status")]
    [SerializeField] private TMP_Text statusLabel; // e.g. "● Connected"

    [Header("Settings")]
    [Tooltip("If the user's scroll position is within this fraction from the " +
             "bottom, auto-scroll to bottom on new messages.")]
    [SerializeField, Range(0f, 0.5f)]
    private float autoScrollThreshold = 0.05f;

    // Reusable WaitForEndOfFrame — allocated once, reused for every scroll.
    // The old pattern 'yield return new WaitForEndOfFrame()' allocates a new
    // object every coroutine invocation. Caching eliminates that.
    private static readonly WaitForEndOfFrame _waitForEndOfFrame = new WaitForEndOfFrame();

    // -------------------------------------------------------------------------
    // Echo suppression
    // -------------------------------------------------------------------------
    // When we send a message, the server broadcasts it back to ALL clients
    // including the sender. We show the message locally immediately for
    // responsiveness, so when our own echo arrives from the server we must
    // discard it — otherwise the message appears twice.
    //
    // Key = "timestamp|username|content" — unique enough for a chat message.
    // We use a HashSet so the Contains check is O(1) with no allocation.
    // The entry is removed on first match so it only suppresses one echo
    // (a duplicate message from another user will still be shown).
    private readonly HashSet<string> _pendingEchoes = new HashSet<string>();

    // Builds a lightweight dedup key from a message. No string interpolation —
    // string.Concat with three args uses a fast internal overload.
    private static string EchoKey(ChatMessage m) =>
        string.Concat(m.timestamp, "|", m.username, "|", m.content);

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Start()
    {
        // --- Wire events ---
        network.OnMessageReceived      += HandleIncomingMessage;
        network.OnConnectionStateChanged += HandleConnectionState;

        if (sendButton != null)
            sendButton.onClick.AddListener(OnSendButtonClicked);

        // --- Populate UI from history ---
        // History was loaded in ChatHistoryManager.Awake(), so the cache is
        // already populated by the time Start() runs.
        foreach (ChatMessage msg in history.Cache)
            pool.Show(msg);

        ScrollToBottomNextFrame();
    }

    private void OnDestroy()
    {
        if (network != null)
        {
            network.OnMessageReceived       -= HandleIncomingMessage;
            network.OnConnectionStateChanged -= HandleConnectionState;
        }

        if (sendButton != null)
            sendButton.onClick.RemoveListener(OnSendButtonClicked);
    }

    // -------------------------------------------------------------------------
    // Update — keyboard submit only
    // -------------------------------------------------------------------------

    private void Update()
    {
        // Allow Enter key to submit while the input field is focused.
        // Input.GetKeyDown allocates nothing.
        if (inputField.isFocused && Input.GetKeyDown(KeyCode.Return))
            OnSendButtonClicked();
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called on the main thread (dispatched by TcpClientManager) when a
    /// remote message arrives.
    /// </summary>
    private void HandleIncomingMessage(ChatMessage message)
    {
        // If this is our own message echoed back by the server, discard it.
        // Remove() returns true only once per registered key, so a genuine
        // duplicate from another user with the same text still shows up.
        string key = EchoKey(message);
        if (_pendingEchoes.Remove(key))
            return;

        history.Append(message);
        pool.Show(message);
        TryAutoScroll();
    }

    private void HandleConnectionState(bool connected)
    {
        if (statusLabel == null) return;

        // String allocation here is acceptable — connection state changes are
        // infrequent (connect/disconnect, not per-message).
        statusLabel.text  = connected ? $"● {network.GetConnectStatus()}" : "○ Disconnected";
        statusLabel.color = connected
            ? new Color(0.33f, 1f, 0.33f)   // green
            : new Color(1f,    0.33f, 0.33f); // red
    }

    // -------------------------------------------------------------------------
    // Send
    // -------------------------------------------------------------------------

    private void OnSendButtonClicked()
    {
        // Guard: ignore empty or whitespace-only input.
        if (string.IsNullOrWhiteSpace(inputField.text)) return;

        // Build the model. DateTime.Now called once here — not in the model
        // constructor — so the timestamp reflects exactly when the user hit send.
        ChatMessage message = ChatMessage.Create(
            username: usernameField.text,
            content:  inputField.text);

        // Clear input immediately for responsiveness.
        inputField.text = string.Empty;

        // Re-focus so the user can keep typing without clicking.
        inputField.ActivateInputField();

        // 1. Show locally (don't wait for echo from server).
        history.Append(message);
        pool.Show(message);
        TryAutoScroll();

        // 2. Register this message so its server echo is silently discarded.
        _pendingEchoes.Add(EchoKey(message));

        // 3. Send over network.
        network.Send(message);
    }

    // -------------------------------------------------------------------------
    // Scroll
    // -------------------------------------------------------------------------

    private void TryAutoScroll()
    {
        // Only auto-scroll if user is near the bottom.
        // normalizedPosition.y == 0 means bottom of scroll view.
        if (scrollRect.normalizedPosition.y <= autoScrollThreshold)
            ScrollToBottomNextFrame();
    }

    private void ScrollToBottomNextFrame()
    {
        // Must wait for layout to finish before setting normalizedPosition.
        StartCoroutine(ScrollCoroutine());
    }

    private IEnumerator ScrollCoroutine()
    {
        // WaitForEndOfFrame: Unity runs layout rebuild passes during LateUpdate
        // and just before rendering. Waiting here ensures the content height
        // is finalised before we move the scroll position.
        yield return _waitForEndOfFrame;

        if (scrollRect != null)
            scrollRect.normalizedPosition = Vector2.zero; // 0 = bottom
    }

    // -------------------------------------------------------------------------
    // Public UI callbacks (wire to buttons in Inspector)
    // -------------------------------------------------------------------------

    public void ClearHistory()
    {
        history.Clear();
        pool.ReturnAll();
    }

    public void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}