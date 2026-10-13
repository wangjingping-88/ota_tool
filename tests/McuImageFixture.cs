using System.Buffers.Binary;

// 测试自行生成构建格式，并可在重算 CRC 后注入结构错误。
static class McuImageFixture
{
    public static byte[] App(int length = 0x2200, byte version = 2,
        uint stack = 0x20008000, uint reset = 0x08008209)
    {
        var bytes = new byte[length];
        if (length < 520) return bytes;
        "IPMAPP01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 64);
        Put(bytes, 12, version);
        "IPM_GD32L233"u8.CopyTo(bytes.AsSpan(16));
        Put(bytes, 32, 0x08008000);
        Put(bytes, 36, 0x200);
        Put(bytes, 40, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(50), 2);
        bytes.AsSpan(64, 448).Fill(0xFF);
        Put(bytes, 512, stack);
        Put(bytes, 516, reset);
        if (length >= 0x2000)
            new byte[] { (byte)'I', (byte)'P', (byte)'M', 3, version }.CopyTo(bytes, 0x1F00);
        Seal(bytes);
        return bytes;
    }

    public static byte[] Factory(byte[] app)
    {
        var bytes = new byte[0x8000 + app.Length];
        bytes.AsSpan(0, 0x8000).Fill(0xFF);
        Put(bytes, 0, 0x20008000);
        Put(bytes, 4, 0x08000101);
        app.CopyTo(bytes, 0x8000);
        return bytes;
    }

    public static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    public static void Seal(byte[] bytes)
    {
        Put(bytes, 44, Crc(bytes.AsSpan(64)));
        Put(bytes, 60, Crc(bytes.AsSpan(0, 60)));
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xEDB88320;
            table[i] = value;
        }
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = table[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
}
