// =============================================================================
// MainThreadDispatcher.cs
// Marshals work from background threads (networking) onto Unity's main thread.
//
// WHY THIS EXISTS
//   Unity's entire API — including TMP text assignment, Instantiate, and even
//   Debug.Log in some versions — must be called from the main thread. The TCP
//   receive loop runs on a background thread via async/await continuations that
//   may resume on the thread pool. Without this dispatcher, touching any Unity
//   API from the network layer will throw or silently corrupt state.
//
// HOW IT WORKS
//   A ConcurrentQueue is written to from any thread (lock-free MPSC structure).
//   Update() drains it on the main thread each frame. ConcurrentQueue uses
//   interlocked operations internally — no Monitor/lock, no GC from locking.
//
// GC IMPACT
//   - ConcurrentQueue<Action>: each Enqueue boxes the Action delegate once.
//     This is one unavoidable alloc per dispatch. In a chat system running at
//     human message rates this is negligible.
//   - The queue itself is allocated once at startup and reused forever.
//   - We do NOT use a List<Action> + lock because that requires allocating a
//     temporary list for the drain loop to avoid lock contention.
//
// SINGLETON PATTERN
//   MonoBehaviour singleton so it persists across scene loads and is accessible
//   from anywhere without a serialised reference. Chat systems often survive
//   scene transitions; the DontDestroyOnLoad call handles that.
// =============================================================================

using System;
using System.Collections.Concurrent;
using UnityEngine;

public sealed class MainThreadDispatcher : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------

    private static MainThreadDispatcher _instance;

    public static MainThreadDispatcher Instance
    {
        get
        {
            return _instance;
        }
    }

    // -------------------------------------------------------------------------
    // Queue
    // -------------------------------------------------------------------------

    // ConcurrentQueue: lock-free for single-consumer (main thread drain) +
    // multiple producers (network threads). Ideal for this pattern.
    private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Drains up to <see cref="MaxActionsPerFrame"/> queued actions per frame.
    /// Capping prevents a flood of network messages from freezing the main thread.
    /// </summary>
    private const int MaxActionsPerFrame = 32;

    private void Update()
    {
        int processed = 0;
        // TryDequeue is lock-free and returns false immediately when empty —
        // no idle allocation, no spin-wait.
        while (processed < MaxActionsPerFrame && _queue.TryDequeue(out Action action))
        {
            action?.Invoke();
            processed++;
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Schedules <paramref name="action"/> to run on the main thread next Update.
    /// Safe to call from any thread.
    /// </summary>
    public void Enqueue(Action action)
    {
        if (action == null) return;
        _queue.Enqueue(action);
    }
}
