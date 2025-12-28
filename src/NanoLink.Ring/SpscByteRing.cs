using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace NanoLink.Ring;

/// <summary>
/// Single-producer / single-consumer variable-size message ring stored in shared memory.
/// Producer and consumer must share the same <see cref="SharedMemoryRingMap"/> mapping.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SpscByteRing
{
  // Special record markers.
  private const int WrapMarker = int.MinValue;

  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  private struct RecordHeader
  {
    public int Length; // bytes. >0 committed. <0 in-progress. WrapMarker=int.MinValue.
    public int Type;
    public long TimestampQpc;
    public int Sequence;
    public int Reserved0; // pad to 24 bytes
  }

  private static readonly int RecordHeaderBytes = SharedRingLayout.AlignUp8(sizeof(RecordHeader));

  private readonly SharedMemoryRingMap _map;
  private readonly SharedRingLayout.Header* _hdr;
  private readonly byte* _ring;
  private readonly int _cap;

  public int CapacityBytes => _cap;

  public SpscByteRing(SharedMemoryRingMap map)
  {
    _map = map;
    _hdr = map.Header;
    _ring = map.RingBase;
    _cap = map.CapacityBytes;

    if (!SharedRingLayout.IsPowerOfTwo(_cap))
      Throw.Invalid("Ring capacity must be power-of-two.");
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int AlignUp8(int x) => SharedRingLayout.AlignUp8(x);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int Offset(long absoluteBytes) => SharedRingLayout.Offset(_cap, absoluteBytes);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int RemainingToEnd(int offset) => _cap - offset;

  public long DroppedWrites => Volatile.Read(ref _hdr->DroppedWrites);

  /// <summary>
  /// Attempts to write one message. Returns false if there is not enough space (drop-newest policy).
  /// </summary>
  public bool TryWrite(ReadOnlySpan<byte> payload, int type, long timestampQpc, int sequence)
  {
    int payloadLen = payload.Length;
    if (payloadLen <= 0)
      Throw.Argument(nameof(payload), "payload must be non-empty.");

    int total = AlignUp8(RecordHeaderBytes + payloadLen);
    if (total > _cap / 2)
      Throw.Argument(nameof(payload), "payload too large for ring (must be <= capacity/2).");

    long head = Volatile.Read(ref _hdr->HeadBytes);
    long tail = Volatile.Read(ref _hdr->TailBytes); // acquire

    long used = head - tail;
    if (used < 0 || used > _cap)
      Throw.Invalid("Ring indices corrupted (head/tail invariant violated).");

    if ((_cap - (int)used) < total)
    {
      // Full: drop newest.
      Interlocked.Increment(ref _hdr->DroppedWrites);
      return false;
    }

    int off = Offset(head);
    int rem = RemainingToEnd(off);

    // If we can't even fit a header in the remaining bytes, pad to end.
    if (rem < RecordHeaderBytes)
    {
      long pad = rem;
      Volatile.Write(ref _hdr->HeadBytes, head + pad);
      head += pad;
      off = 0;
      rem = _cap;
    }

    // If the record doesn't fit contiguously, insert wrap marker (consumes rem bytes).
    if (rem < total)
    {
      // Write wrap marker header (committed).
      var wrap = (RecordHeader*)(_ring + off);
      wrap->Type = 0;
      wrap->TimestampQpc = 0;
      wrap->Sequence = 0;
      Volatile.Write(ref wrap->Length, WrapMarker);

      // Publish that these bytes are consumed (jump to start).
      Volatile.Write(ref _hdr->HeadBytes, head + rem);

      head += rem;
      off = 0;
      rem = _cap;

      // Re-check space with updated head; tail hasn't changed (SPSC) but keep it safe.
      tail = Volatile.Read(ref _hdr->TailBytes);
      used = head - tail;
      if ((_cap - (int)used) < total)
      {
        Interlocked.Increment(ref _hdr->DroppedWrites);
        return false;
      }
    }

    // Write record with two-phase commit: negative length -> payload -> positive length.
    var rec = (RecordHeader*)(_ring + off);
    rec->Type = type;
    rec->TimestampQpc = timestampQpc;
    rec->Sequence = sequence;
    rec->Reserved0 = 0;
    Volatile.Write(ref rec->Length, -payloadLen); // mark in-progress

    // Copy payload just after header.
    byte* payloadDst = (byte*)rec + RecordHeaderBytes;
    payload.CopyTo(new Span<byte>(payloadDst, payloadLen));

    // Commit record.
    Volatile.Write(ref rec->Length, payloadLen);

    // Publish new head (release).
    Volatile.Write(ref _hdr->HeadBytes, head + total);
    return true;
  }

  /// <summary>
  /// Attempts to read one message into <paramref name="dest"/>.
  /// Returns false if no committed message is available (or if the next message is larger than dest).
  /// </summary>
  public bool TryRead(Span<byte> dest, out int type, out long timestampQpc, out int sequence, out int payloadLen)
  {
    type = 0;
    timestampQpc = 0;
    sequence = 0;
    payloadLen = 0;

    long tail = Volatile.Read(ref _hdr->TailBytes);
    long head = Volatile.Read(ref _hdr->HeadBytes); // acquire

    if (tail == head)
      return false;

    int off = Offset(tail);
    int rem = RemainingToEnd(off);

    // If we can't even fit a header, skip padding to the end.
    if (rem < RecordHeaderBytes)
    {
      Volatile.Write(ref _hdr->TailBytes, tail + rem);
      return false;
    }

    var rec = (RecordHeader*)(_ring + off);
    int len = Volatile.Read(ref rec->Length); // acquire-ish

    if (len == WrapMarker)
    {
      // Jump to start by consuming the remaining bytes in this cycle.
      Volatile.Write(ref _hdr->TailBytes, tail + rem);
      return false;
    }

    if (len < 0)
    {
      // In-progress (should be rare with our publish order); treat as not available yet.
      return false;
    }

    payloadLen = len;
    if (payloadLen <= 0)
      Throw.Invalid("Corrupt record length.");

    int total = AlignUp8(RecordHeaderBytes + payloadLen);
    if (total > rem)
      Throw.Invalid("Corrupt record (spans end without wrap marker).");

    if (payloadLen > dest.Length)
      return false;

    type = rec->Type;
    timestampQpc = rec->TimestampQpc;
    sequence = rec->Sequence;

    byte* payloadSrc = (byte*)rec + RecordHeaderBytes;
    new ReadOnlySpan<byte>(payloadSrc, payloadLen).CopyTo(dest);

    // Consume.
    Volatile.Write(ref _hdr->TailBytes, tail + total);
    return true;
  }
}


