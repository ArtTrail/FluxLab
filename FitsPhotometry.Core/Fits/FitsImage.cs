using System;
using System.IO;

namespace FitsPhotometry.Core.Fits;

/// <summary>Minimal FITS image reader -- supports BITPIX 8/16/32/-32/-64 with BZERO/BSCALE.
/// Ported from TransitLab's FitsImageService.
///
/// v2.0.1: walks the HDU chain instead of only reading the primary header. Plenty of real files
/// carry an empty primary HDU (NAXIS=0) with the actual image in an IMAGE extension -- anything
/// unpacked from .fz looks like this -- and those previously loaded as 0x0 and displayed nothing
/// at all, with no error. Table extensions are skipped rather than misread: a BINTABLE also has
/// NAXIS=2, so treating "first HDU with NAXIS>=2" as the image would happily decode a table's
/// bytes as pixels and render garbage.
///
/// Tile-compressed (.fz) images live in a BINTABLE with ZIMAGE=T and are decoded via
/// <see cref="CompressedImage"/>: integer RICE_1/GZIP frames and float frames (RICE_1 with
/// SUBTRACTIVE_DITHER_1/2 or NO_DITHER quantization). HCOMPRESS/PLIO and other exotic variants are
/// not handled and open as empty rather than noise.
/// </summary>
public static class FitsImage
{
    public sealed record Loaded(float[] Pixels, int Width, int Height);

    private sealed record FitsMeta(
        int Bitpix, int Width, int Height, double Bzero, double Bscale,
        bool IsImage, long DataBytes,
        bool ZImage, System.Collections.Generic.Dictionary<string, string> Cards);

    /// <summary>Load the full first image plane as float pixels (BZERO/BSCALE applied), from the
    /// first HDU that actually holds a 2-D image -- primary or extension.</summary>
    public static Loaded Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        FitsMeta? meta = null;
        while (true)
        {
            var hdu = ReadMeta(fs);
            if (hdu is null) return new Loaded([], 0, 0);   // end of file, no image found
            if (hdu.IsImage && hdu.Width > 0 && hdu.Height > 0) { meta = hdu; break; }

            // Tile-compressed image (.fz): a BINTABLE with ZIMAGE=T. Decode it in place; if the
            // compression variant isn't supported, fall back to an empty image (never garbage).
            if (hdu.ZImage)
                return CompressedImage.TryDecode(fs, hdu.Cards, hdu.DataBytes) ?? new Loaded([], 0, 0);

            // Not an image HDU (or has no pixels) -- skip its data, padded to a 2880 boundary,
            // and try the next one.
            long skip = Pad2880(hdu.DataBytes);
            if (skip > 0 && fs.Seek(skip, SeekOrigin.Current) > fs.Length) return new Loaded([], 0, 0);
        }

        int w = meta.Width, h = meta.Height;
        int bytesPerPixel = Math.Abs(meta.Bitpix) / 8;
        int totalPixels = w * h;
        var raw = new byte[totalPixels * bytesPerPixel];
        fs.ReadExactly(raw);

        var pixels = new float[totalPixels];

