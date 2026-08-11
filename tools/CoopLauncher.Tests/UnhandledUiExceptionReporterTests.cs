using Xunit;

namespace CoopLauncher.Tests;

public sealed class UnhandledUiExceptionReporterTests
{
    [Fact]
    public void Report_LogsAndShowsTheFaultButDoesNotKeepRunning()
    {
        string logged = string.Empty;
        string shownTitle = string.Empty;
        string shownMessage = string.Empty;
        var reporter = new UnhandledUiExceptionReporter(
            message => logged = message,
            (title, message) =>
            {
                shownTitle = title;
                shownMessage = message;
            },
            @"C:\private\launcher.log");
        var exception = new InvalidOperationException("render failed");

        bool handled = reporter.Report(exception);

        Assert.False(handled);
        Assert.Contains(exception.ToString(), logged);
        Assert.Equal("Calradia Co-op — launcher error", shownTitle);
        Assert.Contains("render failed", shownMessage);
        Assert.Contains(@"C:\private\launcher.log", shownMessage);
    }
}
