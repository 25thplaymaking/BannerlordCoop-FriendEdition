using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieConfigurationFingerprintTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fourberie-config-" + Guid.NewGuid().ToString("N"));

    public FourberieConfigurationFingerprintTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void FormattingCommentsAndAttributeOrder_DoNotChangeFingerprint()
    {
        var first = Write("first-config.xml", "<?xml version=\"1.0\"?><Settings b=\"2\" a=\"1\"><Enabled>true</Enabled></Settings>");
        var firstTroops = Write("first-troops.xml", "<TroopList><!-- ignored --><Troop id=\"a\"> empire </Troop></TroopList>");
        var second = Write("second-config.xml", "<Settings a=\"1\" b=\"2\">\r\n  <Enabled> true </Enabled>\r\n</Settings>");
        var secondTroops = Write("second-troops.xml", "<TroopList><Troop id=\"a\">empire</Troop></TroopList>");

        Assert.True(FourberieConfigurationFingerprint.TryCompute(new[] { first, firstTroops }, out var left, out var leftFailure), leftFailure);
        Assert.True(FourberieConfigurationFingerprint.TryCompute(new[] { second, secondTroops }, out var right, out var rightFailure), rightFailure);
        Assert.Equal(left, right);
    }

    [Fact]
    public void RecruitableElementOrder_RemainsGameplaySignificant()
    {
        var config = Write("config.xml", "<Settings><Enabled>true</Enabled></Settings>");
        var first = Write("troops-a.xml", "<TroopList><Troop>a</Troop><Troop>b</Troop></TroopList>");
        var second = Write("troops-b.xml", "<TroopList><Troop>b</Troop><Troop>a</Troop></TroopList>");

        Assert.True(FourberieConfigurationFingerprint.TryCompute(new[] { config, first }, out var left, out _));
        Assert.True(FourberieConfigurationFingerprint.TryCompute(new[] { config, second }, out var right, out _));
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void DtdConfiguration_IsRejected()
    {
        var config = Write("config.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><Settings>&e;</Settings>");
        var troops = Write("troops.xml", "<TroopList />");

        Assert.False(FourberieConfigurationFingerprint.TryCompute(new[] { config, troops }, out _, out var failure));
        Assert.Contains("invalid Fourberie configuration", failure);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
