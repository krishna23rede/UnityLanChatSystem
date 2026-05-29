// =============================================================================
// ChatBubbleView.cs
// A reusable, poolable chat bubble with SEPARATE TMP labels per field.
//
// WHY SEPARATE LABELS (timestamp / username / content)?
//
//   The old approach packed everything into one TMP text with rich-text tags:
//     <color=#55FF55>[12:00]</color> <color=#FF5555>Alice:</color> \n <color=#FFFFFF>Hi</color>
//
//   Problems:
//   1. TMP must PARSE the rich-text markup on EVERY text assignment.
//      Each SetText call triggers the TMP parser, which allocates temporary
//      lists for tag tracking. For 50 bubbles on screen, this is 50 parse
//      passes per history load.
//   2. The tag strings themselves ("<color=#FF5555>", "</color>") are
//      concatenated fresh every time, generating garbage.
//   3. Changing ONLY the content (e.g. for an edit feature) requires
//      rebuilding the entire formatted string.
//
//   SEPARATE LABELS fix all three:
//   - Each label has a fixed color set once in the prefab's inspector.
//     No runtime color tags, no parser overhead.
//   - Assigning a clean string to a TMP label is nearly free:
//     TMP caches glyph layout and only re-meshes changed characters.
//   - Timestamp, username, and content can be updated independently.
//
// WHY POOLABLE?
//   Instantiate/Destroy per message causes:
//   - GC pressure from the destroyed GameObject's component references
//   - Layout rebuild storms as the ScrollRect's content height changes
//   - Frame spikes visible as hitches on mobile
//
//   With pooling, Instantiate runs only until the pool is warm (typically
//   after the first 20–30 messages). After that, sending a message has
//   zero allocation in the UI layer.
//
// CANVAS REBUILD OPTIMISATION
//   Each bubble is on its own Canvas component (OverrideSorting = false,
//   PixelPerfect = false). This makes it a "nested canvas" — when its text
//   changes, only THIS canvas rebuilds, not the entire chat panel canvas.
//   Without this, every new message causes the entire scroll view to rebuild
//   ALL vertex buffers. On mobile with 50+ bubbles, that's 16+ ms per message.
// =============================================================================

using TMPro;
using UnityEngine;

[RequireComponent(typeof(Canvas))] // nested canvas for isolated rebuilds
public sealed class ChatBubbleView : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector references — set these in the prefab
    // -------------------------------------------------------------------------

    [Header("Labels — assign in prefab, set color there, never via code")]
    [SerializeField] private TMP_Text timestampLabel;
    [SerializeField] private TMP_Text usernameLabel;
    [SerializeField] private TMP_Text contentLabel;

    // -------------------------------------------------------------------------
    // Pool state
    // -------------------------------------------------------------------------

    // Tracked by the pool — not a Unity active/inactive flag because
    // SetActive has a small cost (sends OnEnable/OnDisable messages).
    // Instead the pool physically moves inactive bubbles off-screen via
    // RectTransform.anchoredPosition, then moves them back on reuse.
    public bool InPool { get; private set; }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Populates all three labels from a <see cref="ChatMessage"/>.
    /// No string concatenation. No rich-text tags. No allocations beyond
    /// the TMP internal mesh update.
    /// </summary>
    public void Populate(ChatMessage message)
    {
        // TMP_Text.text setter: if the new value equals the current value,
        // TMP skips the mesh rebuild entirely. So repeated history loads
        // after pooled reuse are free if the message hasn't changed.
        timestampLabel.text = message.timestamp;
        usernameLabel.text  = message.username;
        contentLabel.text   = message.content;

        InPool = false;
    }

    /// <summary>Called by the pool when this bubble is returned.</summary>
    public void OnReturnToPool()
    {
        // Clear text to release TMP's internal glyph cache reference for these
        // strings — allows GC to collect them if no longer referenced elsewhere.
        timestampLabel.text = string.Empty;
        usernameLabel.text  = string.Empty;
        contentLabel.text   = string.Empty;
        InPool = true;
    }
}