        // Fast path: standard unsigned 16-bit (BITPIX=16, BZERO=32768, BSCALE=1).
        if (meta.Bitpix == 16 && meta.Bzero == 32768.0 && meta.Bscale == 1.0)
        {
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 2;
                short s = (short)((raw[o] << 8) | raw[o + 1]);
                pixels[i] = s + 32768f;
            }
        }
        else if (meta.Bitpix == 16 && meta.Bzero == 0.0 && meta.Bscale == 1.0)
        {
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 2;
                short s = (short)((raw[o] << 8) | raw[o + 1]);
                pixels[i] = s;
            }
        }
        else
        {
            for (int i = 0; i < totalPixels; i++)
            {
                double rv = ReadPixelValue(raw, i * bytesPerPixel, meta.Bitpix);
                pixels[i] = (float)(meta.Bzero + meta.Bscale * rv);
            }
        }

        return new Loaded(pixels, w, h);
    }

    /// <summary>Reads one HDU header unit starting at the stream's current position, leaving the
    /// stream positioned at the first byte of that HDU's data. Returns null at end of file.</summary>
    private static FitsMeta? ReadMeta(FileStream fs)
    {
        int bitpix = 0, width = 0, height = 0, naxis = 0;
        double bzero = 0.0, bscale = 1.0;
        long pcount = 0, gcount = 1;
        string xtension = "";
        bool isPrimary = true;
        bool sawAnyCard = false;
        bool zimage = false;

        // NAXISn beyond the first two still count toward the data size when skipping an HDU.
        var axes = new System.Collections.Generic.Dictionary<int, long>();
        // Every card (trimmed keyword -> raw 80-char card), so a compressed-image HDU can be handed
        // its full Z*/T* header without ReadMeta needing to know every keyword.
        var cards = new System.Collections.Generic.Dictionary<string, string>();

        var block = new byte[2880];
        var card = new byte[80];

        while (true)
        {
            if (!ReadFully(fs, block)) return sawAnyCard ? Build() : null;

            bool foundEnd = false;
            for (int c = 0; c < 36; c++)
            {
                Array.Copy(block, c * 80, card, 0, 80);
                var key = System.Text.Encoding.ASCII.GetString(card, 0, 8);

                if (key.StartsWith("END", StringComparison.Ordinal))
                { foundEnd = true; break; }

                var cardStr = System.Text.Encoding.ASCII.GetString(card, 0, 80);
                var trimKey = key.TrimEnd();
                if (trimKey.Length > 0) { sawAnyCard = true; cards[trimKey] = cardStr; }

                if      (trimKey == "ZIMAGE")   zimage = ParseCardString(cardStr) == "T";
                else if (trimKey == "BITPIX")   bitpix = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS")    naxis  = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS1") { width  = ParseCardInt(cardStr); axes[1] = width; }
                else if (trimKey == "NAXIS2") { height = ParseCardInt(cardStr); axes[2] = height; }
                else if (trimKey == "BZERO")    bzero  = ParseCardDouble(cardStr);
                else if (trimKey == "BSCALE")   bscale = ParseCardDouble(cardStr);
                else if (trimKey == "PCOUNT")   pcount = ParseCardInt(cardStr);
                else if (trimKey == "GCOUNT")   gcount = ParseCardInt(cardStr);
                else if (trimKey == "XTENSION") { xtension = ParseCardString(cardStr); isPrimary = false; }
                else if (trimKey.StartsWith("NAXIS", StringComparison.Ordinal)
                         && int.TryParse(trimKey.AsSpan(5), out var axisNo) && axisNo > 2)
                    axes[axisNo] = ParseCardInt(cardStr);
            }

            if (foundEnd) break;
        }

        return Build();

        FitsMeta Build()
        {
            // Only the primary array and IMAGE extensions hold pixels. A BINTABLE also reports
            // NAXIS=2 (row length x row count), so it must be excluded explicitly or its bytes
            // would be decoded as an image.
            bool isImage = naxis >= 2 &&
                           (isPrimary || xtension.Equals("IMAGE", StringComparison.OrdinalIgnoreCase));

            // Standard FITS data size: |BITPIX|/8 * GCOUNT * (PCOUNT + product(NAXISn)).
            long elements = 0;
            if (naxis > 0)
            {
                elements = 1;
                for (int i = 1; i <= naxis; i++)
                    elements *= axes.TryGetValue(i, out var n) ? Math.Max(n, 0) : 0;
            }
            long dataBytes = naxis == 0
                ? 0
                : (long)(Math.Abs(bitpix) / 8) * Math.Max(gcount, 1) * (pcount + elements);

            return new FitsMeta(bitpix, width, height, bzero, bscale, isImage, dataBytes, zimage, cards);
        }
    }

    /// <summary>FITS data segments are padded out to whole 2880-byte blocks.</summary>
    private static long Pad2880(long bytes) => bytes <= 0 ? 0 : (bytes + 2879) / 2880 * 2880;

    private static bool ReadFully(FileStream fs, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = fs.Read(buffer, total, buffer.Length - total);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>Value of a string-valued card, e.g. XTENSION= 'IMAGE   ' -&gt; IMAGE.</summary>
    private static string ParseCardString(string card)
    {
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return val.Trim('\'').Trim();
    }

    private static int ParseCardInt(string card)
    {
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return int.TryParse(val, out var v) ? v : 0;
    }

    private static double ParseCardDouble(string card)
    {
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return NumericParse.TryParse(val, out var v) ? v : 0.0;
    }

    private static double ReadPixelValue(byte[] raw, int o, int bitpix) => bitpix switch
    {
        8 => raw[o],
        16 => (short)((raw[o] << 8) | raw[o + 1]),
        32 => (int)(((uint)raw[o] << 24) | ((uint)raw[o + 1] << 16) | ((uint)raw[o + 2] << 8) | raw[o + 3]),
        -32 => ToSingleBE(raw, o),
        -64 => ToDoubleBE(raw, o),
        _ => 0.0,
    };

    private static float ToSingleBE(byte[] raw, int o)
    {
        if (BitConverter.IsLittleEndian)
        {
            byte[] b = [raw[o + 3], raw[o + 2], raw[o + 1], raw[o]];
            return BitConverter.ToSingle(b, 0);
        }
        return BitConverter.ToSingle(raw, o);
    }

    private static double ToDoubleBE(byte[] raw, int o)
    {
        if (BitConverter.IsLittleEndian)
        {
            byte[] b = [raw[o + 7], raw[o + 6], raw[o + 5], raw[o + 4],
                        raw[o + 3], raw[o + 2], raw[o + 1], raw[o]];
            return BitConverter.ToDouble(b, 0);
        }
        return BitConverter.ToDouble(raw, o);
    }
}
