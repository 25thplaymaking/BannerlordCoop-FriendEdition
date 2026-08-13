using System.Reflection;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

/// <summary>
/// Guards the 2026-08-13 join-freeze regression: a Harmony target declared on a constructed
/// generic base type (Diplomacy's <c>AbstractDiplomaticAction&lt;T&gt;.TryApply</c>) throws
/// "The given generic instantiation was invalid" on the .NET Framework client CLR, aborting the
/// whole patch category inside the join handshake. The fixture types below mirror Diplomacy
/// 1.4.7's exact CRTP shape so target resolution behaves as it does against the pinned assembly.
/// </summary>
public sealed class HarmonyGenericTargetPolicyTests
{
    private const bool FrameworkRuntime = false;
    private const bool CoreRuntime = true;

    private static MethodBase Resolve(string name) =>
        AccessTools.Method(typeof(ConcreteAction), name);

    [Fact]
    public void MethodDeclaredOnClosedGenericBase_IsRejectedOnFrameworkRuntime()
    {
        var tryApply = Resolve("TryApply");
        Assert.NotNull(tryApply);
        Assert.True(tryApply.DeclaringType.IsGenericType);

        Assert.False(HarmonyGenericTargetPolicy.CanPatch(tryApply, FrameworkRuntime));
    }

    [Fact]
    public void MethodDeclaredOnClosedGenericBase_IsAcceptedOnCoreRuntime()
    {
        var tryApply = Resolve("TryApply");

        Assert.True(HarmonyGenericTargetPolicy.CanPatch(tryApply, CoreRuntime));
    }

    [Fact]
    public void ConcreteOverride_IsAcceptedOnEveryRuntime()
    {
        var applyInternal = Resolve("ApplyInternal");
        Assert.NotNull(applyInternal);
        Assert.Equal(typeof(ConcreteAction), applyInternal.DeclaringType);

        Assert.True(HarmonyGenericTargetPolicy.CanPatch(applyInternal, FrameworkRuntime));
        Assert.True(HarmonyGenericTargetPolicy.CanPatch(applyInternal, CoreRuntime));
    }

    [Fact]
    public void ConcreteMethodOnNonGenericType_IsAcceptedOnEveryRuntime()
    {
        var applyPeace = AccessTools.Method(typeof(NonGenericAction), "ApplyPeace");

        Assert.True(HarmonyGenericTargetPolicy.CanPatch(applyPeace, FrameworkRuntime));
        Assert.True(HarmonyGenericTargetPolicy.CanPatch(applyPeace, CoreRuntime));
    }

    [Fact]
    public void OpenGenericDefinition_IsRejectedOnEveryRuntime()
    {
        var openTryApply = typeof(AbstractAction<>).GetMethod(
            "TryApply", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openTryApply);

        Assert.False(HarmonyGenericTargetPolicy.CanPatch(openTryApply, FrameworkRuntime));
        Assert.False(HarmonyGenericTargetPolicy.CanPatch(openTryApply, CoreRuntime));
    }

    [Fact]
    public void GenericMethodOnNonGenericType_FollowsTheRuntimeGate()
    {
        var open = typeof(NonGenericAction).GetMethod("GenericHelper");
        var closed = open.MakeGenericMethod(typeof(int));

        Assert.False(HarmonyGenericTargetPolicy.CanPatch(open, CoreRuntime));
        Assert.False(HarmonyGenericTargetPolicy.CanPatch(closed, FrameworkRuntime));
        Assert.True(HarmonyGenericTargetPolicy.CanPatch(closed, CoreRuntime));
    }

    [Fact]
    public void NullMethod_IsRejected()
    {
        Assert.False(HarmonyGenericTargetPolicy.CanPatch(null, FrameworkRuntime));
        Assert.False(HarmonyGenericTargetPolicy.CanPatch(null, CoreRuntime));
    }

    // Mirrors Diplomacy.DiplomaticAction.AbstractDiplomaticAction<T>: TryApply declared on the
    // generic base and NOT overridden; ApplyInternal abstract on the base, overridden concretely.
    private abstract class AbstractAction<T> where T : AbstractAction<T>, new()
    {
        protected void TryApply() => ApplyInternal();

        protected abstract void ApplyInternal();
    }

    private sealed class ConcreteAction : AbstractAction<ConcreteAction>
    {
        protected override void ApplyInternal()
        {
        }
    }

    // Mirrors Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction (non-generic static entry).
    private sealed class NonGenericAction
    {
        public static void ApplyPeace()
        {
        }

        public static void GenericHelper<T>()
        {
        }
    }
}
