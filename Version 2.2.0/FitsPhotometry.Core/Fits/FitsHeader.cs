using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FitsPhotometry.Core.Fits;

/// <summary>One FITS header card. For COMMENT/HISTORY/blank-keyword cards, Value holds the free
/// text and Comment is unused (FITS gives those no "/ comment" field of their own).</summary>
public sealed class FitsCard
{
    public string Keyword { get; set; } = "";
    public string Value { get; set; } = "";
    public string Comment { get; set; } = "";

    public bool IsCommentary => Keyword is "COMMENT" or "HISTORY" or "";
}

/// <summary>
/// Reads and edits a FITS primary header without any external library. Ported from TransitLab's
/// FitsHeaderService for reading, extended here with ordered/comment-preserving cards and an
/// in-place Save, to support a header viewer/editor matching the Python app's. FITS headers
/// consist of 2880-byte blocks; each 80-byte record has the form: KEYWORD = VALUE / comment
/// (COMMENT/HISTORY/blank-keyword cards omit the "= " and just carry free text from column 9).
/// </summary>
public sealed class FitsHeader
{
    private readonly List<FitsCard> _cards;
    private readonly Dictionary<string, string> _kv;

    /// <summary>Ordered, editable view of every card in the header, in file order.</summary>
    public IReadOnlyList<FitsCard> Cards => _cards;

    /// <summary>True if this header was read via the tile-compression (.fz-style) remap path --
    /// Save is refused for these, since a correct in-place rewrite would also need to touch the
    /// compressed binary-table data, which this minimal reader/writer doesn't implement.</summary>
    public bool IsFromCompressedContainer { get; }

    /// <summary>Byte length of the original header block(s) in the source file, padded to the
    /// first 2880-byte boundary after END -- where the pixel data begins. Save uses this to copy
    /// the original data forward unchanged regardless of how the edited header's length changes.</summary>
    public long OriginalHeaderBlockBytes { get; }

