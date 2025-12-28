namespace NanoLink.Ring;

internal static class Throw
{
  public static void Argument(string paramName, string message) =>
    throw new ArgumentException(message, paramName);

  public static void Invalid(string message) =>
    throw new InvalidOperationException(message);
}
