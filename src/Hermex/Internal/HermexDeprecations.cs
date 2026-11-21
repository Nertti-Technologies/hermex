namespace Hermex.Internal;

/// <summary>Messages and diagnostic ids for deprecated public APIs, shared by their attributes.</summary>
internal static class HermexDeprecations
{
    /// <summary>
    /// Own id rather than CS0618, so a host building with warnings as errors can suppress just
    /// this while it migrates. Documented in the README upgrade note; keep the two in step.
    /// </summary>
    public const string EarlierNamesId = "HERMEX0001";

    public const string EarlierNamesMessage =
        "AddMail4Dev() and UseMail4Dev() are deprecated and will be removed in Hermex 3.0. Use AddHermex() and UseHermex(); they behave identically.";
}
