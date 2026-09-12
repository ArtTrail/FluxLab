using System.IO;
using System.Text;
using FitsPhotometry.Core.Fits;

namespace FitsPhotometry.Core.Tests;

public class FitsHeaderTests
{
    /// <summary>Hand-builds a minimal, valid single-HDU FITS file (BITPIX=16, no BZERO/BSCALE
    /// cards so FitsImage.Load's zero-offset fast path applies directly) -- there's no general
    /// FITS writer elsewhere in Core to reuse, and Save's safety depends on exact byte layout, so
    /// the test needs full control over what the "original" file looks like.</summary>
    private static string BuildTestFits(int width, int height, short[] pixels, params (string Kw, string Val, string Comment)[] extraCards)
    {
        var cards = new List<string>
        {
            FormatTestCard("SIMPLE", "T", "conforms to FITS standard"),
            FormatTestCard("BITPIX", "16", "16-bit signed integer"),
            FormatTestCard("NAXIS", "2", ""),
            FormatTestCard("NAXIS1", width.ToString(), ""),
            FormatTestCard("NAXIS2", height.ToString(), ""),
        };
        foreach (var (kw, val, comment) in extraCards)
            cards.Add(FormatTestCard(kw, val, comment));
        cards.Add("END".PadRight(80));

        var headerBytes = new List<byte>();
        foreach (var c in cards) headerBytes.AddRange(Encoding.ASCII.GetBytes(c));
        int pad = (2880 - (headerBytes.Count % 2880)) % 2880;
        headerBytes.AddRange(Encoding.ASCII.GetBytes(new string(' ', pad)));

        var dataBytes = new byte[pixels.Length * 2];
        for (int i = 0; i < pixels.Length; i++)
        {
            dataBytes[i * 2] = (byte)(pixels[i] >> 8);
            dataBytes[i * 2 + 1] = (byte)(pixels[i] & 0xFF);
        }
        int dataPad = (2880 - (dataBytes.Length % 2880)) % 2880;

        var path = Path.Combine(Path.GetTempPath(), $"fitsheader_test_{Guid.NewGuid():N}.fits");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(headerBytes.ToArray());
        fs.Write(dataBytes);
        if (dataPad > 0) fs.Write(new byte[dataPad]);
        return path;
    }

    // Value uses simple whole-number/short-string test data only, so a plain fixed layout
    // (value starting at column 11, like FitsHeader.FormatCard produces) is enough here.
    private static string FormatTestCard(string keyword, string value, string comment)
    {
        bool numeric = double.TryParse(value, out _);
        string valueField = numeric ? value.PadLeft(20) : ("'" + value + "'").PadRight(20);
        string body = valueField + (comment.Length > 0 ? " / " + comment : "");
        return (keyword.PadRight(8) + "= " + body).PadRight(80)[..80];
    }

