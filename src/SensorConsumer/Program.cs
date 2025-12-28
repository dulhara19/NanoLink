using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using NanoLink.Ring;
using SensorConsumerApp;

[SupportedOSPlatform("windows")]
internal static class Program
{
  private static int GetIntArg(string[] args, string name, int fallback)
  {
    for (int i = 0; i < args.Length - 1; i++)
    {
      if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        return int.Parse(args[i + 1]);
    }
    return fallback;
  }

  private static string GetStringArg(string[] args, string name, string fallback)
  {
    for (int i = 0; i < args.Length - 1; i++)
    {
      if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        return args[i + 1];
    }
    return fallback;
  }

  public static int Main(string[] args)
  {
    if (!OperatingSystem.IsWindows())
    {
      Console.Error.WriteLine("This demo is Windows-only (MemoryMappedFile IPC).");
      return 1;
    }

    string mapName = GetStringArg(args, "--map", @"Local\NanoLink.SensorRing");
    int capacityBytes = GetIntArg(args, "--cap", 1 << 20);
    int maxMsg = GetIntArg(args, "--maxmsg", 4096);
    int reportHz = GetIntArg(args, "--reportHz", 1);
    int statsWindow = GetIntArg(args, "--window", 200_000); // samples to store per report

    if (maxMsg < 64) maxMsg = 64;
    if (reportHz < 1) reportHz = 1;
    if (statsWindow < 1024) statsWindow = 1024;

    Console.WriteLine($"Consumer map={mapName} cap={capacityBytes} maxMsg={maxMsg} reportHz={reportHz} window={statsWindow}");

    using var map = SharedMemoryRingMap.CreateOrOpen(mapName, capacityBytes, initializeIfNeeded: true);
    var ring = new SpscByteRing(map);

    // QPC conversion
    double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    var pool = ArrayPool<byte>.Shared;
    byte[] buf = pool.Rent(maxMsg);
    try
    {
      int expectedSeq = 0;
      long gaps = 0;
      long total = 0;

      using var stats = new LatencyStats(statsWindow);

      long ticksPerSec = Stopwatch.Frequency;
      long nextReport = Stopwatch.GetTimestamp() + (ticksPerSec / reportHz);
      long droppedBaseline = ring.DroppedWrites;

      while (true)
      {
        bool ok = ring.TryRead(buf, out int type, out long tsQpc, out int seq, out int len);
        if (!ok)
        {
          Thread.Yield();
        }
        else
        {
          total++;

          if (seq != expectedSeq)
          {
            if (seq > expectedSeq)
              gaps += (seq - expectedSeq);
            expectedSeq = seq + 1;
          }
          else
          {
            expectedSeq++;
          }

          long now = Stopwatch.GetTimestamp();
          long latencyTicks = now - tsQpc;
          long latencyNs = (long)(latencyTicks * nsPerTick);
          stats.Add(latencyNs);
        }

        long nowR = Stopwatch.GetTimestamp();
        if (nowR >= nextReport)
        {
          var (p50, p95, p99, n) = stats.SnapshotPercentiles();
          long dropped = ring.DroppedWrites - droppedBaseline;
          Console.WriteLine($"msgs={total} gaps={gaps} dropped={dropped} p50={p50}ns p95={p95}ns p99={p99}ns n={n}");

          stats.Reset();
          nextReport += (ticksPerSec / reportHz);
        }
      }
    }
    finally
    {
      pool.Return(buf);
    }
  }
}
