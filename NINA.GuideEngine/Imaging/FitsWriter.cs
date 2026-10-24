// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Imaging;

/// <summary>
/// Minimal FITS writer for 16-bit images: a primary HDU followed by IMAGE extensions, each with its own keywords
/// (BZERO 32768, so unsigned 16-bit pixels round-trip exactly). Keyword values are written as given: numbers and
/// logicals as they are, strings quoted with <see cref="Quote"/>.
/// </summary>
public static class FitsWriter
{
    public const int BlockSize = 2880;
    private const int CardSize = 80;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Writes frames as a multi-HDU 16-bit FITS file (primary + IMAGE extensions) with extra keywords per HDU.</summary>
    public static void Write(Stream stream, IReadOnlyList<(GuideFrame Frame, IReadOnlyDictionary<string, string> Keywords)> hdus)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hdus);
        for (int h = 0; h < hdus.Count; h++)
        {
            var (f, kw) = hdus[h];
            WriteHdu(stream, f.Width, f.Height, f.Pixels, kw, primary: h == 0);
        }
    }

    /// <summary>
    /// Writes one 16-bit image HDU at the current position of <paramref name="stream"/>: the primary HDU when
    /// <paramref name="primary"/>, else an IMAGE extension. Returns the length of its header (the data follows it) and of
    /// the whole HDU.
    /// </summary>
    public static (int HeaderBytes, long TotalBytes) WriteHdu(Stream stream, int width, int height, ReadOnlySpan<ushort> pixels, IEnumerable<KeyValuePair<string, string>>? keywords,
        bool primary)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int n = checked(width * height);
        if (pixels.Length < n)
        {
            throw new ArgumentException("pixel buffer smaller than the image", nameof(pixels));
        }

        var cards = new List<string>
        {
            primary ? Card("SIMPLE", "T") : Card("XTENSION", "'IMAGE   '"),
            Card("BITPIX", "16"),
            Card("NAXIS", "2"),
            Card("NAXIS1", width.ToString(Inv)),
            Card("NAXIS2", height.ToString(Inv)),
        };
        cards.Add(primary ? Card("EXTEND", "T") : Card("PCOUNT", "0"));
        if (!primary)
        {
            cards.Add(Card("GCOUNT", "1"));
        }

        cards.Add(Card("BZERO", "32768"));
        cards.Add(Card("BSCALE", "1"));
        foreach (var (k, v) in keywords ?? [])
        {
            cards.Add(Card(k, v));
        }

        cards.Add("END".PadRight(CardSize));
        var header = string.Concat(cards);
        header = header.PadRight(Padded(header.Length));
        stream.Write(Encoding.ASCII.GetBytes(header));

        var buf = new byte[Padded(n * 2)];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(i * 2, 2), (short)(pixels[i] - 32768));
        }

        stream.Write(buf);
        return (header.Length, header.Length + (long)buf.Length);
    }

    /// <summary>Bytes an HDU of this size with <paramref name="keywords"/> extra keywords takes at most (header and padded data).</summary>
    public static long HduSize(int width, int height, int keywords) => Padded((10 + keywords) * CardSize) + Padded((long)width * height * 2);

    /// <summary>A FITS string value: quoted, inner quotes doubled, at most 68 characters, ASCII only.</summary>
    public static string Quote(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text ?? string.Empty)
        {
            string s = c == '\'' ? "''" : c is >= ' ' and <= '~' ? c.ToString() : "?";
            if (sb.Length + s.Length > 66)
            {
                break;
            }

            sb.Append(s);
        }

        return "'" + sb.ToString().PadRight(8) + "'";
    }

    /// <summary>A FITS number value (invariant culture, round-trip precision).</summary>
    public static string Number(double value) => double.IsFinite(value) ? value.ToString("R", Inv) : "0";

    public static string Logical(bool value) => value ? "T" : "F";

    private static int Padded(int length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    private static long Padded(long length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    private static string Card(string key, string value)
    {
        key = key.Length > 8 ? key[..8] : key;
        var card = value.StartsWith('\'') ? $"{key,-8}= {value}" : $"{key,-8}= {value,20}";
        return card.Length > CardSize ? card[..CardSize] : card.PadRight(CardSize);
    }
}
