using System.Globalization;
using System.Linq;

namespace FitsPhotometry.Core.Fits;

/// <summary>
/// Parses data-sourced and user-typed numeric strings without trusting (or requiring) any
/// particular OS locale. Ported verbatim from TransitLab's NumericParseService: any "," or "."
/// is always treated as a decimal mark, never as thousands grouping, so "12,345" (comma-decimal
/// locales) and "12.345" (English) both parse to the same correct value with no locale detection.
/// </summary>
public static class NumericParse
{
    public static bool TryParse(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return double.TryParse(Normalize(s), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static string Normalize(string s)
    {
        var t = s.Trim();
        t = t.Replace(" ", "").Replace(" ", "").Replace("'", "");

        int commaCount  = t.Count(c => c == ',');
        int periodCount = t.Count(c => c == '.');

        if (commaCount > 0 && periodCount > 0)
        {
            bool commaIsDecimal = t.LastIndexOf(',') > t.LastIndexOf('.');
            char groupChar = commaIsDecimal ? '.' : ',';
            char decimalChar = commaIsDecimal ? ',' : '.';
            t = t.Replace(groupChar.ToString(), "");
            t = ReplaceLast(t, decimalChar, '.');
        }
        else if (commaCount == 1)
        {
            t = t.Replace(',', '.');
        }
        else if (commaCount > 1)
        {
            t = t.Replace(",", "");
        }

        return t;
    }

    private static string ReplaceLast(string s, char oldChar, char newChar)
    {
        int idx = s.LastIndexOf(oldChar);
        return idx < 0 ? s : s[..idx] + newChar + s[(idx + 1)..];
    }
}