    /// <summary>Keywords required for the file to remain a valid FITS image -- Save-time edits
    /// must not remove these.</summary>
    public static readonly HashSet<string> ProtectedKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "END" };

    public FitsHeader(Dictionary<string, string> kv)
        : this(kv.Select(p => new FitsCard { Keyword = p.Key, Value = p.Value }).ToList())
    {
    }

    public FitsHeader(List<FitsCard> cards, bool isFromCompressedContainer = false, long originalHeaderBlockBytes = 0)
    {
        _cards = cards;
        _kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _cards)
            if (!c.IsCommentary) _kv[c.Keyword] = c.Value;
        IsFromCompressedContainer = isFromCompressedContainer;
        OriginalHeaderBlockBytes = originalHeaderBlockBytes;
    }

    /// <summary>Return the raw string value for a keyword, or "" if absent.</summary>
    public string Get(string keyword)
        => _kv.TryGetValue(Norm(keyword), out var v) ? v : "";

    public bool Has(string keyword) => _kv.ContainsKey(Norm(keyword));

    /// <summary>Return a double or null if absent / unparseable.</summary>
    public double? GetDouble(string keyword)
        => NumericParse.TryParse(Get(keyword), out var d) ? d : null;

    /// <summary>Return an int or null if absent / unparseable.</summary>
    public int? GetInt(string keyword)
        => int.TryParse(Get(keyword), out var i) ? i : null;

    /// <summary>Uppercases and truncates to 8 characters -- the maximum FITS allows in the
    /// keyword field (columns 1-8). A card with a longer keyword would push "= " out of column
    /// 9-10 on write, corrupting that card (and misreading it as commentary text) on the next
    /// read -- confirmed against a real file during testing.</summary>
    private static string Norm(string keyword)
    {
        keyword = keyword.Trim().ToUpperInvariant();
        return keyword.Length > 8 ? keyword[..8] : keyword;
    }

    /// <summary>Adds a new card, or updates the value/comment of an existing one with the same
    /// keyword. COMMENT/HISTORY/blank cards are always appended as new cards (a header can carry
    /// any number of them).</summary>
    public void SetCard(string keyword, string value, string comment)
    {
        keyword = Norm(keyword);
        var card = keyword is not ("COMMENT" or "HISTORY" or "")
            ? _cards.FirstOrDefault(c => c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            : null;
        if (card is not null)
        {
            card.Value = value;
            card.Comment = comment;
        }
        else
        {
            _cards.Add(new FitsCard { Keyword = keyword, Value = value, Comment = comment });
        }
        if (keyword is not ("COMMENT" or "HISTORY" or "")) _kv[keyword] = value;
    }

    /// <summary>Updates one specific card's value/comment in place, by object identity rather
    /// than keyword lookup -- needed for COMMENT/HISTORY/blank cards, which have no unique
    /// keyword to look up (a header can carry many of each), so SetCard's keyword-indexed update
    /// can't target the right one. <paramref name="card"/> must be an instance from this
    /// header's Cards list.</summary>
    public void UpdateCard(FitsCard card, string value, string comment)
    {
        card.Value = value;
        card.Comment = comment;
        if (!card.IsCommentary) _kv[Norm(card.Keyword)] = value;
    }

    /// <summary>Removes every card with the given keyword. Refuses (returns false, no-op) for
    /// keywords required to keep the file a valid FITS image -- callers should check
    /// ProtectedKeywords first to give the user a clear message rather than relying on this.</summary>
    public bool RemoveCard(string keyword)
    {
        keyword = Norm(keyword);
        if (ProtectedKeywords.Contains(keyword)) return false;
        int removed = _cards.RemoveAll(c => c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        _kv.Remove(keyword);
        return removed > 0;
    }

    /// <summary>
    /// Read one HDU's header (2880-byte blocks of 80-byte cards) starting at the stream's
    /// current position, preserving card order and comments. Leaves the stream positioned at the
    /// start of that HDU's data (the next 2880-byte boundary after the END card).
    /// </summary>
    public static List<FitsCard> ReadHeaderBlock(FileStream fs)
    {
        var cards = new List<FitsCard>();
        var block = new byte[2880];
        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 80) break;

            bool end = false;
            for (int i = 0; i + 79 < read; i += 80)
            {
                var record = Encoding.ASCII.GetString(block, i, 80);
                var kw = record[..8].TrimEnd();

                if (kw == "END") { end = true; break; }
                if (kw.Length == 0 && record.Trim().Length == 0) continue;   // blank filler card

                if (record.Length > 8 && record[8] == '=')
                {
                    var rest = record[9..];
                    string rawVal;
                    string comment = "";
                    int slash = FindUnquotedSlash(rest);
                    var valuePart = slash >= 0 ? rest[..slash] : rest;
                    if (slash >= 0) comment = rest[(slash + 1)..].Trim();
                    rawVal = valuePart.Trim();
                    if (rawVal.StartsWith('\'')) rawVal = rawVal.Trim('\'').TrimEnd().Replace("''", "'");
                    cards.Add(new FitsCard { Keyword = kw, Value = rawVal, Comment = comment });
                }
                else
                {
                    // COMMENT / HISTORY / blank-keyword free-text card -- no "=", no comment field.
                    cards.Add(new FitsCard { Keyword = kw, Value = record[8..].TrimEnd(), Comment = "" });
                }
            }
            if (end) break;
        }
        return cards;
    }

    /// <summary>Finds the "/" that starts the trailing comment, ignoring any "/" inside a quoted
    /// string value (e.g. a date like '2026-08-07T00/00/00' or an object name containing "/").</summary>
    private static int FindUnquotedSlash(string s)
    {
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\'') inQuotes = !inQuotes;
            else if (s[i] == '/' && !inQuotes) return i;
        }
        return -1;
    }

    private static string GetRaw(List<FitsCard> cards, string keyword)
        => cards.FirstOrDefault(c => c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    /// <summary>
    /// Read the effective header from a FITS file. Throws on I/O or format error. For
    /// Rice/GZIP tile-compressed images (.fz), the primary HDU is an empty shell per the FITS
    /// Tile Compression convention -- transparently reads the first extension's header instead
    /// and remaps ZBITPIX/ZNAXIS/ZNAXISn back to BITPIX/NAXIS/NAXISn.
    ///
    /// NOTE: only the header-remapping half of tile-compression support is ported here. Reading
    /// the actual (Rice/GZIP-compressed) pixel data of a .fz file is not yet implemented in
    /// FitsImage.Load -- deferred, since it's a substantial standalone piece (TransitLab's
    /// FitsCompressionService is ~490 lines) and not needed for the exposure-meter workflow.
    /// </summary>
    public static FitsHeader Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var cards = ReadHeaderBlock(fs);
        long headerBytes = fs.Position;

        bool looksCompressedContainer =
            GetRaw(cards, "NAXIS") == "0" && GetRaw(cards, "EXTEND") == "T";

        if (looksCompressedContainer)
        {
            var extCards = ReadHeaderBlock(fs);
            if (GetRaw(extCards, "XTENSION") == "BINTABLE" && GetRaw(extCards, "ZIMAGE") == "T")
            {
                var h = new FitsHeader(extCards, isFromCompressedContainer: true, originalHeaderBlockBytes: 0);
                if (h.Has("ZBITPIX")) h.SetCard("BITPIX", h.Get("ZBITPIX"), "");
                if (h.Has("ZNAXIS")) h.SetCard("NAXIS", h.Get("ZNAXIS"), "");
                for (int n = 1; h.Has($"ZNAXIS{n}"); n++)
                    h.SetCard($"NAXIS{n}", h.Get($"ZNAXIS{n}"), "");
                return h;
            }
        }

        return new FitsHeader(cards, isFromCompressedContainer: false, originalHeaderBlockBytes: headerBytes);
    }

    /// <summary>
    /// Writes the current (possibly edited) cards back to the file at <paramref name="path"/> in
    /// place: rebuilds the header block from scratch, then copies the original pixel/extension
    /// data bytes forward unchanged from OriginalHeaderBlockBytes -- so this is safe regardless
    /// of whether the edited header grew or shrank relative to the original. Reads the original
    /// data fully into memory before opening the file for writing, so a failure partway through
    /// formatting never touches the file on disk.
    /// </summary>
    public void SaveToFile(string path)
    {
        if (IsFromCompressedContainer)
            throw new NotSupportedException(
                "This file's image data is tile-compressed (.fz-style); saving header edits back to it is not supported.");

        var headerBytes = new List<byte>(_cards.Count * 80 + 80);
        foreach (var card in _cards)
            headerBytes.AddRange(Encoding.ASCII.GetBytes(FormatCard(card)));
        headerBytes.AddRange(Encoding.ASCII.GetBytes("END".PadRight(80)));

        int pad = (2880 - (headerBytes.Count % 2880)) % 2880;
        if (pad > 0) headerBytes.AddRange(Encoding.ASCII.GetBytes(new string(' ', pad)));

        byte[] originalDataBytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(OriginalHeaderBlockBytes, SeekOrigin.Begin);
            originalDataBytes = new byte[fs.Length - OriginalHeaderBlockBytes];
            fs.ReadExactly(originalDataBytes);
        }

        using var outFs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        outFs.Write(headerBytes.ToArray());
        outFs.Write(originalDataBytes);
    }

    /// <summary>Formats one 80-byte card record. Regular keyword=value cards place the value
    /// starting at exactly column 11 (0-indexed 10) with "= " at columns 9-10, matching
    /// FitsImage.ReadMeta's column-position-dependent parsing, so BITPIX/NAXIS1/NAXIS2/BZERO/
    /// BSCALE etc. remain readable by this app (and other FITS readers) after a save.</summary>
    private static string FormatCard(FitsCard card)
    {
        // Defensive truncation here too, in case a FitsCard was ever built without going through
        // SetCard's normalization (Cards is a mutable public list).
        string kw = card.Keyword.Length > 8 ? card.Keyword[..8] : card.Keyword;

        if (card.IsCommentary)
        {
            string text = card.Value.Length > 72 ? card.Value[..72] : card.Value;
            return (kw.PadRight(8) + text).PadRight(80)[..80];
        }

        string body = FormatValue(card.Value);
        if (!string.IsNullOrEmpty(card.Comment)) body += " / " + card.Comment;

        string record = kw.PadRight(8) + "= " + body;
        return record.Length > 80 ? record[..80] : record.PadRight(80);
    }

    private static string FormatValue(string raw)
    {
        if (raw is "T" or "F") return raw.PadLeft(20);
        if (long.TryParse(raw, CultureInfo.InvariantCulture, out _) ||
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return raw.PadLeft(20);

        string quoted = "'" + raw.Replace("'", "''") + "'";
        return quoted.PadRight(Math.Max(20, quoted.Length));
    }
}
