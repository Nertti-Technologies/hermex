using System.Net.Mail;
using System.Security.Cryptography;
using Hermex;
using Hermex.Sample.Web;

var builder = WebApplication.CreateBuilder(args);

// Register the in-process SMTP server, mail store, background workers and dashboard.
builder.Services.AddHermex(options =>
{
    options.SmtpPort = 2525;
    options.ServerHostName = "hermex.sample";
    options.DashboardTitle = "Hermex";
    options.DefaultTheme = HermexTheme.System;

    // Expose captured mail over IMAP — connect a real mail client to localhost:1143.
    options.EnableImap = true;
    options.ImapPort = 1143;

    // Webhooks: a signed notice for every captured message. This demo posts to the sample's own
    // /webhook-demo route; loopback is allowed because the sample runs in Development. The secret is
    // generated at start-up; a real host reads it from user-secrets or the environment.
    var self = (builder.Configuration["urls"] ?? "http://localhost:5170").Split(';')[0].TrimEnd('/')
        .Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal)
        .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal).Replace("://[::]", "://localhost", StringComparison.Ordinal);
    options.Webhooks.Endpoints.Add(new HermexWebhookEndpoint
    {
        Url = new Uri(self + "/webhook-demo"),
        Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    });

    // Fault injection, Development only. With no rules mail is captured as usual, unless a message asks
    // for a fault with an X-Hermex-Fault header ("Send email that fails"). Add rules on the Settings page.
    options.Faults.Enabled = builder.Environment.IsDevelopment();
});

var app = builder.Build();

// Mount the dashboard at /hermex, the API at /hermex/api and the hub at /hermex/hub.
app.UseHermex();

// A tiny landing page that sends test mail through the in-process SMTP server.
app.MapGet("/", () => Results.Content(SamplePages.Landing, "text/html"));

// The webhook receiver for the demo above. A real receiver verifies webhook-signature first.
app.MapPost("/webhook-demo", (HttpRequest request, ILogger<Program> logger) =>
{
    logger.LogInformation("Webhook {WebhookId} received.", request.Headers["webhook-id"].ToString());
    return Results.NoContent();
});

app.MapPost("/api/send/{kind}", async (string kind, int? count, HermexRuntimeState state) =>
{
    var port = state.ListeningPort ?? 2525;
    try
    {
        if (kind == "fault")
            return Results.Ok(new { sent = 0, port, note = await SendFaultAsync(port) });
        var sent = kind switch
        {
            "text" => await SendOneAsync(port, SampleMail.PlainText()),
            "html" => await SendOneAsync(port, SampleMail.Html()),
            "attachment" => await SendOneAsync(port, SampleMail.WithAttachment()),
            "burst" => await SendManyAsync(port, Math.Clamp(count ?? 25, 1, 250)),
            _ => -1,
        };

        return sent < 0
            ? Results.BadRequest(new { error = $"Unknown message kind '{kind}'." })
            : Results.Ok(new { sent, port });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Could not send mail to the in-process SMTP server: {ex.Message}");
    }
});

app.Run();

// Sends a single message through the standard .NET SMTP client — exactly how a real
// application sends mail. Hermex captures it for inspection.
static async Task<int> SendOneAsync(int port, MailMessage message)
{
    using var client = new SmtpClient("127.0.0.1", port);
    using (message)
    {
        await client.SendMailAsync(message);
    }
    return 1;
}

// Asks Hermex to refuse this message with 451, as a failing mail server would, and describes what the
// app saw. A refused message is not captured.
static async Task<string> SendFaultAsync(int port)
{
    try
    {
        await SendOneAsync(port, SampleMail.AsksForFault());
        return "The server accepted the message: fault injection is on in the Development environment only.";
    }
    catch (SmtpException ex) when (ex.StatusCode == SmtpStatusCode.LocalErrorInProcessing
                                   && ex.Message.Contains("injected fault", StringComparison.Ordinal))
    {
        return $"The server refused the message with {(int)ex.StatusCode}, as its X-Hermex-Fault header asked. " +
               "This is how your app sees a failing mail server; the fault is in the dashboard's Logs.";
    }
}

static async Task<int> SendManyAsync(int port, int count)
{
    for (var i = 1; i <= count; i++)
    {
        await SendOneAsync(port, SampleMail.Marketing(i));
    }
    return count;
}
