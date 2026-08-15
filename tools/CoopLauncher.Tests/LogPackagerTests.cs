using CoopLauncher.Services;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class LogPackagerTests
{
    [Fact]
    public void Redact_RemovesCredentialValuesAndWindowsAccount()
    {
        string input = @"password=hunter2 token: abc C:\Users\Bryce\Documents\game.log";

        string result = LogPackager.Redact(input);

        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("Bryce", result);
        Assert.Contains("password=[redacted]", result);
        Assert.Contains("%USERPROFILE%", result);
    }
}
