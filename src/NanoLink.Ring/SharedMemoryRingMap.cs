using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NanoLink.Ring;

[SupportedOSPlatform("windows")]
public sealed unsafe class SharedMemoryRingMap : IDisposable
{
  private readonly MemoryMappedFile _mmf;
  private readonly MemoryMappedViewAccessor _view;
  private readonly SafeMemoryMappedViewHandle _handle;
  private byte* _basePtr;

  public SharedRingLayout.Header* Header { get; }
  public byte* RingBase { get; }
  public int CapacityBytes { get; }
  public long TotalBytes => SharedRingLayout.HeaderBytes + (long)CapacityBytes;

  private SharedMemoryRingMap(
    MemoryMappedFile mmf,
    MemoryMappedViewAccessor view,
    SafeMemoryMappedViewHandle handle,
    byte* basePtr,
    int capacityBytes)
  {
    _mmf = mmf;
    _view = view;
    _handle = handle;
    _basePtr = basePtr;
    CapacityBytes = capacityBytes;
    Header = (SharedRingLayout.Header*)_basePtr;
    RingBase = _basePtr + SharedRingLayout.HeaderBytes;
  }

  public static SharedMemoryRingMap CreateOrOpen(string mapName, int capacityBytes, bool initializeIfNeeded)
  {
    if (!SharedRingLayout.IsPowerOfTwo(capacityBytes))
      Throw.Argument(nameof(capacityBytes), "capacityBytes must be a power of two.");
    if (capacityBytes < SharedRingLayout.MinCapacityBytes || capacityBytes > SharedRingLayout.MaxCapacityBytes)
      Throw.Argument(nameof(capacityBytes), $"capacityBytes must be between {SharedRingLayout.MinCapacityBytes} and {SharedRingLayout.MaxCapacityBytes}.");

    long totalBytes = SharedRingLayout.HeaderBytes + (long)capacityBytes;

    // CreateOrOpen ensures both sides converge on the same mapping name.
    var mmf = MemoryMappedFile.CreateOrOpen(mapName, totalBytes, MemoryMappedFileAccess.ReadWrite);
    var view = mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);

    var handle = view.SafeMemoryMappedViewHandle;
    byte* ptr = null;
    handle.AcquirePointer(ref ptr);

    var map = new SharedMemoryRingMap(mmf, view, handle, ptr, capacityBytes);
    if (initializeIfNeeded)
      map.InitializeIfNeeded();
    else
      map.ValidateInitialized();

    return map;
  }

  private void InitializeIfNeeded()
  {
    // Note: initialization is racing-safe for our SPSC demo usage because
    // both sides will converge to the same values; still validate after.
    if (Header->Magic != SharedRingLayout.Magic || Header->Version != SharedRingLayout.Version || Header->CapacityBytes != CapacityBytes)
    {
      Header->Magic = SharedRingLayout.Magic;
      Header->Version = SharedRingLayout.Version;
      Header->CapacityBytes = CapacityBytes;

      // Reset counters.
      Header->HeadBytes = 0;
      Header->TailBytes = 0;
      Header->DroppedWrites = 0;
    }

    ValidateInitialized();
  }

  private void ValidateInitialized()
  {
    if (Header->Magic != SharedRingLayout.Magic)
      Throw.Invalid("Shared memory region has invalid magic. Wrong mapping name or stale layout.");
    if (Header->Version != SharedRingLayout.Version)
      Throw.Invalid($"Shared memory region has unsupported version {Header->Version} (expected {SharedRingLayout.Version}).");
    if (Header->CapacityBytes != CapacityBytes)
      Throw.Invalid($"Shared memory capacity mismatch: region={Header->CapacityBytes}, requested={CapacityBytes}.");
  }

  public void Dispose()
  {
    if (_basePtr != null)
    {
      // ReleasePointer must be called once per AcquirePointer.
      _handle.ReleasePointer();
      _basePtr = null;
    }

    _view.Dispose();
    _mmf.Dispose();
  }
}


