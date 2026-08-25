using System.Globalization;

namespace OtaTool.Core.Protocols;

public static class ProtocolVersionFormatter
{
    public static bool IsKnown(byte version) => version is >= 1 and <= 254;

    public static string Format(byte version)
        => IsKnown(version)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{version / 10}.{version % 10}")
            : "未知";

    public static string FormatWithPrefix(byte version)
        => IsKnown(version) ? $"v{Format(version)}" : "未知版本";

    public static string FormatRaw(string? version)
        => byte.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            ? Format(numeric)
            : "未知";
}
