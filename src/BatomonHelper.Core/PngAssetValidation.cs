using System.Buffers.Binary;

namespace BatomonHelper.Core;

internal static class PngAssetValidation
{
    public static void Validate(byte[] bytes, string url)
    {
        if (bytes.Length < 57 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) Fail();
        var offset = 8; bool header = false, data = false, end = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue || length > bytes.Length - offset - 12) Fail();
            var chunk = bytes.AsSpan(offset + 4, checked((int)length + 4));
            uint crc = 0xffffffff;
            foreach (var value in chunk)
            { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + (int)length, 4))) Fail();
            var kind = System.Text.Encoding.ASCII.GetString(chunk[..4]);
            if (!header && kind != "IHDR") Fail();
            if (kind == "IHDR")
            {
                if (header || length != 13) Fail();
                var width = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(4, 4));
                var height = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(8, 4));
                if (width is 0 or > 2048 || height is 0 or > 2048) Fail();
                header = true;
            }
            if (kind == "IDAT") data = true;
            offset += (int)length + 12;
            if (kind == "IEND") { if (length != 0) Fail(); end = true; break; }
        }
        if (!header || !data || !end || offset != bytes.Length) Fail();
        void Fail() => throw new InvalidDataException($"Invalid PNG asset: {url}");
    }
}
