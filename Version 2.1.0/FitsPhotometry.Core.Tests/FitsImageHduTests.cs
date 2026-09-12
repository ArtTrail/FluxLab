using System.Text;
using FitsPhotometry.Core.Fits;

namespace FitsPhotometry.Core.Tests;

/// <summary>v2.0.1 regression cover for HDU-chain walking. Before this, FitsImage.Load read only
/// the primary header, so any file with an empty primary and its image in an extension -- anything
/// unpacked from .fz looks like that -- loaded as 0x0 and the viewer displayed nothing at all, with
/// no error shown. Verified against two real frames at the time of the fix (an extension-based file
/// matched astropy exactly on dimensions/min/max/median); these tests pin the behaviour with
/// synthetic files so it can't silently regress.</summary>
public class FitsImageHduTests
{
    private static string Card(string keyword, string value, bool quoted = false)
    {
        string valueField = quoted ? ("'" + value + "'").PadRight(20) : value.PadLeft(20);
        return (keyword.PadRight(8) + "= " + valueField).PadRight(80)[..80];
    }

    private static byte[] HeaderBlock(IEnumerable<string> cards)
    {
        var sb = new StringBuilder();
        foreach (var c in cards) sb.Append(c);
        sb.Append("END".PadRight(80));
        int pad = (2880 - (sb.Length % 2880)) % 2880;
        sb.Append(new string(' ', pad));
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Pixels16BE(short[] px)
    {
        var data = new byte[px.Length * 2];
        for (int i = 0; i < px.Length; i++)
        {
            data[i * 2] = (byte)(px[i] >> 8);
            data[i * 2 + 1] = (byte)(px[i] & 0xFF);
        }
        int pad = (2880 - (data.Length % 2880)) % 2880;
        if (pad == 0) return data;
        var padded = new byte[data.Length + pad];
        data.CopyTo(padded, 0);
        return padded;
    }

    private static string WriteTemp(params byte[][] chunks)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fitsimage_hdu_{Guid.NewGuid():N}.fits");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks) fs.Write(c);
        return path;
    }

    [Fact]
    public void Load_ReadsImageFromPrimaryHdu()
    {
        var primary = HeaderBlock([
            Card("SIMPLE", "T"), Card("BITPIX", "16"), Card("NAXIS", "2"),
            Card("NAXIS1", "2"), Card("NAXIS2", "2"),
        ]);
        var path = WriteTemp(primary, Pixels16BE([10, 20, 30, 40]));
        try
        {
            var loaded = FitsImage.Load(path);
            Assert.Equal(2, loaded.Width);
            Assert.Equal(2, loaded.Height);
            Assert.Equal([10f, 20f, 30f, 40f], loaded.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ReadsImageFromExtension_WhenPrimaryIsEmpty()
    {
        // Empty primary (NAXIS=0, therefore no data segment at all), image in IMAGE extension.
        var primary = HeaderBlock([Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0")]);
        var ext = HeaderBlock([
            Card("XTENSION", "IMAGE", quoted: true), Card("BITPIX", "16"), Card("NAXIS", "2"),
            Card("NAXIS1", "2"), Card("NAXIS2", "2"), Card("PCOUNT", "0"), Card("GCOUNT", "1"),
        ]);
        var path = WriteTemp(primary, ext, Pixels16BE([7, 8, 9, 11]));
        try
        {
            var loaded = FitsImage.Load(path);
            Assert.Equal(2, loaded.Width);
            Assert.Equal(2, loaded.Height);
            Assert.Equal([7f, 8f, 9f, 11f], loaded.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_SkipsTableExtension_AndFindsLaterImage()
    {
        // A BINTABLE also reports NAXIS=2 (row length x row count). Treating "first HDU with
        // NAXIS>=2" as the image would decode this table's bytes as pixels and render garbage --
        // it must be skipped, and its data segment stepped over by exactly the right amount so
        // the following IMAGE extension is still found.
        var primary = HeaderBlock([Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0")]);
        var table = HeaderBlock([
            Card("XTENSION", "BINTABLE", quoted: true), Card("BITPIX", "8"), Card("NAXIS", "2"),
            Card("NAXIS1", "4"), Card("NAXIS2", "3"), Card("PCOUNT", "0"), Card("GCOUNT", "1"),
        ]);
        var tableData = new byte[2880];   // 4*3 = 12 bytes, padded to one block
        var ext = HeaderBlock([
            Card("XTENSION", "IMAGE", quoted: true), Card("BITPIX", "16"), Card("NAXIS", "2"),
            Card("NAXIS1", "2"), Card("NAXIS2", "2"), Card("PCOUNT", "0"), Card("GCOUNT", "1"),
        ]);
        var path = WriteTemp(primary, table, tableData, ext, Pixels16BE([1, 2, 3, 4]));
        try
        {
            var loaded = FitsImage.Load(path);
            Assert.Equal(2, loaded.Width);
            Assert.Equal(2, loaded.Height);
            Assert.Equal([1f, 2f, 3f, 4f], loaded.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenOnlyTableExtensionsPresent()
    {
        // Shape of a .fz: empty primary + a single BINTABLE holding tile-compressed pixels this
        // reader cannot decode. Returning empty is correct; rendering the table as pixels is not.
        var primary = HeaderBlock([Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0")]);
        var table = HeaderBlock([
            Card("XTENSION", "BINTABLE", quoted: true), Card("BITPIX", "8"), Card("NAXIS", "2"),
            Card("NAXIS1", "4"), Card("NAXIS2", "3"), Card("PCOUNT", "0"), Card("GCOUNT", "1"),
        ]);
        var path = WriteTemp(primary, table, new byte[2880]);
        try
        {
            var loaded = FitsImage.Load(path);
            Assert.Equal(0, loaded.Width);
            Assert.Equal(0, loaded.Height);
            Assert.Empty(loaded.Pixels);
        }
        finally { File.Delete(path); }
    }
}
