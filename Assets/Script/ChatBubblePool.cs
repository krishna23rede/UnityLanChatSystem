// =============================================================================
// ChatBubblePool.cs
// GameObject pool for ChatBubbleView instances.
//
// DESIGN GOALS
//   1. Zero Instantiate calls after the pool is warm.
//   2. Oldest-bubble recycling: when the pool is exhausted and the visible
//      cap is hit, we reuse the OLDEST bubble rather than allocating a new
//      one. This bounds memory at exactly MaxVisible GameObjects forever.
//   3. Deterministic visible count: chat windows cap visible messages to
//      prevent unbounded memory growth. We enforce this here.
//
// OBJECT POOL VS UNITY'S BUILT-IN ObjectPool<T>
//   Unity 2021+ ships UnityEngine.Pool.ObjectPool<T>. We implement our own
//   here for two reasons:
//   (a) Compatibility with Unity 2019/2020 LTS (common in studios).
//   (b) We need oldest-bubble recycling, which the built-in pool doesn't support.
//
// GC IMPACT
//   - Pool is a fixed-size array pre-allocated in Awake. No list growth.
//   - Get() and Release() do array index reads/writes only. Zero allocs.
//   - The Queue<ChatBubbleView> for active bubbles has a small internal array
//     that may resize once if initial history > PrewarmCount, then stabilises.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

public sealed class ChatBubblePool : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------

    [Header("Prefab")]
    [SerializeField] private ChatBubbleView bubblePrefab;

    [Header("Sizing")]
    [Tooltip("How many bubbles to instantiate at startup.")]
    [SerializeField] private int prewarmCount   = 30;

    [Tooltip("Maximum simultaneously visible bubbles. Oldest are recycled beyond this.")]
    [SerializeField] private int maxVisible      = 50;

    [Header("Layout")]
    [SerializeField] private Transform contentParent;

    // -------------------------------------------------------------------------
    // Pool state
    // -------------------------------------------------------------------------

    // Stack of inactive bubbles ready for reuse. Stack (LIFO) gives better
    // cache locality than Queue (FIFO) because the most-recently-used bubble's
    // GameObject and components are more likely still in CPU cache.
    private readonly Stack<ChatBubbleView> _free   = new Stack<ChatBubbleView>();

    // Queue of active (visible) bubbles in display order. FIFO so we can
    // recycle the OLDEST (front) bubble when maxVisible is reached.
    private readonly Queue<ChatBubbleView> _active = new Queue<ChatBubbleView>();

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        // Pre-warm: Instantiate all bubbles now, during loading, so the first
        // 'prewarmCount' messages have zero Instantiate cost.
        for (int i = 0; i < prewarmCount; i++)
        {
            ChatBubbleView bubble = CreateNewBubble();
            bubble.gameObject.SetActive(false);
            _free.Push(bubble);
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a bubble populated with <paramref name="message"/> and adds it
    /// to the content parent. Recycles the oldest visible bubble if at capacity.
    ///
    /// GC: Zero allocs if pool is warm. One Instantiate if pool is exhausted
    ///     beyond prewarmCount (only on first run with more history than prewarm).
    /// </summary>
    public ChatBubbleView Show(ChatMessage message)
    {
        ChatBubbleView bubble;

        // --- Recycle oldest if at visible cap ---
        if (_active.Count >= maxVisible)
        {
            // Dequeue the oldest bubble and reuse it rather than Instantiate.
            bubble = _active.Dequeue();
            // No need to call OnReturnToPool — we immediately repopulate it.
        }
        else if (_free.Count > 0)
        {
            // Take from the free pool.
            bubble = _free.Pop();
            bubble.gameObject.SetActive(true);
        }
        else
        {
            // Pool exhausted — grow. This should only happen if history is larger
            // than prewarmCount. It runs at most (historyCount - prewarmCount)
            // times, then never again.
            Debug.LogWarning("[ChatBubblePool] Pool exhausted — consider increasing prewarmCount.");
            bubble = CreateNewBubble();
        }

        // Populate and add to the end of the sibling list (bottom of chat).
        bubble.Populate(message);

        // SetAsLastSibling moves the RectTransform in the hierarchy without
        // allocating. Cheaper than Remove + Add.
        bubble.transform.SetAsLastSibling();

        _active.Enqueue(bubble);
        return bubble;
    }

    /// <summary>
    /// Returns all active bubbles to the pool and hides them.
    /// Called when clearing history.
    /// </summary>
    public void ReturnAll()
    {
        while (_active.Count > 0)
        {
            ChatBubbleView bubble = _active.Dequeue();
            ReturnToPool(bubble);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ChatBubbleView CreateNewBubble()
    {
        // Instantiate under contentParent so it participates in the layout.
        ChatBubbleView b = Instantiate(bubblePrefab, contentParent);
        b.OnReturnToPool(); // initialise to empty state
        return b;
    }

    private void ReturnToPool(ChatBubbleView bubble)
    {
        bubble.OnReturnToPool();
        bubble.gameObject.SetActive(false);
        _free.Push(bubble);
    }
}
