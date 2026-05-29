// =============================================================================
// ChatMessage.cs
// Pure data model. Zero Unity dependency. Zero allocations at construction
// beyond the string fields themselves (which are unavoidable — strings are
// reference types in C#). No methods, no logic, no inheritance.
//
// WHY A SEPARATE MODEL?
//   Separating the data model from transport, UI, and persistence means each
//   layer can evolve independently. The network layer produces ChatMessages;
//   the UI layer consumes them; the history layer serialises them. No layer
//   knows about the others.
//
// WHY [Serializable]?
//   JsonUtility requires it. We keep this attribute here so the history layer
//   can serialise the model directly without a DTO translation step.
// =============================================================================

using System;

[Serializable]
public sealed class ChatMessage
{
    // -------------------------------------------------------------------------
    // Fields are kept as plain strings because:
    // (a) JsonUtility only serialises fields, not properties.
    // (b) string interning means repeated usernames/timestamps share memory.
    // -------------------------------------------------------------------------

    /// <summary>Pre-formatted as "yyyy-MM-dd HH:mm" — set once, never mutated.</summary>
    public string timestamp;

    /// <summary>Raw username with no markup.</summary>
    public string username;

    /// <summary>Raw message body with no markup.</summary>
    public string content;

    // -------------------------------------------------------------------------
    // Static factory — avoids calling DateTime.Now and string.Format in multiple
    // places. Centralises formatting so it can be changed in one spot.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a ChatMessage stamped with the current local time.
    /// Called only from the main thread (DateTime.Now is not thread-safe on Mono).
    /// </summary>
    public static ChatMessage Create(string username, string content)
    {
        // DateTime.Now.ToString allocates one string. Unavoidable.
        // We do NOT cache the formatted timestamp on a StringBuilder here
        // because this runs at human typing speed (~once per second at most),
        // so the single alloc is irrelevant.
        return new ChatMessage
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            username  = username,
            content   = content
        };
    }
}
