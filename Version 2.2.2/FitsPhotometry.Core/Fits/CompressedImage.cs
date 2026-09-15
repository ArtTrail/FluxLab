using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FitsPhotometry.Core.Fits;

/// <summary>
/// Decoder for tile-compressed FITS images (the ".fz" convention: image stored in a BINTABLE with
/// ZIMAGE=T). Supports the integer cases FluxLab actually sees from cameras: RICE_1 (the common one)
/// and GZIP_1, any tiling, BITPIX 8/16/32 with BZERO/BSCALE. Returns null for anything it does not
/// handle (float quantization via ZQUANTIZ, HCOMPRESS/PLIO, a tile with no COMPRESSED_DATA) so the
/// caller falls back to "empty" rather than rendering garbage.
///
/// Rice decode ported verbatim from CFITSIO's fits_rdecomp_short / fits_rdecomp (ricecomp.c).
/// Verified against astropy on a real ASI2600 .fz (6252x4176, min 387 / max 7741 / median 499).
/// </summary>
public static class CompressedImage
{
    // nonzero_count[b] = number of bits to represent b (position of its highest set bit); [0]=0.
    private static readonly int[] NonzeroCount = BuildNonzeroCount();
    private static int[] BuildNonzeroCount()
    {
        var t = new int[256];
        for (int b = 1; b < 256; b++) { int k = 0, v = b; while (v > 0) { k++; v >>= 1; } t[b] = k; }
        return t;
    }

    /// <summary>Reads and decodes a compressed-image BINTABLE whose header has already been parsed.
    /// The stream must be positioned at the first byte of the table's data. <paramref name="cards"/>
    /// maps trimmed keyword -> raw 80-char card. Returns null if unsupported.</summary>
    public static FitsImage.Loaded? TryDecode(Stream fs, IReadOnlyDictionary<string, string> cards, long dataBytes)
    {
        string Cmp = CardStr(cards, "ZCMPTYPE");
        if (Cmp != "RICE_1" && Cmp != "GZIP_1") return null;      // unsupported compression
        if (cards.ContainsKey("ZQUANTIZ")) return null;           // float-quantized: not handled yet

        int zbitpix = CardInt(cards, "ZBITPIX");
        int znaxis  = CardInt(cards, "ZNAXIS");
        if (znaxis != 2) return null;
        int znx = CardInt(cards, "ZNAXIS1");
        int zny = CardInt(cards, "ZNAXIS2");
        int ztx = cards.ContainsKey("ZTILE1") ? CardInt(cards, "ZTILE1") : znx;
        int zty = cards.ContainsKey("ZTILE2") ? CardInt(cards, "ZTILE2") : 1;
        if (znx <= 0 || zny <= 0 || ztx <= 0 || zty <= 0) return null;

        int bytepix = Math.Abs(zbitpix) / 8;
        if (bytepix != 1 && bytepix != 2 && bytepix != 4) return null;

        // Rice parameters live in ZNAMEn/ZVALn pairs.
        int blocksize = 32;
        for (int n = 1; cards.ContainsKey($"ZNAME{n}"); n++)
        {
            string nm = CardStr(cards, $"ZNAME{n}");
            if (nm == "BLOCKSIZE") blocksize = CardInt(cards, $"ZVAL{n}");
            // BYTEPIX (ZVALn) is implied by ZBITPIX; we use bytepix from ZBITPIX above.
        }

        double bzero  = cards.ContainsKey("BZERO")  ? CardDouble(cards, "BZERO")  : 0.0;
        double bscale = cards.ContainsKey("BSCALE") ? CardDouble(cards, "BSCALE") : 1.0;

        // Table geometry.
        int naxis1 = CardInt(cards, "NAXIS1");   // bytes per row
        int naxis2 = CardInt(cards, "NAXIS2");   // number of rows (= number of tiles)
        long pcount = CardInt(cards, "PCOUNT");
        long theap = cards.ContainsKey("THEAP") ? CardInt(cards, "THEAP") : (long)naxis1 * naxis2;

        int tfields = CardInt(cards, "TFIELDS");
        // Find the COMPRESSED_DATA column and its byte offset within a row.
        int colOffset = -1; char descKind = 'P';
        int off = 0;
        for (int n = 1; n <= tfields; n++)
        {
            string tform = CardStr(cards, $"TFORM{n}");
            string ttype = CardStr(cards, $"TTYPE{n}");
            (int width, char kind) = TformWidth(tform);
            if (ttype == "COMPRESSED_DATA") { colOffset = off; descKind = kind; }
            off += width;
        }
        if (colOffset < 0 || (descKind != 'P' && descKind != 'Q')) return null;

        // Read the whole table + heap segment.
        var seg = new byte[dataBytes];
        if (!ReadFully(fs, seg)) return null;

        int tilesX = (znx + ztx - 1) / ztx;
        int tilesY = (zny + zty - 1) / zty;
        long nTiles = (long)tilesX * tilesY;
        if (nTiles > naxis2) return null;   // more tiles than table rows: malformed

        var pixels = new float[(long)znx * zny];
        var tileVals = new int[ztx * zty];   // reusable per-tile decode buffer (max tile size)

        for (int ty = 0; ty < tilesY; ty++)
        {
            int y0 = ty * zty;
            int th = Math.Min(zty, zny - y0);
            for (int tx = 0; tx < tilesX; tx++)
            {
                int x0 = tx * ztx;
                int tw = Math.Min(ztx, znx - x0);
                int npix = tw * th;
                long tileIdx = (long)ty * tilesX + tx;      // tiles ordered X-fastest

                // Variable-length descriptor for this tile's COMPRESSED_DATA.
                long rowStart = tileIdx * naxis1 + colOffset;
                long nelem, hoff;
                if (descKind == 'P')
                {
                    nelem = ReadI32BE(seg, (int)rowStart);
                    hoff  = ReadI32BE(seg, (int)rowStart + 4);
                }
                else // 'Q'
                {
                    nelem = ReadI64BE(seg, (int)rowStart);
                    hoff  = ReadI64BE(seg, (int)rowStart + 8);
                }
                if (nelem <= 0) return null;   // a tile with no COMPRESSED_DATA (would need a fallback column)
                int cStart = (int)(theap + hoff);
                if (cStart < 0 || cStart + nelem > seg.Length) return null;

                if (Cmp == "RICE_1")
                {
                    if (bytepix == 2) RDecompShort(seg, cStart, (int)nelem, tileVals, npix, blocksize);
                    else if (bytepix == 4) RDecompInt(seg, cStart, (int)nelem, tileVals, npix, blocksize);
                    else return null;   // RICE byte-pixel is rare; not implemented
                }
                else // GZIP_1: gunzip to big-endian ZBITPIX values, no differencing
                {
                    if (!GzipTile(seg, cStart, (int)nelem, tileVals, npix, bytepix)) return null;
                }

                // Place the decoded tile into the image, applying BZERO/BSCALE and the signed
                // interpretation of ZBITPIX.
                for (int ry = 0; ry < th; ry++)
                {
                    long dst = (long)(y0 + ry) * znx + x0;
                    int src = ry * tw;
                    for (int rx = 0; rx < tw; rx++)
                    {
                        int raw = tileVals[src + rx];
                        double v = zbitpix switch
                        {
                            16 => (short)raw,
                            32 => raw,
                            8  => (byte)raw,
                            _  => raw,
                        };
                        pixels[dst + rx] = (float)(bzero + bscale * v);
                    }
                }
            }
        }

        return new FitsImage.Loaded(pixels, znx, zny);
    }

