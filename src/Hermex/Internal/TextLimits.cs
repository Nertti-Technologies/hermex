namespace Hermex.Internal;

/// <summary>Shared helper for shortening text shown in warnings, findings and the dashboard.</summary>
internal static class TextLimits
{
    /// <summary>Returns <paramref name="value"/> cut to at most <paramref name="max"/> characters, ending in "…" when cut.</summary>
    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
