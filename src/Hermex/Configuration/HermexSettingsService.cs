using System.Text.Json;
using Hermex.Imap;
using Hermex.Internal;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.Logging;

namespace Hermex.Configuration;

/// <summary>
/// Reads, validates, applies and persists the runtime-editable <see cref="HermexSettings"/>.
/// Port changes re-bind the affected listener live, with no host restart. Every change, from the
/// dashboard, from host code or from the database at startup, passes the
/// <see cref="HermexOptions.AllowRuntimeSettings"/> lock, validation and the settings hooks.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim holds no unmanaged resource unless AvailableWaitHandle is used; disposing it at shutdown would make an in-flight update throw.")]
internal sealed class HermexSettingsService : IHermexSettings
{
    private const string SettingsKey = "hermex.settings";

    private readonly HermexOptions _options;
    private readonly IMailStore _store;
    private readonly SmtpServerService _smtpServer;
    private readonly ImapServerService _imapServer;
    private readonly HermexEventDispatcher _events;
    private readonly ILogger<HermexSettingsService> _logger;
    private readonly object _gate = new();

    // One change at a time: a change reads the current settings, awaits the hooks, persists and
    // applies, so overlapping changes would persist one and apply another.
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    // Identifies the running change on its async flow. The lock is not re-entrant, so a settings
    // hook that calls UpdateAsync would otherwise wait on itself forever. A token (not a bool) so
    // work a hook forks, which inherits the flow, is not refused once that change has finished.
    private readonly AsyncLocal<object?> _updateFlow = new();
    private object? _activeUpdate;

    public HermexSettingsService(
        HermexOptions options,
        IMailStore store,
        SmtpServerService smtpServer,
        ImapServerService imapServer,
        HermexEventDispatcher events,
        ILogger<HermexSettingsService> logger)
    {
        _options = options;
        _store = store;
        _smtpServer = smtpServer;
        _imapServer = imapServer;
        _events = events;
        _logger = logger;
    }

    /// <summary>Returns the current effective settings.</summary>
    public HermexSettings GetCurrent()
    {
        lock (_gate)
        {
            var (allowedRecipients, blockedRecipients) = _options.Relay.DashboardRecipientRules();
            return new HermexSettings
            {
                SmtpPort = _options.SmtpPort,
                ImapPort = _options.ImapPort,
                EnableImap = _options.EnableImap,
                ServerHostName = _options.ServerHostName,
                MaxMessageSizeBytes = _options.MaxMessageSizeBytes,
                RequireAuthentication = _options.RequireAuthentication,
                CaptureSessionTranscript = _options.CaptureSessionTranscript,
                RetentionMaxMessages = _options.RetentionMaxMessages,
                RetentionMaxAgeHours = _options.RetentionMaxAge?.TotalHours ?? 0,
                DashboardTitle = _options.DashboardTitle,
                DefaultTheme = _options.DefaultTheme.ToString(),
                RelayEnabled = _options.Relay.Enabled,
                RelayAutomatic = _options.Relay.AutomaticRelay,
                RelayHost = _options.Relay.Host,
                RelayPort = _options.Relay.Port,
                RelayAllowedRecipients = allowedRecipients.ToList(),
                RelayBlockedRecipients = blockedRecipients.ToList(),
            };
        }
    }

