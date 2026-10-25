namespace ModernWigiDash.Widgets.Steam.Parsing;

/// <summary>
/// The zero-allocation VDF/ACF scanner: a span-based reader over the Valve
/// data format (libraryfolders.vdf, appmanifest_*.acf) that finds
/// <c>"key" "value"</c> pairs by index, with no regex, no per-token string
/// allocation, and no throw on corrupt or truncated input (a bad file simply
/// yields no values past the fault). The only allocation site is
/// <see cref="Unescape"/>, which materializes a field's string value once
/// (the manifest record needs the strings; the scan itself allocates
/// nothing).
/// </summary>
internal static class SteamVdfTokenizer
{
    /// <summary>
    /// Finds the next <c>"key"</c> entry at or after <paramref name="fromIndex"/>
    /// and reports its LEAF value span (the raw, still-escaped, unquoted
    /// bytes). A key whose value is a nested block (<c>"key" { ... }</c>)
    /// yields no leaf value: the block is skipped so a caller scanning for
    /// sibling keys keeps working. Corrupt input (an unterminated quote, a
    /// truncated block) yields no value and never throws.
    /// </summary>
    /// <param name="text">The file content span.</param>
    /// <param name="key">The key to find (ordinal, case-sensitive — VDF keys
    /// are case-sensitive: "appid" and "AppID" are distinct).</param>
    /// <param name="fromIndex">The scan start offset (0 for a fresh scan; the
    /// caller advances it past a found value to enumerate repeats).</param>
    /// <param name="valueStart">The value span's start offset (unquoted).</param>
    /// <param name="valueLength">The value span's length (unquoted).</param>
    /// <returns>True when a leaf value for the key was found.</returns>
    public static bool TryFindValue(ReadOnlySpan<char> text, string key, int fromIndex, out int valueStart, out int valueLength)
    {
        valueStart = 0;
        valueLength = 0;

        int index = Math.Clamp(fromIndex, 0, text.Length);
        while (index < text.Length)
        {
            // Advance to the next double quote (a key or value opener).
            int quote = text[index..].IndexOf('"');
            if (quote < 0)
                return false;

            quote += index;
            // Read the quoted key span (a backslash-escaped quote is part of
            // the span, not its terminator — vdf.cpp's read rule).
            int keyEnd = ReadQuotedSpan(text, quote);
            if (keyEnd < 0)
                return false; // unterminated quote: a corrupt tail, stop.

            ReadOnlySpan<char> candidateKey = text[(quote + 1)..keyEnd];
            if (!candidateKey.SequenceEqual(key.AsSpan()))
            {
                index = keyEnd + 1;
                continue;
            }

            // The key matched: the value is what follows (whitespace-skipped).
            int valueOpen = SkipWhitespace(text, keyEnd + 1);
            if (valueOpen >= text.Length)
                return false;

            char opener = text[valueOpen];
            if (opener == '{')
            {
                // A block value: skip to the matching close brace so the
                // caller's next scan starts past the block.
                int blockEnd = SkipBlock(text, valueOpen);
                if (blockEnd < 0)
                    return false; // truncated block: a corrupt tail, stop.
                index = blockEnd + 1;
                continue;
            }

            if (opener != '"')
            {
                // Not a leaf value (malformed entry): keep scanning.
                index = valueOpen + 1;
                continue;
            }

            int valueEnd = ReadQuotedSpan(text, valueOpen);
            if (valueEnd < 0)
                return false; // unterminated value quote: a corrupt tail.

            valueStart = valueOpen + 1;
            valueLength = valueEnd - valueOpen - 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a leaf value span as a uint (the ACF's appid / StateFlags
    /// shape). Zero-allocation (the span overload of the BCL parse); a
    /// non-numeric value yields false.
    /// </summary>
    public static bool TryParseUInt32(ReadOnlySpan<char> value, out uint result)
        => uint.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Parses a leaf value span as an Int64 (the ACF's byte counters and Unix
    /// timestamps). Zero-allocation; a non-numeric value yields false.
    /// </summary>
    public static bool TryParseInt64(ReadOnlySpan<char> value, out long result)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Parses a leaf value span as a double (the ACF's hours_on_record).
    /// Zero-allocation; a non-numeric value yields false.
    /// </summary>
    public static bool TryParseDouble(ReadOnlySpan<char> value, out double result)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Materializes a value span as a C# string, unescaping the VDF escapes
    /// (backslash, double quote, newline, tab). The ONE allocation site in
    /// the scanner: the manifest record's fields are strings, and each field
    /// is unescaped exactly once.
    /// </summary>
    public static string Unescape(ReadOnlySpan<char> value)
    {
        if (value.IndexOfAny(['\\', '"', 'n', 't']) < 0)
            return value.ToString(); // the common path: no escapes, one copy.

        var builder = new System.Text.StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                builder.Append(c);
                i++;
                continue;
            }

            char next = value[i + 1];
            switch (next)
            {
                case '\\': builder.Append('\\'); i += 2; break;
                case '"': builder.Append('"'); i += 2; break;
                case 'n': builder.Append('\n'); i += 2; break;
                case 't': builder.Append('\t'); i += 2; break;
                default: builder.Append(c); i++; break; // an unknown escape: keep the backslash.
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Advances from the opening quote at <paramref name="openIndex"/> to the
    /// index of the closing quote (escaped quotes do not terminate), or -1
    /// when the span is truncated mid-quote.
    /// </summary>
    private static int ReadQuotedSpan(ReadOnlySpan<char> text, int openIndex)
    {
        int i = openIndex + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2; // skip the escaped char (a truncated tail reads -1 below).
                continue;
            }
            if (c == '"')
                return i;
            i++;
        }

        return -1;
    }

    /// <summary>
    /// Skips whitespace from <paramref name="index"/> to the next content
    /// character (the VDF's key/value separator is any run of whitespace).
    /// </summary>
    private static int SkipWhitespace(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    /// <summary>
    /// Skips a nested block opened by the brace at <paramref name="openIndex"/>
    /// and returns the index of the matching close brace (string literals are
    /// respected, so a brace inside a quoted value does not count), or -1
    /// when the block is truncated.
    /// </summary>
    private static int SkipBlock(ReadOnlySpan<char> text, int openIndex)
    {
        int depth = 0;
        int i = openIndex;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                int quoteEnd = ReadQuotedSpan(text, i);
                if (quoteEnd < 0)
                    return -1;
                i = quoteEnd + 1;
                continue;
            }
            if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
            i++;
        }

        return -1;
    }
}
