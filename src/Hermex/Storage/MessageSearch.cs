using System.Globalization;
using System.Text;

namespace Hermex.Storage;

/// <summary>
/// Parses the dashboard's search box (ADR-0003 D9) into parameterised SQL over the Messages table.
/// Grammar: whitespace-separated terms, AND-ed. A term is <c>[-]word</c>, <c>[-]"a phrase"</c> or
/// <c>[-]operator:value</c> (value may be quoted). Operators: <c>from: to: cc: subject: mailbox:</c> (text),
/// <c>is:read is:unread has:attachment has:html</c>, <c>larger: smaller:</c> (bytes, with k/kb/m/mb),
/// <c>before: after:</c> (yyyy-mm-dd or yyyy/mm/dd, UTC; after is inclusive, before exclusive).
/// An unknown operator or an unreadable value is searched as plain text.
/// </summary>
internal static class MessageSearch
{
    /// <summary>Longest query accepted (estimate: far above any hand-typed search); the rest is ignored.</summary>
    public const int MaxQueryLength = 512;

    /// <summary>Most terms used (estimate); further terms are ignored, bounding the SQL size.</summary>
    public const int MaxTerms = 16;

    private static readonly string[] AnyColumns =
        { "Subject", "FromDisplay", "FromAddress", "EnvelopeFrom", "ToDisplay", "EnvelopeTo", "CcDisplay" };
    private static readonly string[] FromColumns = { "FromDisplay", "FromAddress", "EnvelopeFrom" };
    private static readonly string[] ToColumns = { "ToDisplay", "EnvelopeTo" };
    private static readonly string[] CcColumns = { "CcDisplay" };
    private static readonly string[] SubjectColumns = { "Subject" };
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyy/MM/dd" };

    // Longest suffix first, so "kb" is not read as "b".
    private static readonly (string Suffix, long Factor)[] SizeSuffixes =
        { ("kb", 1024L), ("mb", 1024L * 1024), ("k", 1024L), ("m", 1024L * 1024), ("b", 1L) };

