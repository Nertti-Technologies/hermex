using System.Collections.Concurrent;
using System.Net.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hermex.Tests;

/// <summary>The event log's mirror to the host's ILogger keeps its 2.0.0 shape.</summary>
public sealed class EventLogMirrorTests
{
    [Fact]
    public async Task Mirrored_events_keep_event_id_zero_and_no_name()
    {
        var capture = new CaptureProvider();
        await using var host = await HermexTestHost.StartAsync(services: s => s.AddSingleton<ILoggerProvider>(capture));
        var port = await host.WaitForSmtpPortAsync();

        using (var client = new SmtpClient("127.0.0.1", port))
        using (var message = new MailMessage("a@example.test", "b@example.test", "Mirror", "Body"))
            await client.SendMailAsync(message);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (capture.Mirrored.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotEmpty(capture.Mirrored);
        Assert.All(capture.Mirrored, e =>
        {
            Assert.Equal(0, e.Id);
            Assert.True(string.IsNullOrEmpty(e.Name), $"EventName was '{e.Name}'");
        });
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentBag<EventId> Mirrored { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void Dispose() { }

        private sealed class CaptureLogger(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category.EndsWith("HermexEventLog", StringComparison.Ordinal))
                    owner.Mirrored.Add(eventId);
            }
        }
    }
}
