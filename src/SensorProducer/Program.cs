using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using NanoLink.Ring;

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

  private static bool HasFlag(string[] args, string name)
    => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

  public static unsafe int Main(string[] args)
  {
    if (!OperatingSystem.IsWindows())
    {
      Console.Error.WriteLine("This demo is Windows-only (MemoryMappedFile IPC).");
      return 1;
    }

    // Defaults
    string mapName = GetStringArg(args, "--map", @"Local\NanoLink.SensorRing");
    int capacityBytes = GetIntArg(args, "--cap", 1 << 20); // 1 MiB
    int rate = GetIntArg(args, "--rate", 50_000); // msgs/sec
    int minSize = GetIntArg(args, "--min", 32);
    int maxSize = GetIntArg(args, "--max", 512);
    bool lossless = HasFlag(args, "--lossless"); // spin/yield until write succeeds

    if (minSize < 1) minSize = 1;
    if (maxSize < minSize) maxSize = minSize;

    Console.WriteLine($"Producer map={mapName} cap={capacityBytes} rate={rate}/s size=[{minSize},{maxSize}] lossless={lossless}");

    using var map = SharedMemoryRingMap.CreateOrOpen(mapName, capacityBytes, initializeIfNeeded: true);
    var ring = new SpscByteRing(map);

    var pool = ArrayPool<byte>.Shared;
    var rng = new Random(1234);

    long ticksPerSec = Stopwatch.Frequency;
    long start = Stopwatch.GetTimestamp();
    long nextReport = start + ticksPerSec;
    long sent = 0;
    long droppedAtStart = ring.DroppedWrites;
    int seq = 0;

    // Simple rate control: target interval in ticks.
    long interval = rate > 0 ? ticksPerSec / rate : 0;
    long nextSend = start;

    while (true)
    {
      long now = Stopwatch.GetTimestamp();

      if (rate > 0 && now < nextSend)
      {
        Thread.Yield();
        continue;
      }

      int size = rng.Next(minSize, maxSize + 1);
      byte[] buf = pool.Rent(size);
      try
      {
        if (size >= 16)
        {
          Buffer.BlockCopy(BitConverter.GetBytes(rng.Next(1, 33)), 0, buf, 0, 4);
          Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, buf, 4, 4);
          Buffer.BlockCopy(BitConverter.GetBytes(now), 0, buf, 8, 8);
          for (int i = 16; i < size; i++)
            buf[i] = (byte)rng.Next(0, 256);
        }
        else
        {
          for (int i = 0; i < size; i++)
            buf[i] = (byte)rng.Next(0, 256);
        }

        long ts = Stopwatch.GetTimestamp();
        bool ok;
        do
        {
          ok = ring.TryWrite(new ReadOnlySpan<byte>(buf, 0, size), type: 1, timestampQpc: ts, sequence: seq);
          if (!ok && lossless)
            Thread.Yield();
        } while (!ok && lossless);

        if (ok)
        {
          sent++;
          seq++;
        }
      }
      finally
      {
        pool.Return(buf);
      }

      if (rate > 0)
        nextSend += interval;

      if (now >= nextReport)
      {
        long dropped = ring.DroppedWrites - droppedAtStart;
        double seconds = (double)(now - start) / ticksPerSec;
        double msgPerSec = sent / Math.Max(1e-9, seconds);
        long usedBytes = Volatile.Read(ref map.Header->HeadBytes) - Volatile.Read(ref map.Header->TailBytes);
        Console.WriteLine($"sent={sent} rate={msgPerSec:F0}/s dropped={dropped} used={usedBytes}B");

        nextReport += ticksPerSec;
      }
    }
  }
}