    // ── CFITSIO fits_rdecomp_short (16-bit) ──────────────────────────────────────────────────────
    private static void RDecompShort(byte[] c, int ci, int clen, int[] outArr, int nx, int nblock)
    {
        const int fsbits = 4, fsmax = 14, bbits = 1 << fsbits;
        uint lastpix = (uint)((c[ci] << 8) | c[ci + 1]);
        ci += 2;
        uint b = c[ci++];
        int nbits = 8;
        for (int i = 0; i < nx;)
        {
            nbits -= fsbits;
            while (nbits < 0) { b = (b << 8) | c[ci++]; nbits += 8; }
            int fs = (int)(b >> nbits) - 1;
            b &= (uint)((1 << nbits) - 1);
            int imax = Math.Min(i + nblock, nx);
            if (fs < 0)
            {
                for (; i < imax; i++) outArr[i] = (int)lastpix;
            }
            else if (fs == fsmax)
            {
                for (; i < imax; i++)
                {
                    int k = bbits - nbits;
                    uint diff = b << k;
                    for (k -= 8; k >= 0; k -= 8) { b = c[ci++]; diff |= b << k; }
                    if (nbits > 0) { b = c[ci++]; diff |= b >> (-k); b &= (uint)((1 << nbits) - 1); }
                    else b = 0;
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    lastpix = (uint)((diff + lastpix) & 0xffff);
                    outArr[i] = (int)lastpix;
                }
            }
            else
            {
                for (; i < imax; i++)
                {
                    while (b == 0) { nbits += 8; b = c[ci++]; }
                    int nzero = nbits - NonzeroCount[(int)b];
                    nbits -= nzero + 1;
                    b ^= (uint)(1 << nbits);
                    nbits -= fs;
                    while (nbits < 0) { b = (b << 8) | c[ci++]; nbits += 8; }
                    uint diff = ((uint)nzero << fs) | (b >> nbits);
                    b &= (uint)((1 << nbits) - 1);
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    lastpix = (uint)((diff + lastpix) & 0xffff);
                    outArr[i] = (int)lastpix;
                }
            }
        }
    }

