using Hermex;
using Hermex.Background;
using Hermex.Configuration;
using Hermex.Dashboard;
using Hermex.Diagnostics;
using Hermex.Imap;
using Hermex.Internal;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration entry point for the Hermex in-process SMTP server and dashboard.</summary>
public static class HermexServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hermex SMTP server, SQLite mail store, background workers, SignalR
    /// hub and dashboard services. Pair this with <c>app.UseHermex()</c> in the request
    /// pipeline.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    public static IServiceCollection AddHermex(this IServiceCollection services,
        Action<HermexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HermexOptions();
        configure?.Invoke(options);
        options.Validate();

        // Core singletons.
        services.AddSingleton(options);
        services.AddSingleton<HermexRuntimeState>();

        // Ingestion queue + storage.
        services.AddSingleton<MailWriteQueue>();
        services.AddSingleton<IReceivedMailSink>(sp => sp.GetRequiredService<MailWriteQueue>());
        services.AddSingleton<IMailStore, SqliteMailStore>();

        // Diagnostics.
        services.AddSingleton<IHermexEventLog, HermexEventLog>();

        // Host hooks. TryAdd, so a host's own AddSingleton<HermexEvents, MyEvents>() wins
        // whether it is registered before or after this call (the last registration resolves).
        services.AddHttpContextAccessor();
        services.TryAddSingleton(options.Events);
        services.AddSingleton<HermexEventDispatcher>();

        // Upstream relay.
        services.AddSingleton<IMailRelayService, MailRelayService>();

        // Realtime dashboard updates.
        services.AddSingleton<IInboxNotifier, InboxNotifier>();
        services.AddSignalR();

        // Background workers. The SMTP and IMAP listeners are also registered as concrete
        // singletons so the settings service can re-bind them when a port changes at runtime.
        services.AddSingleton<SmtpServerService>();
        services.AddSingleton<ImapServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SmtpServerService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ImapServerService>());
        services.AddHostedService<MailPersistenceService>();
        services.AddHostedService<RetentionService>();

        // Runtime-editable settings, also exposed to host code.
        services.AddSingleton<HermexSettingsService>();
        services.AddSingleton<IHermexSettings>(sp => sp.GetRequiredService<HermexSettingsService>());

        // Dashboard UI (Razor Pages shipped inside this package). AddRazorPages is
        // idempotent, so this is safe even when the host application already uses it.
        var mvcBuilder = services.AddRazorPages();
        RegisterDashboardPart(mvcBuilder);

        // Authorization for Hermex's own pages only (see HermexPageConvention).
        services.Configure<RazorPagesOptions>(razor =>
            razor.Conventions.Add(new HermexPageConvention(options.Authorization)));

        return services;
    }

    /// <summary>
    /// Earlier name of <see cref="AddHermex"/>, kept so existing applications keep working; it
    /// behaves identically. Prefer <c>AddHermex()</c>: this name will be deprecated in a future release.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    public static IServiceCollection AddMail4Dev(this IServiceCollection services,
        Action<HermexOptions>? configure = null) =>
        services.AddHermex(configure);

    /// <summary>
    /// Ensures the Hermex assembly is registered as an MVC application part exactly once,
    /// so its dashboard Razor Pages are discovered without producing duplicate endpoints.
    /// </summary>
    private static void RegisterDashboardPart(IMvcBuilder mvcBuilder)
    {
        var assembly = typeof(HermexServiceCollectionExtensions).Assembly;

        var alreadyRegistered = mvcBuilder.PartManager.ApplicationParts
            .OfType<AssemblyPart>()
            .Any(part => part.Assembly == assembly);

        if (!alreadyRegistered)
            mvcBuilder.AddApplicationPart(assembly);
    }
}
