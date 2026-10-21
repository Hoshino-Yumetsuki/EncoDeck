// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Incremental stream de-framer. RFCOMM delivers an ordered byte stream with no message
/// boundaries, so frames may arrive split across reads (half packets) or several per read
/// (sticky packets), and large packets may be fragmented over several link frames.
/// Garbage and inconsistent headers are skipped one byte at a time.
/// Not thread-safe: feed it from a single read loop.
/// </summary>
public sealed class OppoFrameDecoder
{
    // Accept somewhat more than the documented 660-byte maximum so unusual firmware does not
    // desynchronize us, but stay small enough that a corrupt length resyncs quickly.
    private const int MaxAcceptedLen = 1024;

    private readonly byte[] _buffer = new byte[MaxAcceptedLen * 4];
    private int _count;

    // Fragment reassembly.
    private readonly List<byte> _reassembly = [];
    private int _nextFsn = -1;

    /// <summary>Total bytes discarded while resynchronizing (for diagnostics).</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Fragmented packets dropped because a fragment was missing or out of order.</summary>
    public long DroppedFragmentedPackets { get; private set; }

    /// <summary>Bytes currently buffered waiting for the rest of a frame.</summary>
    public int PendingBytes => _count;

    public IReadOnlyList<OppoFrame> Push(ReadOnlySpan<byte> data)
    {
        var frames = new List<OppoFrame>();
        while (!data.IsEmpty)
        {
            int take = Math.Min(data.Length, _buffer.Length - _count);
            data[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            data = data[take..];
            Drain(frames);
        }
        return frames;
    }

    public void Reset()
    {
        _count = 0;
        _reassembly.Clear();
        _nextFsn = -1;
    }

    private void Drain(List<OppoFrame> frames)
    {
        int offset = 0;
        while (true)
        {
            int magic = _buffer.AsSpan(offset, _count - offset).IndexOf(OppoFrame.Magic);
            if (magic < 0)
            {
                DiscardedBytes += _count - offset;
                offset = _count;
                break;
            }
            DiscardedBytes += magic;
            offset += magic;

            var result = TryReadFrame(_buffer.AsSpan(offset, _count - offset), out int consumed, out byte ctrl, out int fsn, out var chunk);
            if (result == ReadResult.NeedMore) break;
            if (result == ReadResult.Invalid)
            {
                offset++;
                DiscardedBytes++;
                continue;
            }

            if (ctrl == OppoLink.CtrlSingle)
            {
                if (OppoFrame.TryParsePacket(chunk, out var frame))
                {
                    frames.Add(frame);
                }
                else
                {
                    // Header looked right but the packet is inconsistent: treat as a false sync.
                    offset++;
                    DiscardedBytes++;
                    continue;
                }
            }
            else
            {
                AcceptFragment(ctrl, fsn, chunk, frames);
            }
            offset += consumed;
        }

        if (offset > 0)
        {
            int remaining = _count - offset;
            if (remaining > 0) Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
            _count = remaining;
        }
    }

    private enum ReadResult
    {
        Ok,
        NeedMore,
        Invalid,
    }

    private static ReadResult TryReadFrame(ReadOnlySpan<byte> span, out int consumed, out byte ctrl, out int fsn, out ReadOnlySpan<byte> chunk)
    {
        consumed = 0;
        ctrl = 0;
        fsn = -1;
        chunk = default;
        if (span.Length < 2) return ReadResult.NeedMore;

        int len;
        int lenBytes;
        if ((span[1] & 0x80) == 0)
        {
            len = span[1];
            lenBytes = 1;
        }
        else
        {
            if (span.Length < 3) return ReadResult.NeedMore;
            if ((span[2] & 0x80) != 0) return ReadResult.Invalid; // at most 2 varint bytes
            len = (span[1] & 0x7F) | (span[2] << 7);
            lenBytes = 2;
            if (len < 0x80) return ReadResult.Invalid; // non-canonical encoding
        }
        if (len < 2 || len > MaxAcceptedLen) return ReadResult.Invalid;

        int headerEnd = 1 + lenBytes;
        if (span.Length < headerEnd + 2) return ReadResult.NeedMore;
        byte control = span[headerEnd];
        if ((control & ~0x03) != 0) return ReadResult.Invalid;
        ctrl = (byte)(control & 0x03);

        if (ctrl == OppoLink.CtrlSingle)
        {
            if (len < 2 + OppoFrame.PacketHeaderLength) return ReadResult.Invalid;
            // Cross-check the packet's own length field before trusting LEN, so a stray 0xAA inside a
            // payload cannot make us swallow the frame that follows.
            if (span.Length < headerEnd + 2 + OppoFrame.PacketHeaderLength) return ReadResult.NeedMore;
            int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(headerEnd + 2 + 3)..]);
            if (payloadLength != len - 2 - OppoFrame.PacketHeaderLength) return ReadResult.Invalid;
        }
        else
        {
            // Fragments carry no inner length we can check up front; require the reserved byte to be 0.
            if (len < 4 || span[headerEnd + 1] != 0x00) return ReadResult.Invalid;
        }

        int total = headerEnd + len;
        if (span.Length < total) return ReadResult.NeedMore;

        int dataStart = headerEnd + 2;
        if (ctrl != OppoLink.CtrlSingle)
        {
            fsn = span[dataStart];
            dataStart++;
        }
        chunk = span[dataStart..total];
        consumed = total;
        return ReadResult.Ok;
    }

    private void AcceptFragment(byte ctrl, int fsn, ReadOnlySpan<byte> chunk, List<OppoFrame> frames)
    {
        if (ctrl == OppoLink.CtrlFirst)
        {
            if (_nextFsn >= 0) DroppedFragmentedPackets++;
            _reassembly.Clear();
            _reassembly.AddRange(chunk);
            _nextFsn = (fsn + 1) & 0xFF;
            return;
        }

        if (_nextFsn < 0 || fsn != _nextFsn || _reassembly.Count + chunk.Length > OppoFrame.MaxPayloadLength + OppoFrame.PacketHeaderLength)
        {
            // Missing first fragment, gap, or runaway packet.
            DroppedFragmentedPackets++;
            _reassembly.Clear();
            _nextFsn = -1;
            return;
        }

        _reassembly.AddRange(chunk);
        _nextFsn = (fsn + 1) & 0xFF;
        if (ctrl == OppoLink.CtrlLast)
        {
            if (OppoFrame.TryParsePacket(_reassembly.ToArray(), out var frame))
            {
                frames.Add(frame);
            }
            else
            {
                DroppedFragmentedPackets++;
            }
            _reassembly.Clear();
            _nextFsn = -1;
        }
    }
}
