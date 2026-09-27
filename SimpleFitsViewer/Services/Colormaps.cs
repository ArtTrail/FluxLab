using System;
using System.Collections.Generic;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Display colormaps for the image view (issue #13). Purely a viewing aid -- it changes nothing
/// about the pixel values or the photometry; it just maps the stretched 0-255 brightness of each
/// pixel to a colour instead of a shade of grey, which can make faint structure easier to spot.
/// Each map is defined by a handful of anchor colours and expanded to a 256-entry lookup table by
/// linear interpolation (mirrors how the old Python viewer used cached 256-entry LUTs, avoiding a
/// per-pixel float RGBA computation). Grayscale is the default and the identity map.
/// </summary>
public static class Colormaps
{
    public static readonly string[] Names = { "Gray", "Viridis", "Plasma", "Inferno", "Magma", "Hot" };

    // Anchor colours (RGB), evenly spaced across the 0..1 range, from the matplotlib maps.
    private static readonly Dictionary<string, (byte R, byte G, byte B)[]> Anchors = new()
    {
        ["Gray"]    = new[] { ((byte)0,(byte)0,(byte)0), ((byte)255,(byte)255,(byte)255) },
        ["Viridis"] = new[] { ((byte)68,(byte)1,(byte)84), ((byte)65,(byte)68,(byte)135), ((byte)42,(byte)120,(byte)142), ((byte)34,(byte)168,(byte)132), ((byte)122,(byte)209,(byte)81), ((byte)253,(byte)231,(byte)37) },
        ["Plasma"]  = new[] { ((byte)13,(byte)8,(byte)135), ((byte)106,(byte)0,(byte)168), ((byte)177,(byte)42,(byte)144), ((byte)225,(byte)100,(byte)98), ((byte)252,(byte)166,(byte)54), ((byte)240,(byte)249,(byte)33) },
        ["Inferno"] = new[] { ((byte)0,(byte)0,(byte)4), ((byte)66,(byte)10,(byte)104), ((byte)147,(byte)38,(byte)103), ((byte)221,(byte)81,(byte)58), ((byte)252,(byte)165,(byte)10), ((byte)252,(byte)255,(byte)164) },
        ["Magma"]   = new[] { ((byte)0,(byte)0,(byte)4), ((byte)59,(byte)15,(byte)112), ((byte)140,(byte)41,(byte)129), ((byte)222,(byte)73,(byte)104), ((byte)254,(byte)159,(byte)109), ((byte)252,(byte)253,(byte)191) },
        ["Hot"]     = new[] { ((byte)0,(byte)0,(byte)0), ((byte)255,(byte)0,(byte)0), ((byte)255,(byte)255,(byte)0), ((byte)255,(byte)255,(byte)255) },
    };

    /// <summary>Returns three 256-entry channel tables (R, G, B) for the named map; falls back to
    /// grayscale for an unknown name.</summary>
    public static (byte[] R, byte[] G, byte[] B) BuildLut(string name)
    {
        if (!Anchors.TryGetValue(name, out var a)) a = Anchors["Gray"];
        var r = new byte[256]; var g = new byte[256]; var b = new byte[256];
        int segs = a.Length - 1;
        for (int i = 0; i < 256; i++)
        {
            double p = i / 255.0 * segs;          // position in anchor space
            int lo = (int)Math.Floor(p);
            if (lo >= segs) lo = segs - 1;
            double f = p - lo;                     // 0..1 within the segment
            var c0 = a[lo]; var c1 = a[lo + 1];
            r[i] = (byte)Math.Round(c0.R + (c1.R - c0.R) * f);
            g[i] = (byte)Math.Round(c0.G + (c1.G - c0.G) * f);
            b[i] = (byte)Math.Round(c0.B + (c1.B - c0.B) * f);
        }
        return (r, g, b);
    }
}
