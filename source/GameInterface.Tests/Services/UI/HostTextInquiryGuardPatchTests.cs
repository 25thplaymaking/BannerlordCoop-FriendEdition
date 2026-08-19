using Common;
using GameInterface.Services.UI.Patches;
using HarmonyLib;
using System;
using System.Reflection;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.UI;

/// <summary>
/// Covers the guard that stops a modal text prompt opening on the headless host.
/// </summary>
/// <remarks>
/// <c>DedicatedServer.Core</c> guards <c>ShowInquiry</c> but not <c>ShowTextInquiry</c>, and a process
/// with no keyboard cannot answer "type something". The two properties that matter are that the host
/// never shows one, and that a client still does — the prompt has to reach a player, and the join and
/// listen-host password dialogs both go through this same API.
/// </remarks>
public class HostTextInquiryGuardPatchTests
{
    private static readonly MethodInfo Prefix = AccessTools.Method(
        typeof(HostTextInquiryGuardPatch), "ShowTextInquiryPrefix");

    /// <summary>Runs the patch prefix directly; true means "let the original run".</summary>
    private static bool RunPrefix(TextInquiryData data) =>
        (bool)Prefix.Invoke(null, new object[] { data });

    private static TextInquiryData Prompt(Action<string> affirmative, Action negative) =>
        new TextInquiryData("Title", "Body", true, true, "OK", "Cancel", affirmative, negative);

    private static T AsServer<T>(Func<T> body)
    {
        bool previous = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = true;
            return body();
        }
        finally
        {
            ModInformation.IsServer = previous;
        }
    }

    [Fact]
    public void AClientStillShowsThePrompt()
    {
        // The join-password and listen-host dialogs use this API. Suppressing them would break both.
        bool previous = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = false;
            Assert.True(RunPrefix(Prompt(_ => { }, () => { })));
        }
        finally
        {
            ModInformation.IsServer = previous;
        }
    }

    [Fact]
    public void TheHostNeverShowsThePrompt()
    {
        Assert.False(AsServer(() => RunPrefix(Prompt(_ => { }, () => { }))));
    }

    [Fact]
    public void TheHostDeclinesRatherThanInventingAnAnswer()
    {
        // Affirming would have to make up the typed string, committing the caller to a value nobody
        // chose. Declining is the only honest answer a machine can give.
        bool affirmed = false;
        bool declined = false;

        AsServer(() => RunPrefix(Prompt(_ => affirmed = true, () => declined = true)));

        Assert.True(declined);
        Assert.False(affirmed);
    }

    [Fact]
    public void APromptWithNoDeclineActionIsSimplyDropped()
    {
        Assert.False(AsServer(() => RunPrefix(Prompt(_ => { }, null))));
    }

    [Fact]
    public void ANullPromptDoesNotThrow()
    {
        Assert.False(AsServer(() => RunPrefix(null)));
    }

    [Fact]
    public void AThrowingDeclineIsContainedAndStillSuppresses()
    {
        // The prompt is already suppressed by that point, which is the part that protects the host;
        // a throwing cancel must not take the game thread with it.
        bool suppressed = AsServer(() =>
            !RunPrefix(Prompt(_ => { }, () => throw new InvalidOperationException("cancel blew up"))));

        Assert.True(suppressed);
    }
}
