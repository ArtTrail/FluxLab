using System;
using System.IO;

namespace FitsPhotometry.Core.Fits;

/// <summary>Minimal FITS image reader -- supports BITPIX 8/16/32/-32/-64 with BZERO/BSCALE.
/// Ported from TransitLab's FitsImageService. Rice/GZIP tile-compressed (.fz) pixel data is not
/// yet supported here (only the header remapping in FitsHeader.Read is) -- deferred as noted
/// there.
/// </summary>
public static class FitsImage
{
    public sealed record Loaded(float[] Pixels, int Width, int Height);

    private sealed record FitsMeta(int Bitpix, int Width, int Height, double Bzero, double Bscale);

    /// <summary>Load the full first image plane as float pixels (BZERO/BSCALE applied).</summary>
    public static Loaded Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = ReadMeta(fs);
        if (meta.Width == 0 || meta.Height == 0) return new Loaded([], 0, 0);

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

    private static FitsMeta ReadMeta(FileStream fs)
    {
        int bitpix = 0, width = 0, height = 0;
        double bzero = 0.0, bscale = 1.0;
        var block = new byte[2880];
        var card = new byte[80];

        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 2880) break;

            bool foundEnd = false;
            for (int c = 0; c < 36; c++)
            {
                Array.Copy(block, c * 80, card, 0, 80);
                var key = System.Text.Encoding.ASCII.GetString(card, 0, 8);

                if (key.StartsWith("END", StringComparison.Ordinal))
                { foundEnd = true; break; }

                var cardStr = System.Text.Encoding.ASCII.GetString(card, 0, 80);
                var trimKey = key.TrimEnd();

                if      (trimKey == "BITPIX") bitpix = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS1") width  = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS2") height = ParseCardInt(cardStr);
                else if (trimKey == "BZERO")  bzero  = ParseCardDouble(cardStr);
                else if (trimKey == "BSCALE") bscale = ParseCardDouble(cardStr);
            }

            if (foundEnd) break;
        }

        return new FitsMeta(bitpix, width, height, bzero, bscale);
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
