namespace Hermex;

/// <summary>The outcome of a settings update.</summary>
public enum HermexSettingsUpdateStatus
{
    /// <summary>The settings were validated, applied and persisted.</summary>
    Saved = 0,
    /// <summary>A value failed validation; nothing was changed.</summary>
    Invalid = 1,
    /// <summary>The host set <see cref="HermexOptions.AllowRuntimeSettings"/> to <c>false</c>; nothing was changed.</summary>
    Locked = 2,
    /// <summary>An <see cref="HermexEvents.OnSettingsChanging"/> hook rejected the change; nothing was changed.</summary>
    Rejected = 3,
}

/// <summary>The result of <see cref="IHermexSettings.UpdateAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="ListenerRestarted">Whether the SMTP or IMAP listener is re-binding because a port changed.</param>
/// <param name="Error">Why the update was not saved, or <c>null</c> when it was.</param>
public sealed record HermexSettingsUpdateResult(
    HermexSettingsUpdateStatus Status, bool ListenerRestarted, string? Error)
{
    /// <summary>Whether the settings were saved.</summary>
    public bool Success => Status == HermexSettingsUpdateStatus.Saved;

    internal static HermexSettingsUpdateResult Saved(bool restarted) => new(HermexSettingsUpdateStatus.Saved, restarted, null);
    internal static HermexSettingsUpdateResult Invalid(string error) => new(HermexSettingsUpdateStatus.Invalid, false, error);
    internal static HermexSettingsUpdateResult Locked() =>
        new(HermexSettingsUpdateStatus.Locked, false, "Runtime settings are disabled by the host application.");
    internal static HermexSettingsUpdateResult Rejected(string reason) => new(HermexSettingsUpdateStatus.Rejected, false, reason);
}

/// <summary>
/// Reads and changes Hermex's runtime-editable settings from host code. Updates go through the
/// same validation, <see cref="HermexOptions.AllowRuntimeSettings"/> lock and
/// <see cref="HermexEvents"/> hooks as the dashboard, with source <see cref="HermexSettingsChangeSource.Api"/>.
/// </summary>
public interface IHermexSettings
{
    /// <summary>Returns a copy of the current effective settings.</summary>
    HermexSettings GetCurrent();

    /// <summary>Validates, applies and persists <paramref name="settings"/>, re-binding any listener whose port changed.</summary>
    Task<HermexSettingsUpdateResult> UpdateAsync(HermexSettings settings, CancellationToken cancellationToken = default);
}