    // ── CFITSIO fits_rdecomp (32-bit) ────────────────────────────────────────────────────────────
    private static void RDecompInt(byte[] c, int ci, int clen, int[] outArr, int nx, int nblock)
    {
        const int fsbits = 5, fsmax = 25, bbits = 1 << fsbits;
        uint lastpix = ((uint)c[ci] << 24) | ((uint)c[ci + 1] << 16) | ((uint)c[ci + 2] << 8) | c[ci + 3];
        ci += 4;
        uint b = c[ci++];
        int nbits = 8;
        for (int i = 0; i < nx;)
        {
            nbits -= fsbits;
            while (nbits < 0) { b = (b << 8) | c[ci++]; nbits += 8; }
            int fs = (int)(b >> nbits) - 1;
            b &= (uint)((1 << nbits) - 1);
            int imax = Math.Min(i + nblock, nx);
            if (fs < 0)
            {
                for (; i < imax; i++) outArr[i] = (int)lastpix;
            }
            else if (fs == fsmax)
            {
                for (; i < imax; i++)
                {
                    int k = bbits - nbits;
                    uint diff = b << k;
                    for (k -= 8; k >= 0; k -= 8) { b = c[ci++]; diff |= b << k; }
                    if (nbits > 0) { b = c[ci++]; diff |= b >> (-k); b &= (uint)((1 << nbits) - 1); }
                    else b = 0;
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    lastpix = diff + lastpix;
                    outArr[i] = (int)lastpix;
                }
            }
            else
            {
                for (; i < imax; i++)
                {
                    while (b == 0) { nbits += 8; b = c[ci++]; }
                    int nzero = nbits - NonzeroCount[(int)b];
                    nbits -= nzero + 1;
                    b ^= (uint)(1 << nbits);
                    nbits -= fs;
                    while (nbits < 0) { b = (b << 8) | c[ci++]; nbits += 8; }
                    uint diff = ((uint)nzero << fs) | (b >> nbits);
                    b &= (uint)((1 << nbits) - 1);
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    lastpix = diff + lastpix;
                    outArr[i] = (int)lastpix;
                }
            }
        }
    }

    private static bool GzipTile(byte[] seg, int start, int len, int[] outArr, int npix, int bytepix)
    {
        try
        {
            using var ms = new MemoryStream(seg, start, len);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            var raw = new byte[npix * bytepix];
            int total = 0, n;
            while (total < raw.Length && (n = gz.Read(raw, total, raw.Length - total)) > 0) total += n;
            if (total != raw.Length) return false;
            for (int i = 0; i < npix; i++)
            {
                int o = i * bytepix;
                outArr[i] = bytepix switch
                {
                    2 => (short)((raw[o] << 8) | raw[o + 1]),
                    4 => (raw[o] << 24) | (raw[o + 1] << 16) | (raw[o + 2] << 8) | raw[o + 3],
                    _ => raw[o],
                };
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Byte width of one row cell for a TFORM, and (for a variable-length array) the
    /// descriptor kind 'P' (32-bit) or 'Q' (64-bit); otherwise '\0'.</summary>
    private static (int width, char kind) TformWidth(string tform)
    {
        int i = 0; while (i < tform.Length && char.IsDigit(tform[i])) i++;
        int repeat = i > 0 && int.TryParse(tform.AsSpan(0, i), out var r) ? r : 1;
        char type = i < tform.Length ? char.ToUpperInvariant(tform[i]) : '\0';
        return type switch
        {
            'P' => (8, 'P'),
            'Q' => (16, 'Q'),
            'L' or 'A' or 'B' or 'X' => (type == 'X' ? (repeat + 7) / 8 : repeat, '\0'),
            'I' => (2 * repeat, '\0'),
            'J' or 'E' => (4 * repeat, '\0'),
            'K' or 'D' or 'C' => (8 * repeat, '\0'),
            'M' => (16 * repeat, '\0'),
            _ => (0, '\0'),
        };
    }

    private static long ReadI32BE(byte[] a, int o)
        => (a[o] << 24) | (a[o + 1] << 16) | (a[o + 2] << 8) | a[o + 3];

    private static long ReadI64BE(byte[] a, int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | a[o + i];
        return v;
    }

    private static bool ReadFully(Stream fs, byte[] buffer)
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

    private static string CardStr(IReadOnlyDictionary<string, string> cards, string key)
    {
        if (!cards.TryGetValue(key, out var card)) return "";
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return val.Trim('\'').Trim();
    }
    private static int CardInt(IReadOnlyDictionary<string, string> cards, string key)
        => int.TryParse(CardStr(cards, key), out var v) ? v : 0;
    private static double CardDouble(IReadOnlyDictionary<string, string> cards, string key)
        => NumericParse.TryParse(CardStr(cards, key), out var v) ? v : 0.0;
}