    /// <summary>
    /// Loads persisted settings and applies them to the live options. Called at startup,
    /// before the listeners bind, so no restart is needed. When the host has turned runtime
    /// settings off, the stored row is ignored but kept, so turning them back on restores it.
    /// </summary>
    public async Task LoadPersistedAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.AllowRuntimeSettings)
        {
            _logger.LogInformation(
                "Hermex ignored any persisted runtime settings because HermexOptions.AllowRuntimeSettings is false.");
            return;
        }

        try
        {
            var stored = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!stored.TryGetValue(SettingsKey, out var json) || string.IsNullOrWhiteSpace(json))
                return;

            var settings = JsonSerializer.Deserialize<HermexSettings>(json);
            if (settings is null)
            {
                _logger.LogWarning("Hermex skipped the persisted runtime settings because they are empty.");
                return;
            }

            var result = await ApplyAsync(settings, HermexSettingsChangeSource.Persisted, cancellationToken)
                .ConfigureAwait(false);
            switch (result.Status)
            {
                case HermexSettingsUpdateStatus.Saved:
                    _logger.LogInformation("Hermex applied persisted runtime settings.");
                    break;
                case HermexSettingsUpdateStatus.Rejected:
                    _logger.LogWarning("Hermex skipped the persisted runtime settings because a host hook rejected them.");
                    break;
                default:
                    _logger.LogWarning("Hermex skipped the persisted runtime settings because they are invalid: {Error}",
                        result.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermex could not load persisted settings; using the configured defaults.");
        }
    }

    /// <inheritdoc />
    public Task<HermexSettingsUpdateResult> UpdateAsync(HermexSettings settings,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings, HermexSettingsChangeSource.Api, cancellationToken);

    /// <summary>Validates, applies and persists settings, re-binding any listener whose port changed.</summary>
    public async Task<HermexSettingsUpdateResult> UpdateAsync(HermexSettings settings,
        HermexSettingsChangeSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!_options.AllowRuntimeSettings)
            return HermexSettingsUpdateResult.Locked();

        return await ApplyAsync(settings, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates, runs the hooks and applies <paramref name="settings"/>. Persists and re-binds
    /// listeners for live changes; at startup (<see cref="HermexSettingsChangeSource.Persisted"/>)
    /// the stored row is already there and the listeners have not bound yet.
    /// </summary>
    private async Task<HermexSettingsUpdateResult> ApplyAsync(HermexSettings settings,
        HermexSettingsChangeSource source, CancellationToken cancellationToken)
    {
        if (_updateFlow.Value is { } flow && ReferenceEquals(flow, Volatile.Read(ref _activeUpdate)))
        {
            throw new InvalidOperationException(
                "A settings hook cannot update settings; change HermexSettingsChangingContext.Proposed instead.");
        }

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = new object();
            Volatile.Write(ref _activeUpdate, token);
            _updateFlow.Value = token;
            return await ApplyLockedAsync(settings, source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeUpdate, null);
            _updateLock.Release();
        }
    }

    private async Task<HermexSettingsUpdateResult> ApplyLockedAsync(HermexSettings settings,
        HermexSettingsChangeSource source, CancellationToken cancellationToken)
    {
        // Work on a copy so a hook never mutates the caller's object.
        var proposed = settings.Clone();
        var error = Validate(proposed);
        if (error is not null)
            return HermexSettingsUpdateResult.Invalid(error);

        var previous = GetCurrent();
        var changing = new HermexSettingsChangingContext
        {
            Current = previous.Clone(),
            Proposed = proposed,
            Source = source,
            HttpContext = _events.HttpContext,
            CancellationToken = cancellationToken,
        };
        if (!await _events.SettingsChangingAsync(changing).ConfigureAwait(false))
            return HermexSettingsUpdateResult.Rejected(changing.RejectionReason!);

        // The hook may have changed the proposal.
        error = Validate(proposed);
        if (error is not null)
            return HermexSettingsUpdateResult.Invalid(error);

        // Persist first: if saving fails, the live options are left as they were.
        if (source != HermexSettingsChangeSource.Persisted)
        {
            await _store.SaveSettingsAsync(
                new Dictionary<string, string> { [SettingsKey] = JsonSerializer.Serialize(proposed) },
                cancellationToken).ConfigureAwait(false);
        }

        bool smtpRestart, imapRestart;
        lock (_gate)
        {
            var live = source != HermexSettingsChangeSource.Persisted;
            smtpRestart = live && proposed.SmtpPort != _options.SmtpPort;
            imapRestart = live && (proposed.ImapPort != _options.ImapPort || proposed.EnableImap != _options.EnableImap);
            ApplyToOptions(proposed);
        }

        // ADR-0003 OD4: relay opened to anyone from the dashboard (start-up is covered by UseHermex).
        if (source != HermexSettingsChangeSource.Persisted && _options.Relay.Enabled && _options.Relay.RecipientRules().AllowsAnyone)
            _logger.LogWarning("Hermex: relay is enabled with no recipient allow-list, so captured mail can be relayed to any address.");

        if (smtpRestart)
        {
            _logger.LogInformation("Hermex re-binding the SMTP listener after a settings change.");
            _smtpServer.RequestRestart();
        }
        if (imapRestart)
        {
            _logger.LogInformation("Hermex re-binding the IMAP listener after a settings change.");
            _imapServer.RequestRestart();
        }

        await _events.SettingsChangedAsync(new HermexSettingsChangedContext
        {
            Previous = previous,
            Current = GetCurrent(),
            Source = source,
            ListenerRestarted = smtpRestart || imapRestart,
            HttpContext = _events.HttpContext,
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);

        return HermexSettingsUpdateResult.Saved(smtpRestart || imapRestart);
    }

    private void ApplyToOptions(HermexSettings settings)
    {
        _options.SmtpPort = settings.SmtpPort;
        _options.ImapPort = settings.ImapPort;
        _options.EnableImap = settings.EnableImap;
        _options.ServerHostName = settings.ServerHostName;
        _options.MaxMessageSizeBytes = settings.MaxMessageSizeBytes;
        _options.RequireAuthentication = settings.RequireAuthentication;
        _options.CaptureSessionTranscript = settings.CaptureSessionTranscript;
        _options.RetentionMaxMessages = settings.RetentionMaxMessages;
        _options.RetentionMaxAge = settings.RetentionMaxAgeHours > 0
            ? TimeSpan.FromHours(settings.RetentionMaxAgeHours)
            : null;
        _options.DashboardTitle = settings.DashboardTitle;
        _options.DefaultTheme = Enum.TryParse<HermexTheme>(settings.DefaultTheme, ignoreCase: true, out var theme)
            ? theme
            : HermexTheme.System;
        _options.Relay.Enabled = settings.RelayEnabled;
        _options.Relay.AutomaticRelay = settings.RelayAutomatic;
        _options.Relay.Host = settings.RelayHost;
        _options.Relay.Port = settings.RelayPort;
        _options.Relay.SetDashboardRecipientRules(settings.RelayAllowedRecipients, settings.RelayBlockedRecipients);
    }

    private string? Validate(HermexSettings s)
    {
        if (s.SmtpPort is < 1 or > 65535)
            return "SMTP port must be between 1 and 65535.";
        if (s.ImapPort is < 1 or > 65535)
            return "IMAP port must be between 1 and 65535.";
        if (s.EnableImap && s.ImapPort == s.SmtpPort)
            return "The SMTP and IMAP ports must be different.";
        if (s.MaxMessageSizeBytes < 1024)
            return "Maximum message size must be at least 1024 bytes.";
        if (s.RetentionMaxMessages < 0)
            return "Retention message count cannot be negative.";
        if (s.RetentionMaxAgeHours < 0)
            return "Retention age cannot be negative.";
        if (string.IsNullOrWhiteSpace(s.ServerHostName))
            return "The server host name is required.";
        if (s.RelayEnabled && string.IsNullOrWhiteSpace(s.RelayHost))
            return "A relay host is required when relay is enabled.";
        if (s.RelayEnabled && s.RelayPort is < 1 or > 65535)
            return "The relay port must be between 1 and 65535.";
        if (s.RelayEnabled && !_options.Relay.IsHostAllowed(s.RelayHost))
            return "The relay host is not in the host application's list of allowed relay hosts.";
        // An explicit JSON null (or a hook) can still null them; treat as "no dashboard restriction".
        s.RelayAllowedRecipients ??= new List<string>();
        s.RelayBlockedRecipients ??= new List<string>();
        return HermexRelayOptions.ValidateRecipientPatterns(s.RelayAllowedRecipients, "Allowed relay recipients")
               ?? HermexRelayOptions.ValidateRecipientPatterns(s.RelayBlockedRecipients, "Blocked relay recipients");
    }
}
