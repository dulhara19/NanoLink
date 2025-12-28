using System.Buffers;

namespace SensorConsumerApp;

internal sealed class LatencyStats : IDisposable
{
  private readonly long[] _buf;
  private int _count;

  public LatencyStats(int capacity)
  {
    if (capacity <= 0) capacity = 1;
    _buf = ArrayPool<long>.Shared.Rent(capacity);
    _count = 0;
  }

  public void Add(long value)
  {
    if (_count < _buf.Length)
      _buf[_count++] = value;
  }

  public (long p50, long p95, long p99, int n) SnapshotPercentiles()
  {
    int n = _count;
    if (n <= 0) return (0, 0, 0, 0);

    Array.Sort(_buf, 0, n);
    long p50 = _buf[(int)Math.Floor(0.50 * (n - 1))];
    long p95 = _buf[(int)Math.Floor(0.95 * (n - 1))];
    long p99 = _buf[(int)Math.Floor(0.99 * (n - 1))];
    return (p50, p95, p99, n);
  }

  public void Reset() => _count = 0;

  public void Dispose()
  {
    ArrayPool<long>.Shared.Return(_buf);
  }
}