    [Fact]
    public void Read_PreservesCardOrderValueAndComment()
    {
        var path = BuildTestFits(2, 2, [1, 2, 3, 4],
            ("EGAIN", "1.04", "e-/ADU"),
            ("OBJECT", "KELT-8", "target name"));
        try
        {
            var header = FitsHeader.Read(path);

            Assert.Equal("1.04", header.Get("EGAIN"));
            Assert.Equal("KELT-8", header.Get("OBJECT"));

            var egainCard = header.Cards.First(c => c.Keyword == "EGAIN");
            Assert.Equal("e-/ADU", egainCard.Comment);

            // Order preserved: OBJECT comes after EGAIN, both after the mandatory cards.
            var keywords = header.Cards.Select(c => c.Keyword).ToList();
            Assert.True(keywords.IndexOf("EGAIN") < keywords.IndexOf("OBJECT"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveToFile_RoundTrips_EditedValue_WithoutTouchingPixelData()
    {
        short[] pixels = [100, 200, 300, 400, 500, 600];
        var path = BuildTestFits(3, 2, pixels, ("EGAIN", "1.04", "e-/ADU"));
        try
        {
            var header = FitsHeader.Read(path);
            header.SetCard("EGAIN", "1.10", "e-/ADU (recalibrated)");
            header.SaveToFile(path);

            var reloaded = FitsHeader.Read(path);
            Assert.Equal("1.10", reloaded.Get("EGAIN"));
            Assert.Equal("e-/ADU (recalibrated)", reloaded.Cards.First(c => c.Keyword == "EGAIN").Comment);

            var image = FitsImage.Load(path);
            Assert.Equal(3, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(pixels.Select(p => (float)p), image.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveToFile_RoundTrips_AddedAndDeletedCards()
    {
        var path = BuildTestFits(2, 2, [1, 2, 3, 4], ("OLDKW", "42", ""));
        try
        {
            var header = FitsHeader.Read(path);
            header.SetCard("NEWKW", "hello world", "a new card");
            Assert.True(header.RemoveCard("OLDKW"));
            header.SaveToFile(path);

            var reloaded = FitsHeader.Read(path);
            Assert.Equal("hello world", reloaded.Get("NEWKW"));
            Assert.False(reloaded.Has("OLDKW"));

            var image = FitsImage.Load(path);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UpdateCard_TargetsExactCardByIdentity_AmongDuplicateCommentKeywords()
    {
        // COMMENT/HISTORY have no unique keyword -- a header can carry many. SetCard's
        // keyword-indexed update can't tell them apart, so editing must go by object identity.
        var path = BuildTestFits(2, 2, [1, 2, 3, 4]);
        try
        {
            var header = FitsHeader.Read(path);
            header.SetCard("COMMENT", "first comment", "");
            header.SetCard("COMMENT", "second comment", "");
            var comments = header.Cards.Where(c => c.Keyword == "COMMENT").ToList();
            Assert.Equal(2, comments.Count);

            header.UpdateCard(comments[1], "edited second comment", "");

            Assert.Equal("first comment", comments[0].Value);
            Assert.Equal("edited second comment", comments[1].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RemoveCard_RefusesProtectedKeywords()
    {
        var path = BuildTestFits(2, 2, [1, 2, 3, 4]);
        try
        {
            var header = FitsHeader.Read(path);
            Assert.False(header.RemoveCard("BITPIX"));
            Assert.False(header.RemoveCard("NAXIS1"));
            Assert.True(header.Has("BITPIX"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveToFile_HeaderGrowingPastOneBlock_StillLeavesDataIntact()
    {
        // 36 cards fit in one 2880-byte block; pushing well past that forces Save to grow the
        // header into a second block, exercising the pad-to-2880 and data-offset-preserving logic.
        short[] pixels = [7, 8, 9, 10];
        var path = BuildTestFits(2, 2, pixels);
        try
        {
            var header = FitsHeader.Read(path);
            for (int i = 0; i < 50; i++)
                header.SetCard($"EXTRA{i}", i.ToString(), $"padding card {i}");
            header.SaveToFile(path);

            var reloaded = FitsHeader.Read(path);
            Assert.Equal("25", reloaded.Get("EXTRA25"));

            var image = FitsImage.Load(path);
            Assert.Equal(pixels.Select(p => (float)p), image.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetCard_TruncatesKeywordsLongerThan8Chars_SoTheyRoundTripCorrectly()
    {
        // Regression test: a keyword over 8 chars used to push "= " out of its required column,
        // corrupting that card into unreadable "commentary" text on the next read -- found via a
        // real file during manual testing.
        var path = BuildTestFits(2, 2, [1, 2, 3, 4]);
        try
        {
            var header = FitsHeader.Read(path);
            header.SetCard("CLAUDETEST", "42", "should be truncated to CLAUDETE");
            header.SaveToFile(path);

            var reloaded = FitsHeader.Read(path);
            Assert.Equal("42", reloaded.Get("CLAUDETE"));
            Assert.Equal("CLAUDETE", reloaded.Cards.Single(c => c.Value == "42").Keyword);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveToFile_TileCompressedContainer_Throws()
    {
        var cards = new List<FitsCard> { new() { Keyword = "BITPIX", Value = "16" } };
        var header = new FitsHeader(cards, isFromCompressedContainer: true, originalHeaderBlockBytes: 2880);
        Assert.Throws<NotSupportedException>(() => header.SaveToFile(Path.GetTempFileName()));
    }
}
