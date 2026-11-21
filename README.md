<div align="center">

<img src="header.png" alt="An in-process SMTP + IMAP server" />

</div>

**An in-process SMTP + IMAP server with an executive-grade web dashboard for .NET applications.**

Hermex captures the HTML and text email your application sends — locally, inside your own
process. No Docker container, no external service, no mail ever leaving your machine. Add one
service registration and one middleware call, and every message your app sends becomes
inspectable in a polished, real-time dashboard — or in a real mail client over IMAP.

```csharp
builder.Services.AddHermex();
app.UseHermex();
```

Point your application's mail client at `localhost:2525` and open `/hermex`.

---

## Why Hermex

Most development SMTP servers run as a *separate* process or
container. Hermex runs **inside your application** as a set of background services:

- **Zero external dependencies** — it ships as a single NuGet package.
- **Starts and stops with your app** — nothing to spin up or forget to shut down.
- **No impact on application performance** — the SMTP/IMAP listeners and storage run on
  background threads; your request pipeline never touches the mail store.

> **Upgrading from 2.0?** The registration methods are now `AddHermex()` and `UseHermex()`.
> The earlier names `AddMail4Dev()` and `UseMail4Dev()` still work, but are deprecated since 2.2.0
> (warning `HERMEX0001`) and will be removed in 3.0. Rename the two calls; nothing else changes.
> If you build with warnings as errors and need time, add `<NoWarn>$(NoWarn);HERMEX0001</NoWarn>`
> to your project for now. Suppressing `CS0618` does not cover this warning; use `HERMEX0001`.

## Features

- **Hand-rolled SMTP server** — RFC 5321 (`HELO`/`EHLO`, `MAIL`, `RCPT`, `DATA`, `AUTH`,
  `STARTTLS`, `RSET`, `NOOP`, `QUIT`), dot-unstuffing, size limits, concurrency control.
- **Hand-rolled IMAP server** — browse captured mail from a real client (Outlook,
  Thunderbird, Apple Mail): `LOGIN`, `LIST`, `SELECT`/`EXAMINE`, `FETCH`/`UID FETCH`,
  `SEARCH`, `STORE`, `EXPUNGE` with `ENVELOPE` and `BODYSTRUCTURE`.
- **Hand-rolled MIME parser** — nested multipart, base64 / quoted-printable, RFC 2047
  encoded-words, charsets, attachments and inline resources, malformed-input tolerance.
- **Automatic mailboxes** — every recipient address becomes its own mailbox; an `INBOX`
  catch-all holds everything. The dashboard has a mailbox sidebar; IMAP exposes each as a folder.
- **SQLite mail store** — WAL journaling and batched transactional writes that comfortably
  absorb bursts of marketing mail.
- **Executive web dashboard** — Razor Pages UI: master/detail inbox, HTML / Text / Source /
  Headers / Structure / Attachments / Session views, dark & light themes, live SignalR updates.
- **Runtime-editable settings** — change ports, retention, relay and more from the dashboard;
  saved to the database and applied immediately. A port change re-binds the listener live.
- **TLS** — `STARTTLS` and implicit TLS, with a supplied or auto-generated certificate.
- **Per-message SMTP transcript**, **MIME structure tree**, **upstream relay**, **diagnostic
  logs**, **port-conflict auto-resolution** and **retention by count and age**.

## Installation

Add a project or package reference to `Hermex`, then register it.

### ASP.NET Core web application

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHermex(options =>
{
    options.SmtpPort = 2525;
    options.EnableImap = true;     // optional — expose captured mail over IMAP
    options.ImapPort = 1143;
});

var app = builder.Build();

app.UseHermex();   // dashboard at /hermex, API at /hermex/api, hub at /hermex/hub

