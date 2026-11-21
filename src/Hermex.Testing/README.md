# Hermex.Testing

Wait for the email your app sends, then check it, in any test framework. It talks to a running
[Hermex](https://github.com/Nertti-Technologies/hermex) host over HTTP and has no dependencies.

```csharp
await using var app = new WebApplicationFactory<Program>();   // your app, with services.AddHermex()
var hermex = new HermexClient(app.CreateClient());

var mark = await hermex.MarkAsync();                          // ignore mail sent before this point
await SignUpAsync(app, "ann@example.test");                   // your code sends the email

var message = await hermex.WaitForMessageAsync(
    new HermexMessageCriteria { To = "ann@example.test", Subject = "Confirm", After = mark },
    timeout: TimeSpan.FromSeconds(10));

var links = await hermex.GetLinksAsync(message.Id);
var confirm = links.Single(l => l.Href.Contains("/confirm", StringComparison.Ordinal));
```

- `WaitForMessageAsync` returns the oldest matching message stored after the cursor. Matching is
  case-insensitive "contains" on each field you set. The server signals the arrival; nothing polls.
  On timeout it throws `HermexWaitTimeoutException`.
- `GetMessageAsync` returns the subject, bodies and headers (and marks the message read).
- `GetHtmlAsync` returns the HTML as the dashboard previews it; `GetLinksAsync` returns each unique link.
- `DeleteAllAsync` clears captured mail between tests.

When the host protects Hermex with tier policies, waiting and reading need the Viewer tier and
`DeleteAllAsync` needs the Manager tier.

Licensed under Apache-2.0.
