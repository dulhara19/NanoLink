using System.Runtime.Versioning;
using NanoLink.Ring;
using Spectre.Console;

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

  public static unsafe int Main(string[] args)
  {
    if (!OperatingSystem.IsWindows())
    {
      Console.Error.WriteLine("SensorMonitor is Windows-only (MemoryMappedFile IPC).");
      return 1;
    }

    string mapName = GetStringArg(args, "--map", @"Local\NanoLink.SensorRing");
    int capacityBytes = GetIntArg(args, "--cap", 1 << 20);

    using var map = SharedMemoryRingMap.CreateOrOpen(mapName, capacityBytes, initializeIfNeeded: false);

    long head = Volatile.Read(ref map.Header->HeadBytes);
    long tail = Volatile.Read(ref map.Header->TailBytes);
    long used = head - tail;
    long dropped = Volatile.Read(ref map.Header->DroppedWrites);

    var table = new Table().RoundedBorder().Title("NanoLink Sensor Ring (snapshot)");
    table.AddColumn("Metric");
    table.AddColumn("Value");
    table.AddRow("map", mapName);
    table.AddRow("capacityBytes", capacityBytes.ToString());
    table.AddRow("headBytes", head.ToString());
    table.AddRow("tailBytes", tail.ToString());
    table.AddRow("usedBytes", used.ToString());
    table.AddRow("droppedWrites", dropped.ToString());

    AnsiConsole.Write(table);
    return 0;
  }
}
