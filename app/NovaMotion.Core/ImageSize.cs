// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Buffers.Binary;

namespace NovaMotion.Core;

/// <summary>A picture's width and height from its file header (PNG, JPEG, WebP, BMP, GIF), without
/// decoding it: Enhance a photo's time estimate needs only the size.</summary>
public static class ImageSize
{
    public static (int W, int H)? Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[64 * 1024];
            var n = f.Read(head, 0, head.Length);
            return Parse(head.AsSpan(0, n), f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static (int, int)? Parse(ReadOnlySpan<byte> b, Stream rest)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return (BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..]));
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M')
            return (BinaryPrimitives.ReadInt32LittleEndian(b[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..])));
        if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            var kind = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4));
            if (kind == "VP8X") return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
            if (kind == "VP8 ") return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
            if (kind == "VP8L") { var v = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]); return ((int)(v & 0x3FFF) + 1, (int)((v >> 14) & 0x3FFF) + 1); }
            return null;
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // JPEG: walk the markers to the frame header (SOF0..SOF15, not DHT/JPG/DAC)
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var len = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
                i += 2 + len;
            }
        }
        return null;
    }
}
