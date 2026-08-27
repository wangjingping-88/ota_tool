using System.Globalization;

namespace OtaTool.Core.Protocols;

public static class ProtocolIdentifierFormatter
{
    public static string Format(ushort value)
        => string.Create(CultureInfo.InvariantCulture, $"{value}（0x{value:X4}）");

    public static string Format(uint value)
        => value <= ushort.MaxValue
            ? string.Create(CultureInfo.InvariantCulture, $"{value}（0x{value:X4}）")
            : string.Create(CultureInfo.InvariantCulture, $"{value}（0x{value:X8}）");
}