app.Run();
```

Your application sends mail exactly as it always has — for example with the standard
`System.Net.Mail.SmtpClient` or MailKit pointed at `localhost:2525`. Hermex captures it.

### Console / worker application

The dashboard is ASP.NET Core middleware, so a console process hosts it through the
lightweight `WebApplication` host. The SMTP/IMAP servers are background services and run under
any generic host.

## Configuration

All configuration is done through the `AddHermex` callback.

| Option | Default | Description |
| --- | --- | --- |
| `SmtpPort` | `2525` | TCP port for the SMTP server. |
| `EnableImap` / `ImapPort` | `false` / `1143` | Enable the IMAP server and choose its port. |
| `ListenAddress` | `loopback` | Interface to bind (`loopback`, `any`, or an IP). |
| `ServerHostName` | `hermex.local` | SMTP greeting host name. |
| `AutoResolvePortConflict` | `true` | Probe subsequent ports when one is busy. |
| `MaxConcurrentConnections` | `64` | Concurrency cap for connections. |
| `MaxMessageSizeBytes` | `25 MB` | Largest message accepted. |
| `RequireAuthentication` | `false` | Require `AUTH` (any credentials are accepted). |
| `CaptureSessionTranscript` | `true` | Record the SMTP conversation per message. |
| `TlsMode` | `None` | `None`, `StartTls`, or `Implicit`. |
| `DatabasePath` | `{ContentRoot}/hermex/hermex.db` | SQLite file location. |
| `RetentionMaxMessages` / `RetentionMaxAge` | `5000` / _none_ | Retention policy. |
| `DefaultTheme` | `System` | `System`, `Light`, or `Dark`. |
| `Relay` | _disabled_ | Upstream relay settings. `Relay.AllowedHosts` limits which hosts relay may connect to (empty = any). |
| `AllowRuntimeSettings` | `true` | Let the Settings page change settings and re-apply saved settings at startup. `false` makes your code options final. |
| `Authorization` | _no policies_ | Viewer / Manager / Admin policies for the dashboard, API and hub. |
| `Events` | _no-ops_ | Hooks to veto or audit settings changes, relay and deletes, and to observe stored mail. |

Most of these are also editable at runtime from the dashboard's **Settings** page — including
the SMTP and IMAP ports, which re-bind their listener live. Host code can do the same through
`IHermexSettings`.

## Running Hermex for a team

By default Hermex is a single-developer tool on `localhost`: anyone who can reach the dashboard
can read mail, change settings and switch on relay. Before exposing it to other people (a test
or staging server behind your own sign-in), keep control with these options:

```csharp
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("SignedIn", p => p.RequireAuthenticatedUser());
    o.AddPolicy("ManageConfiguration", p => p.RequireRole("admin"));
});

