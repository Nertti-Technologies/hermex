namespace Hermex.Internal;

/// <summary>
/// Address patterns shared by fault rules (ADR-0003 D11) and relay recipient rules (D14): one matcher, so both
/// features read <c>*@example.test</c> the same way.
/// </summary>
internal static class AddressPattern
{
    /// <summary>Longest pattern accepted anywhere (estimate: RFC 5321's 256-octet path plus wildcards).</summary>
    public const int MaxLength = 320;

    /// <summary>
    /// Case-insensitive glob: <c>*</c> any run, <c>?</c> one character. Two pointers with star backtracking, no
    /// recursion: worst case pattern × text comparisons (320 × 4,096 ≈ 1.3 M per rule, at the Match and SMTP command
    /// limits), never exponential.
    /// </summary>
    public static bool IsMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') // a wildcard first, even when the text has a literal '*'
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
