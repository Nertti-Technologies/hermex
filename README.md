![An in-process SMTP + IMAP server](https://raw.githubusercontent.com/Nertti-Technologies/hermex/138701e5636c98aa67c41d338cb3a1817fb7206a/header.png)

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
- **Mailbox management** — delete one mailbox, reset everything, delete or mark rows one at a time
  or in bulk, and import `.eml` files by dropping them on the inbox.
- **Search operators** — `from:`, `to:`, `cc:`, `subject:`, `mailbox:`, `is:read`, `has:attachment`,
  `larger:`, `before:` and more, with `"quoted phrases"` and `-` to exclude.
- **HTML checks and previews** — common email pitfalls flagged per message, phone/tablet/desktop
  and dark previews, and the authentication results (SPF, DKIM, DMARC) the message carries.
- **Testing support** — a wait-for-message API and the `Hermex.Testing` package.
- **Signed webhooks**, **SMTP fault injection** and **relay recipient rules**.

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
| `Events` | _no-ops_ | Hooks to veto or audit settings changes, relay, deletes, imports and fault changes, and to observe stored mail. |
| `Relay.AllowedRecipients` / `Relay.BlockedRecipients` | _empty_ | Recipient patterns relay may / may never send to. See [Relay recipient rules](#relay-recipient-rules). |
| `Webhooks` | _no endpoints_ | Signed notices to your URLs when mail is captured. See [Webhooks](#webhooks). |
| `Faults` | _disabled_ | SMTP fault injection for testing failure handling. See [Fault injection](#fault-injection). |

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
  header `X-Hermex-Request: 1` (the dashboard does). Relay, mark-read, import and webhook resume need it
  even without a policy. This stops other sites from triggering them
  with a signed-in user's cookie.
- **Live-update hub.** Once a policy is set, the hub at `/hermex/hub` refuses browser requests whose
  `Origin` is another site, so another page cannot open it with a signed-in user's credentials. Pages
  on the same host always work; if a reverse proxy rewrites the `Host` header, list the public origin
  in `o.Authorization.AllowedHubOrigins`.
- **Status.** `GET /hermex/api/status` returns the database path and last error only to callers
  who pass the Admin tier.
- **Hooks.** Veto hooks (`OnSettingsChanging`, `OnRelaying`, `OnDeleting`, `OnImporting`,
  `OnFaultsChanging`, `OnWebhookSending`) run before the action,
  including automatic relay and startup; an exception counts as a rejection. Notification hooks
  (`OnSettingsChanged`, `OnMessageStored`) run after it and never break capture. `OnMessageStored`
  is delivered from a bounded background queue; if your hook falls far behind, events are dropped
  and a warning is logged. To use DI services in hooks, subclass `HermexEvents`, override its
  methods and register it with `services.AddSingleton<HermexEvents, MyEvents>()`.
  Don't inject `IHermexSettings` into that subclass's constructor (it depends on the events),
  and don't call `UpdateAsync` from a settings hook; change `ctx.Proposed` instead.

## Testing with Hermex

`GET /hermex/api/messages/wait` holds the request until a matching message is stored (or the
timeout passes, then `204`). The `Hermex.Testing` package wraps it for any test framework:

```csharp
var hermex = new HermexClient(app.CreateClient());   // e.g. from WebApplicationFactory
var mark = await hermex.MarkAsync();                 // ignore mail sent before this point
await SignUpAsync("ann@example.test");               // your code sends the email
var message = await hermex.WaitForMessageAsync(
    new HermexMessageCriteria { To = "ann@example.test", Subject = "Confirm", After = mark },
    timeout: TimeSpan.FromSeconds(10));
var links = await hermex.GetLinksAsync(message.Id);
```

See the [Hermex.Testing README](https://github.com/Nertti-Technologies/hermex/blob/main/src/Hermex.Testing/README.md) for the full API.

## Webhooks

Hermex can POST a signed JSON notice to your URLs whenever a message is captured:

```csharp
o.Webhooks.Endpoints.Add(new HermexWebhookEndpoint
{
    Url = new Uri("https://ci.example.com/hooks/mail"),
    Secret = builder.Configuration["Hermex:WebhookSecret"]!,   // "whsec_" + base64 of 24–64 random bytes
});
```

- **Payload.** `type` is `message.received`; `data` holds the id, time, sender, recipients, subject,
  size and an API path. Metadata only: never bodies or attachments. Long lists are capped and
  marked `truncated`.
- **Signing.** [Standard Webhooks](https://www.standardwebhooks.com/): `webhook-id`, `webhook-timestamp`
  and `webhook-signature` (HMAC-SHA256) headers, so its receiver libraries verify Hermex notices.
  `PreviousSecret` signs with a second key while you rotate.
- **Delivery.** At-least-once, in memory, retried at 5 s, 30 s, 2 min and 10 min. A `410 Gone`
  disables the endpoint; 20 failures in a row pause it until resumed on the **Logs** page.
- **Network policy.** Cloud-metadata and link-local addresses are always refused, and redirects are
  not followed. Loopback and private networks are allowed only in Development unless you set
  `NetworkPolicy = AllowPrivate`.
- Endpoints are set in code or configuration only; the dashboard shows the delivery log.

## Fault injection

To test how your app handles a failing mail server, Hermex can reject commands, slow replies or
drop the connection at any SMTP step:

```csharp
o.Faults.Enabled = true;
o.Faults.Rules.Add(new HermexFaultRule { Stage = HermexSmtpStage.RcptTo, Match = "*@bounce.test", ReplyCode = 550 });
o.Faults.Rules.Add(new HermexFaultRule { Stage = HermexSmtpStage.Data, ReplyCode = 451, EveryNth = 3 });
```

- A message can also ask for its own fault with an `X-Hermex-Fault: 451`, `drop` or `delay=5s` header.
- Faults work only in the Development environment; set `AllowOutsideDevelopment` to allow them
  elsewhere. On a shared server, one rule fails everyone's mail.
- Admins can change the rules on the **Settings** page (kept in memory only, vetoable with
  `OnFaultsChanging`). Every injected fault is written to the diagnostic log.

## Relay recipient rules

Keep captured mail away from real people when relay is on:

```csharp
o.Relay.AllowedRecipients.Add("*@example.com");   // * any characters, ? one character, ignoring case
o.Relay.BlockedRecipients.Add("ceo@example.com"); // blocked beats allowed
```

- The rules apply to manual and automatic relay. Recipients that don't match are left out; a message
  with none left is not relayed.
- The lists in code are a fixed limit. The **Settings** page has its own lists, which can only narrow
  them further.
- Only plain `name@domain` addresses are relayed; routing forms such as `a%b@c` are refused.
- With no allow-list, relay goes to anyone, as in 2.2, and Hermex logs a warning. A future major
  version will relay to nobody without one.

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

## What's new in 2.3.0

New features are off or additive by default. The behaviour changes are listed under **Changed**.

- **Mailboxes:** delete one mailbox (Manager) or reset all captured mail (Admin, typed confirmation).
  Both run through `OnDeleting`, which now says what is being deleted.
- **Inbox:** per-row delete, multi-select and bulk delete / mark read; `.eml` import by drag and drop.
- **Search operators:** `from:`, `to:`, `cc:`, `subject:`, `mailbox:`, `is:read`, `is:unread`,
  `has:attachment`, `has:html`, `larger:`, `smaller:`, `before:`, `after:`.
- **HTML checks**, responsive and dark previews, and an authentication-results view.
- **Testing:** `GET /hermex/api/messages/wait` and the new `Hermex.Testing` package.
- **Webhooks**, **fault injection** and **relay recipient rules**; see the sections above.
- **New hooks:** `OnImporting`, `OnFaultsChanging` and `OnWebhookSending` (add headers to, or skip, a
  webhook notice). `HermexDeletingContext` gains `Scope`, `MessageIds` and `MailboxAddress`.
- **Changed — search:** plain words are now matched separately (all must match), `-word` excludes,
  and `word:word` is read as an operator. Quote a phrase (`"John Smith"`) for the old exact-phrase
  match. Queries longer than 512 characters return `400`.
- **Changed — relay:** only plain `name@domain` addresses are relayed, even with no recipient rules.
  Recipients in other forms (quoted, routed with `%` or `!`, with spaces) are left out, and a message
  whose sender is in such a form is not relayed.
- **Changed — HTML preview:** `<base>` and `<meta http-equiv="refresh">` are removed from the
  preview, as email clients do.
- **Check your `OnDeleting` hook:** new delete paths (bulk delete, mailbox delete, reset) call it with
  a `Scope`. A hook that protects particular messages by checking `MessageId` must also check
  `MessageIds` (bulk) and refuse `Mailbox` and `Reset` scopes it cannot judge; `MessageId` is set
  only for a single message.
- **Changed — API scripts:** `POST /hermex/api/messages/{id}/relay`, `…/read`, `…/messages/import` and
  `…/webhooks/{i}/resume` need the `X-Hermex-Request: 1` header even without authorization, so another
  website cannot trigger them through your browser. The dashboard and `Hermex.Testing` send it.
- **Custom mail stores:** a host-supplied `IMailStore` gets `501` from the new store operations it
  does not implement, including the wait API.
- **Upgrade:** the database gains a `CcDisplay` column, filled in for existing mail on first start.

## What's new in 2.2.1

- **Package page:** the README now renders on nuget.org (the header showed as raw HTML since 2.0.0).
  No code changes.

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

Apache License 2.0; see [LICENSE](https://github.com/Nertti-Technologies/hermex/blob/main/LICENSE).

Version 1.0.0 was released under the MIT License and remains available under those terms.
