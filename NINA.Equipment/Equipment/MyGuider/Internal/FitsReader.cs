// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using NINA.GuideEngine.Core;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// Minimal in-memory FITS reader for guide frames delivered as INDI BLOBs (no temp files, no native code).
/// Supports the primary HDU with BITPIX 8, 16 (with BZERO 32768) and 32/-32 (scaled to 16 bit), NAXIS 2
/// (or NAXIS 3 with a single plane), and zlib-compressed BLOBs (".fits.z"). Writing is the engine's
/// <see cref="GuideEngine.Imaging.FitsWriter"/>.
/// </summary>
internal static class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public static GuideFrame Read(byte[] data, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (format is not null && format.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
        {
            data = Decompress(data);
        }

        int offset = 0;
        return ReadHdu(data, ref offset, out _) ?? throw new InvalidDataException("FITS primary HDU has no image");
    }

    /// <summary>Reads every image HDU (primary and IMAGE extensions) with its header keywords.</summary>
    public static IReadOnlyList<(GuideFrame Frame, IReadOnlyDictionary<string, string> Header)> ReadAll(byte[] data)
    {
        var result = new List<(GuideFrame, IReadOnlyDictionary<string, string>)>();
        int offset = 0;
        while (offset + BlockSize <= data.Length)
        {
            var frame = ReadHdu(data, ref offset, out var header);
            if (frame is not null)
            {
                result.Add((frame, header));
            }
        }

        return result;
    }

    private static GuideFrame? ReadHdu(byte[] data, ref int offset, out IReadOnlyDictionary<string, string> headerOut)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headerOut = header;
        bool end = false;
        while (!end)
        {
            if (offset + BlockSize > data.Length)
            {
                throw new InvalidDataException("FITS header truncated");
            }

            for (int c = 0; c < BlockSize / CardSize; c++)
            {
                string card = System.Text.Encoding.ASCII.GetString(data, offset + c * CardSize, CardSize);
                string key = card[..8].Trim();
                if (key == "END")
                {
                    end = true;
                    break;
                }

                if (card.Length > 9 && card[8] == '=')
                {
                    string value = card[10..];
                    int slash = value.IndexOf('/');
                    if (slash >= 0 && !value.TrimStart().StartsWith('\''))
                    {
                        value = value[..slash];
                    }

                    header[key] = value.Trim().Trim('\'').Trim();
                }
            }

            offset += BlockSize;
        }

        int bitpix = Int(header, "BITPIX");
        int naxis = Int(header, "NAXIS");
        long dataBytes = 0;
        if (naxis > 0)
        {
            dataBytes = Math.Abs(bitpix) / 8;
            for (int a = 1; a <= naxis; a++)
            {
                dataBytes *= Int(header, "NAXIS" + a.ToString(CultureInfo.InvariantCulture));
            }
        }

        int dataStart = offset;
        offset += (int)((dataBytes + BlockSize - 1) / BlockSize * BlockSize);
        if (naxis < 2)
        {
            return null;
        }

        int width = Int(header, "NAXIS1");
        int height = Int(header, "NAXIS2");
        double bzero = Dbl(header, "BZERO", 0);
        double bscale = Dbl(header, "BSCALE", 1);
        int n = checked(width * height);
        int bytesPer = Math.Abs(bitpix) / 8;
        if (dataStart + (long)n * bytesPer > data.Length)
        {
            throw new InvalidDataException("FITS data truncated");
        }

        var pixels = new ushort[n];
        var src = data.AsSpan(dataStart);
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < n; i++)
                {
                    pixels[i] = (ushort)Math.Clamp(src[i] * bscale + bzero, 0, 65535);
                }

                break;
            case 16:
                if (bscale == 1 && bzero == 32768)
                {
                    for (int i = 0; i < n; i++)
                    {
                        pixels[i] = (ushort)(BinaryPrimitives.ReadInt16BigEndian(src.Slice(i * 2, 2)) + 32768);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        pixels[i] = (ushort)Math.Clamp(BinaryPrimitives.ReadInt16BigEndian(src.Slice(i * 2, 2)) * bscale + bzero, 0, 65535);
                    }
                }

                break;
            case 32:
                for (int i = 0; i < n; i++)
                {
                    pixels[i] = (ushort)Math.Clamp(BinaryPrimitives.ReadInt32BigEndian(src.Slice(i * 4, 4)) * bscale + bzero, 0, 65535);
                }

                break;
            case -32:
                for (int i = 0; i < n; i++)
                {
                    float f = BinaryPrimitives.ReadSingleBigEndian(src.Slice(i * 4, 4));
                    double v = f * bscale + bzero;
                    pixels[i] = (ushort)Math.Clamp(v <= 1.0 ? v * 65535.0 : v, 0, 65535);
                }

                break;
            default:
                throw new InvalidDataException($"FITS BITPIX={bitpix} not supported");
        }

        return new GuideFrame(width, height, pixels) { BitsPerPixel = bitpix == 8 ? 8 : 16 };
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 3);
        z.CopyTo(output);
        return output.ToArray();
    }

    private static int Int(Dictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new InvalidDataException($"FITS keyword {key} missing");

    private static double Dbl(Dictionary<string, string> h, string key, double def) =>
        h.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
}
