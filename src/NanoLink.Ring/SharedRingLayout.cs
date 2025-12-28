using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NanoLink.Ring;

public static class SharedRingLayout
{
  public const uint Magic = 0x4B4E4C4E; // 'N''L''N''K' (NanoLink)
  public const uint Version = 1;

  public const int CacheLineBytes = 64;

  // Keep header size a multiple of cache line.
  public const int HeaderBytes = 256;

  public const int MinCapacityBytes = 1 << 12; // 4 KiB
  public const int MaxCapacityBytes = 1 << 28; // 256 MiB

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static bool IsPowerOfTwo(int x) => (x > 0) && ((x & (x - 1)) == 0);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int AlignUp8(int x) => (x + 7) & ~7;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int Offset(int capacityBytes, long absoluteBytes)
  {
    // capacityBytes must be power-of-two.
    return (int)(absoluteBytes & (capacityBytes - 1));
  }

  [StructLayout(LayoutKind.Explicit, Pack = 1, Size = HeaderBytes)]
  public unsafe struct Header
  {
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public uint Version;
    [FieldOffset(8)] public int CapacityBytes; // power-of-two
    [FieldOffset(12)] public int Reserved0;

    // Put head/tail/dropped on separate cache lines to reduce false sharing.
    [FieldOffset(CacheLineBytes * 1)] public long HeadBytes; // producer writes, consumer reads
    [FieldOffset(CacheLineBytes * 2)] public long TailBytes; // consumer writes, producer reads
    [FieldOffset(CacheLineBytes * 3)] public long DroppedWrites; // producer increments when full
  }
}