    /// <summary>
    /// Appends one SQL clause per term to <paramref name="clauses"/> and its values to
    /// <paramref name="parameters"/>. Column names come only from the fixed lists above, never from input.
    /// </summary>
    public static void AppendClauses(string? query, List<string> clauses, List<KeyValuePair<string, object>> parameters)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var index = 0;
        foreach (var token in Tokenize(query.Length > MaxQueryLength ? query[..MaxQueryLength] : query).Take(MaxTerms))
        {
            var name = $"@q{index++}";
            var clause = BuildClause(token, name, parameters);
            clauses.Add(token.Negated ? $"NOT ({clause})" : clause);
        }
    }

    private static string BuildClause(Token token, string name, List<KeyValuePair<string, object>> parameters)
    {
        switch (token.Operator)
        {
            case "from": return Text(FromColumns, token.Value, name, parameters);
            case "to": return Text(ToColumns, token.Value, name, parameters);
            case "cc": return Text(CcColumns, token.Value, name, parameters);
            case "subject": return Text(SubjectColumns, token.Value, name, parameters);
            case "mailbox":
                parameters.Add(new(name, Like(token.Value)));
                return "EXISTS (SELECT 1 FROM MailboxMessages sm JOIN Mailboxes sb ON sb.Id = sm.MailboxId " +
                       $"WHERE sm.MessageId = Messages.Id AND sb.Address LIKE {name} ESCAPE '\\')";
            case "is" when token.Value.Equals("read", StringComparison.OrdinalIgnoreCase): return "IsRead = 1";
            case "is" when token.Value.Equals("unread", StringComparison.OrdinalIgnoreCase): return "IsRead = 0";
            case "has" when token.Value.Equals("attachment", StringComparison.OrdinalIgnoreCase): return "AttachmentCount > 0";
            case "has" when token.Value.Equals("html", StringComparison.OrdinalIgnoreCase): return "HasHtml = 1";
            case "larger" when TryParseSize(token.Value, out var larger):
                parameters.Add(new(name, larger));
                return $"RawSize > {name}";
            case "smaller" when TryParseSize(token.Value, out var smaller):
                parameters.Add(new(name, smaller));
                return $"RawSize < {name}";
            // ReceivedAtUtc is stored as round-trip ISO-8601 UTC text, so a date prefix compares correctly.
            case "after" when TryParseDate(token.Value, out var after):
                parameters.Add(new(name, after));
                return $"ReceivedAtUtc >= {name}";
            case "before" when TryParseDate(token.Value, out var before):
                parameters.Add(new(name, before));
                return $"ReceivedAtUtc < {name}";
            default:
                return Text(AnyColumns, token.Operator is null ? token.Value : token.Raw, name, parameters);
        }
    }

    private static string Text(string[] columns, string value, string name, List<KeyValuePair<string, object>> parameters)
    {
        parameters.Add(new(name, Like(value)));
        // CcDisplay is NULL when a message has no Cc; COALESCE keeps such rows in "-cc:x" (NOT (NULL LIKE …) is
        // NULL, which would drop them). Every other column is NOT NULL.
        return "(" + string.Join(" OR ", columns.Select(c => c == "CcDisplay"
            ? $"COALESCE({c}, '') LIKE {name} ESCAPE '\\'"
            : $"{c} LIKE {name} ESCAPE '\\'")) + ")";
    }

    /// <summary>Wraps a value for a LIKE "contains" match, escaping LIKE's own wildcards.</summary>
    internal static string Like(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('%');
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.Append('%').ToString();
    }

    internal static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        var text = value.Trim().ToLowerInvariant();
        long multiplier = 1;
        foreach (var (suffix, factor) in SizeSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text[..^suffix.Length];
                multiplier = factor;
                break;
            }
        }
        // Whole or decimal numbers ("1.5mb"); compare before multiplying so a large value cannot overflow.
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || number > long.MaxValue / multiplier)
            return false;
        bytes = (long)decimal.Round(number * multiplier);
        return true;
    }

    internal static bool TryParseDate(string value, out string isoDate)
    {
        isoDate = string.Empty;
        if (!DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return false;
        isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    internal readonly record struct Token(string? Operator, string Value, bool Negated, string Raw);

    /// <summary>Splits the query into terms. Linear in the query length; no regular expressions.</summary>
    internal static IEnumerable<Token> Tokenize(string query)
    {
        var i = 0;
        while (i < query.Length)
        {
            while (i < query.Length && char.IsWhiteSpace(query[i]))
                i++;
            if (i >= query.Length)
                yield break;

            var negated = false;
            if (query[i] == '-' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]))
            {
                negated = true;
                i++;
            }

            var start = i;
            string? op = null;
            string value;
            if (query[i] == '"')
            {
                value = ReadQuoted(query, ref i);
            }
            else
            {
                var colon = -1;
                while (i < query.Length && !char.IsWhiteSpace(query[i]))
                {
                    if (query[i] == ':' && colon < 0)
                    {
                        colon = i;
                        if (i + 1 < query.Length && query[i + 1] == '"')
                        {
                            i++;
                            var quoted = ReadQuoted(query, ref i);
                            op = query[start..colon].ToLowerInvariant();
                            value = quoted;
                            goto Emit;
                        }
                    }
                    i++;
                }
                var word = query[start..i];
                if (colon > start && colon < i - 1)
                {
                    op = word[..(colon - start)].ToLowerInvariant();
                    value = word[(colon - start + 1)..];
                }
                else
                {
                    value = word;
                }
            }

        Emit:
            if (value.Length > 0)
                yield return new Token(op, value, negated, op is null ? value : op + ":" + value);
        }
    }

    private static string ReadQuoted(string query, ref int i)
    {
        i++; // opening quote
        var start = i;
        while (i < query.Length && query[i] != '"')
            i++;
        var value = query[start..i];
        if (i < query.Length)
            i++; // closing quote
        return value;
    }
}