builder.Services.AddHermex(o =>
{
    o.SmtpPort = 2525;
    o.ListenAddress = "loopback";              // SMTP only for the app on this machine
    o.AutoResolvePortConflict = false;         // fail loudly rather than move the port
    o.AllowRuntimeSettings = false;            // your options are final; Settings page is read-only
    o.Relay.AllowedHosts.Add("smtp.example.com");
    o.Relay.ValidateCertificate = true;        // relay over STARTTLS only, with a valid certificate

    o.Authorization.ViewerPolicy  = "SignedIn";            // read mail, live updates, status
    o.Authorization.ManagerPolicy = "SignedIn";            // delete, clear inbox, relay
    o.Authorization.AdminPolicy   = "ManageConfiguration"; // Settings and Logs pages and APIs

    o.Events.OnRelaying = ctx =>
    {
        ctx.Reject("Test mail is never forwarded.");
        return Task.CompletedTask;
    };
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseHermex();
```

- **Tiers.** A tier left `null` uses the policy of the tier below it, so Settings and Logs are
  never less protected than the inbox. With no policies at all, nothing changes.
- **Endpoint metadata.** Every Hermex endpoint carries `HermexEndpointMetadata(Tier)`, so your own
  middleware or policies can target tiers instead of URLs.
- **Startup checks.** `UseHermex()` throws `HermexConfigurationException` if a policy name is not
  registered or there is no authentication scheme to challenge with.
- **Scripting the API.** Once a policy is set, POST/PUT/DELETE calls to `/hermex/api` must send the
  header `X-Hermex-Request: 1` (the dashboard does). This stops other sites from triggering them
  with a signed-in user's cookie.
- **Live-update hub.** Once a policy is set, the hub at `/hermex/hub` refuses browser requests whose
  `Origin` is another site, so another page cannot open it with a signed-in user's credentials. Pages
  on the same host always work; if a reverse proxy rewrites the `Host` header, list the public origin
  in `o.Authorization.AllowedHubOrigins`.
- **Status.** `GET /hermex/api/status` returns the database path and last error only to callers
  who pass the Admin tier.
- **Hooks.** Veto hooks (`OnSettingsChanging`, `OnRelaying`, `OnDeleting`) run before the action,
  including automatic relay and startup; an exception counts as a rejection. Notification hooks
  (`OnSettingsChanged`, `OnMessageStored`) run after it and never break capture. `OnMessageStored`
  is delivered from a bounded background queue; if your hook falls far behind, events are dropped
  and a warning is logged. To use DI services in hooks, subclass `HermexEvents`, override its
  methods and register it with `services.AddSingleton<HermexEvents, MyEvents>()`.
  Don't inject `IHermexSettings` into that subclass's constructor (it depends on the events),
  and don't call `UpdateAsync` from a settings hook; change `ctx.Proposed` instead.

## Browsing captured mail over IMAP

With `EnableImap = true`, point any mail client at `localhost:1143` (any credentials are
accepted). Each recipient address appears as a folder, plus an `INBOX` that holds everything.
Clients can read, search, flag and expunge messages.

## Mailboxes

Every captured message is filed into the `INBOX` catch-all **and** a mailbox for each of its
recipient addresses. The dashboard's mailbox sidebar filters the inbox; over IMAP each mailbox
is a selectable folder.

## Architecture

```
   your app sends mail
          |
          v
  +------------------+   raw bytes    +------------------+
  |  SMTP server     | -------------> |  write queue     |  (bounded Channel)
  |  (background)    |   250 OK fast  |  (in memory)     |
  +------------------+                +--------+---------+
                                               | batches
                                               v
                                      +------------------+      +--------------+
                                      |  persistence     |----->|  SQLite      |<--- IMAP server
                                      |  service         |      |  mail store  |<--- dashboard
                                      +------------------+      +--------------+
```

The SMTP path does the minimum — accept the bytes, return `250 OK`, hand off to an in-memory
queue. MIME parsing and disk I/O happen on a background worker in batched transactions, so
mail capture stays fast and the host application is never blocked.

### Will SQLite handle a thousand emails?

Comfortably. A 1,000-message marketing burst is a small workload for SQLite. WAL journaling
plus batched transactions (default 64 messages per transaction) sustain thousands of inserts
per second, and the queue absorbs the burst so the SMTP path stays fast. Retention keeps the
database file bounded.

## Samples

| Sample | Description |
| --- | --- |
| `samples/Hermex.Sample.Web` | An ASP.NET Core app with a page that sends test HTML, text, attachment and bulk emails. |
| `samples/Hermex.Sample.Console` | A console/worker process that hosts Hermex and sends sample mail on startup. |

```bash
dotnet run --project samples/Hermex.Sample.Web
# then browse to http://localhost:5170 and http://localhost:5170/hermex
```

## Building and testing

```bash
dotnet build Hermex.slnx
dotnet test  tests/Hermex.Tests/Hermex.Tests.csproj
```

The library multi-targets **net10.0** and **net11.0**, so it requires .NET 10 or later.

The build runs the recommended .NET analyzers (`AnalysisLevel=latest-recommended`) and treats every
warning as an error, including NuGet vulnerability advisories. Deliberate opt-outs are recorded,
with their reason, in `.editorconfig`.

## What's new in 2.2.0

- **Deprecated:** `AddMail4Dev()` and `UseMail4Dev()` are marked `[Obsolete]` with warning
  `HERMEX0001`; use `AddHermex()` and `UseHermex()`. They still work, and are removed in 3.0.

## What's new in 2.1.0

Everything is additive and every default keeps 2.0.0 behaviour.

- **New method names:** `AddHermex()` and `UseHermex()`. `AddMail4Dev()` and `UseMail4Dev()` keep
  working unchanged and will be deprecated in a future release (done in 2.2.0).
- **Host control:** `AllowRuntimeSettings`, authorization tiers, a relay host allow-list,
  `HermexEvents` hooks and the public `IHermexSettings`. See [Running Hermex for a team](#running-hermex-for-a-team).
- **Diagnostics:** saved settings that fail validation are now skipped with a warning instead of silently.

## What's new in 2.0.0

- **Licence:** Hermex is now licensed under the Apache License 2.0. Version 1.0.0 remains available under MIT.
- **Frameworks:** adds .NET 11 and drops .NET 8 and .NET 9, which reached end of support on 2026-11-10. This is the breaking change.
- **Security:** the bundled SQLite is updated to fix [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q). Version 1.0.0 carries the vulnerable SQLite on every target, so apps on .NET 8 or .NET 9 that cannot upgrade should reference a patched `Microsoft.Data.Sqlite` themselves.

## License

Apache License 2.0; see [LICENSE](LICENSE).

Version 1.0.0 was released under the MIT License and remains available under those terms.
