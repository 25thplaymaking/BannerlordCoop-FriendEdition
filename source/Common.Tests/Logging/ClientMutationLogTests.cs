using Common.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using System.Collections.Generic;
using Xunit;

namespace Common.Tests.Logging;

public sealed class ClientMutationLogTests : System.IDisposable
{
    private readonly List<LogEvent> events = new List<LogEvent>();
    private readonly ILogger logger;

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> target;
        public CollectingSink(List<LogEvent> target) => this.target = target;
        public void Emit(LogEvent logEvent) => target.Add(logEvent);
    }

    public ClientMutationLogTests()
    {
        ClientMutationLog.Reset();
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Sink(new CollectingSink(events)).CreateLogger();
    }

    public void Dispose() => ClientMutationLog.Reset();

    /// <summary>
    /// A client locally authoring a server-owned object is expected and constant. Reporting every one
    /// at Error produced 48,712 lines in ten minutes and buried the 892 that explained a crash, so a
    /// repeating subject must go quiet after a bounded number of reports — and must never be Error.
    /// </summary>
    [Fact]
    public void RepeatedSubjectGoesQuietAfterTheLimit()
    {
        for (int i = 0; i < 50; i++)
            ClientMutationLog.Report(logger, "created", "ItemRoster");

        // The kept reports, plus exactly one "further reports suppressed" notice.
        Assert.Equal(ClientMutationLog.PerSubjectLimit + 1, events.Count);
        Assert.All(events, e => Assert.Equal(LogEventLevel.Warning, e.Level));
        Assert.DoesNotContain(events, e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void DistinctSubjectsAndActionsAreTrackedSeparately()
    {
        for (int i = 0; i < 10; i++)
        {
            ClientMutationLog.Report(logger, "created", "ItemRoster");
            ClientMutationLog.Report(logger, "updated", "ItemRoster");
            ClientMutationLog.Report(logger, "created", "Settlement");
        }

        // Each (action, subject) keeps its own budget, so a new offender is still visible even once
        // a noisy one has gone quiet.
        Assert.Equal(3 * (ClientMutationLog.PerSubjectLimit + 1), events.Count);
    }

    [Fact]
    public void NullLoggerIsIgnored()
    {
        ClientMutationLog.Report(null, "created", "ItemRoster");
    }
}
