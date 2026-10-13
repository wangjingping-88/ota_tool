namespace OtaTool.Core.Models;

/// <summary>三个 IPM 工程共用的 BIN 身份位置和字段顺序；不使用 ECO 类型表。</summary>
public sealed record IpmImageIdentity(byte DeviceTypeCode, byte Version)
{
    public const int Offset = 0x9F00;
    public const int Length = 5;
    public const byte Gateway = 8;
    public const byte Wiota = 2;
    public const byte Mcu = 3;

    public static IpmImageIdentity Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length < Offset + Length)
            throw new InvalidDataException("IPM 镜像长度不足，未覆盖统一标识偏移 0x9F00。");
        return ParseMarker(image.Slice(Offset, Length));
    }

    public static IpmImageIdentity ParseMarker(ReadOnlySpan<byte> marker)
    {
        if (marker.Length != Length || !marker[..3].SequenceEqual("IPM"u8))
            throw new InvalidDataException("未识别到偏移 0x9F00 的 IPM+type+version，请使用新规则重新构建固件。");
        if (marker[3] is not (Gateway or Wiota or Mcu) || marker[4] is < 1 or > 254)
            throw new InvalidDataException("IPM 类型或版本无效：网关=8、WIoTa=2、MCU=3，软件版本=1～254。");
        return new(marker[3], marker[4]);
    }
}
