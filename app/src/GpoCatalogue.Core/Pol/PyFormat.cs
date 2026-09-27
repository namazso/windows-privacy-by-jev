using System.Globalization;
using System.Text;

namespace GpoCatalogue.Core.Pol;

/// <summary>
/// Python's <c>repr()</c> and <c>str()</c> for the value types the pipeline renders, so summaries and raw
/// lines produced here read exactly like the ones in the published dataset.
/// </summary>
public static class PyFormat
{
    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        string[] list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        long n => n.ToString(CultureInfo.InvariantCulture),
        ulong n => n.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string Repr(string s)
    {
        // Python prefers single quotes and switches to double quotes only when that avoids escaping.
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == quote || c == '\\')
            {
                sb.Append('\\').Append(c);
                continue;
            }
            switch (c)
            {
                case '\t': sb.Append("\\t"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
            }
            int codePoint = c;
            var units = 1;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, s[i + 1]);
                units = 2;
            }
            if (IsPrintable(codePoint))
                sb.Append(s, i, units);
            else if (codePoint < 0x100)
                sb.Append("\\x").Append(codePoint.ToString("x2", CultureInfo.InvariantCulture));
            else if (codePoint < 0x10000)
                sb.Append("\\u").Append(codePoint.ToString("x4", CultureInfo.InvariantCulture));
            else
                sb.Append("\\U").Append(codePoint.ToString("x8", CultureInfo.InvariantCulture));
            i += units - 1;
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Python's <c>str.isprintable()</c> for one code point.</summary>
    private static bool IsPrintable(int codePoint)
    {
        if (codePoint == ' ')
            return true;
        if (codePoint is >= 0xD800 and <= 0xDFFF)
            return false;
        return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    /// <summary>Python's <c>int(text.strip())</c> for the plain decimal strings a policy stores as text.</summary>
    public static bool TryParseInt(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
