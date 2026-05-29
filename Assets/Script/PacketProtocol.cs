// =============================================================================
// PacketProtocol.cs
// Length-prefixed packet framing over a raw TCP byte stream.
//
// THE CORE TCP PROBLEM
//   TCP is a byte stream, not a message stream. A single ReadAsync call may
//   return any of the following:
//     - Part of one message          ("Hell")
//     - Exactly one message          ("Hello")
//     - One message + part of next   ("Hello\x00\x05World")
//     - Multiple complete messages   ("Hello\x00\x05World\x00\x03Foo")
//   The original code assumed one ReadAsync == one message. This is wrong and
//   causes silent data corruption or crashes under any real load.
//
// OUR FRAMING PROTOCOL
//   Each packet on the wire is:
//     [ 4 bytes big-endian length ][ N bytes UTF-8 payload ]
//   The reader accumulates bytes into a ring buffer until it has a complete
//   length header, then accumulates until it has the full payload, then yields.
//
//   WHY 4 BYTES (int)?
//     Supports messages up to ~2 GB. Overkill for chat, but means the framing
//     code never needs to change if we later add file transfer or game state.
//
//   WHY BIG-ENDIAN?
//     Network byte order convention (RFC 1700). Both ends agree without
//     configuration. BitConverter on x86 is little-endian, so we manually
//     compose/decompose bytes — two arithmetic ops, no alloc.
//
// GC STRATEGY
//   - All intermediate buffers come from ArrayPool<byte>.Shared.
//     ArrayPool maintains a per-thread cache of power-of-2-sized arrays.
//     Rent + Return = zero heap allocation for the buffer itself.
//   - Encoding.UTF8.GetBytes(string, ...) overload writes into a caller-supplied
//     buffer — no intermediate byte[] created.
//   - Encoding.UTF8.GetString(ReadOnlySpan<byte>) (Unity 2021+) or the
//     segment overload avoids an extra copy.
//   - The only unavoidable allocation per decoded message is the result string
//     itself — strings are immutable reference types in C#.
//
// THREAD SAFETY
//   PacketReader and PacketWriter are NOT shared across threads. Each
//   TcpClientManager owns one of each and uses them only from its receive/send
//   async continuations.
// =============================================================================

using System;
using System.Buffers;          // ArrayPool
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// =============================================================================
// PacketWriter — frames a string into a length-prefixed packet and sends it.
// =============================================================================
public sealed class PacketWriter
{
    // Reusable 4-byte header buffer. Allocated once per PacketWriter instance.
    // Safe because PacketWriter is not used concurrently.
    private readonly byte[] _header = new byte[4];

    /// <summary>
    /// Encodes <paramref name="message"/> as UTF-8, prepends a 4-byte big-endian
    /// length, and writes both to <paramref name="stream"/>.
    ///
    /// GC: One ArrayPool rent/return for the payload buffer.
    ///     One unavoidable alloc if the string is longer than the pooled
    ///     array — ArrayPool will allocate a new one, but it caches it for
    ///     next time.
    /// </summary>
    public async Task SendAsync(NetworkStream stream, string message,
                                CancellationToken ct = default)
    {
        if (stream == null || !stream.CanWrite) return;

        // --- Measure required byte count without allocating a byte[] ---
        int byteCount = Encoding.UTF8.GetByteCount(message);

        // --- Rent a buffer from the pool ---
        // ArrayPool rounds up to the next power of 2, so 100-char messages
        // will always hit the same bucket after the first call.
        byte[] payload = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            // Write UTF-8 bytes directly into the rented buffer. No intermediate
            // byte[] created.
            int written = Encoding.UTF8.GetBytes(message, 0, message.Length,
                                                  payload, 0);

            // --- Build 4-byte big-endian length header in-place ---
            _header[0] = (byte)(written >> 24);
            _header[1] = (byte)(written >> 16);
            _header[2] = (byte)(written >>  8);
            _header[3] = (byte)(written       );

            // Two writes. We could combine into one buffer for a single syscall,
            // but header (4 bytes) + payload typically fits in one TCP segment
            // anyway (Nagle algorithm), so the extra copy is not worth it for chat.
            await stream.WriteAsync(_header, 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, written, ct).ConfigureAwait(false);
        }
        finally
        {
            // ALWAYS return to pool, even if an exception was thrown.
            ArrayPool<byte>.Shared.Return(payload);
        }
    }
}

// =============================================================================
// PacketReader — reads exactly one framed message from the stream.
// =============================================================================
public sealed class PacketReader
{
    // Persistent 4-byte header buffer — allocated once, reused forever.
    private readonly byte[] _header = new byte[4];

    // Maximum allowed message size (4 MB). Guards against malformed or
    // malicious length headers that would cause ArrayPool to rent huge arrays.
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads exactly one length-prefixed packet from <paramref name="stream"/>.
    /// Blocks (awaits) until a complete packet arrives.
    ///
    /// Returns null if the connection closed cleanly (0 bytes read).
    /// Throws on stream errors or malformed packets.
    ///
    /// GC: One ArrayPool rent/return for the payload buffer.
    ///     One string allocation for the decoded result — unavoidable.
    /// </summary>
    public async Task<string> ReadOneAsync(NetworkStream stream,
                                           CancellationToken ct = default)
    {
        // --- Step 1: Read exactly 4 header bytes ---
        if (!await ReadExactAsync(stream, _header, 4, ct).ConfigureAwait(false))
            return null; // clean close

        // --- Step 2: Decode big-endian length ---
        int length = (_header[0] << 24)
                   | (_header[1] << 16)
                   | (_header[2] <<  8)
                   |  _header[3];

        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidOperationException(
                $"[PacketReader] Invalid packet length: {length}");

        // --- Step 3: Rent payload buffer and fill it exactly ---
        byte[] payload = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            if (!await ReadExactAsync(stream, payload, length, ct).ConfigureAwait(false))
                return null;

            // Decode UTF-8. One unavoidable string allocation.
            return Encoding.UTF8.GetString(payload, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // -------------------------------------------------------------------------
    // Reads EXACTLY 'count' bytes into 'buffer', looping over partial reads.
    // This is the correct TCP read pattern — ReadAsync may return fewer bytes
    // than requested even with data available (kernel buffer, segmentation).
    // Returns false only on clean EOF (0 bytes read).
    // -------------------------------------------------------------------------
    private static async Task<bool> ReadExactAsync(NetworkStream stream,
                                                    byte[] buffer, int count,
                                                    CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, ct)
                                   .ConfigureAwait(false);
            if (read == 0)
                return false; // connection closed cleanly
            offset += read;
        }
        return true;
    }
}
