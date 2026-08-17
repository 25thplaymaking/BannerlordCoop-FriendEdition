using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.GameMenus;

/// <summary>
/// Guards the two client actions that were shipped disabled: settlement "take to party" and companion
/// dismissal. Both had a working client->server->apply path that a later commit short-circuited with an
/// early return and a <c>#pragma warning disable CS0162</c> hiding the unreachable remainder, and in
/// both cases the server never subscribed the apply message either — so nothing failed loudly and no
/// test noticed. Trade was disabled the same way and crashed the client for weeks.
///
/// These assert the wiring at the IL level rather than the behaviour, because the behaviour needs live
/// TaleWorlds campaign objects. That is the property worth pinning: a stub that returns before reaching
/// the send/apply is exactly what re-breaks these, and it is invisible to a compile.
/// </summary>
public class RestoredClientActionWiringTests
{
    private static readonly Assembly GameInterfaceAssembly =
        typeof(global::GameInterface.Services.ObjectManager.IObjectManager).Assembly;

    private static Type Handler(string fullName) =>
        GameInterfaceAssembly.GetType(fullName, throwOnError: true);

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(type.FullName, name);

    [Fact]
    public void HeroTransfer_ClientRequestReachesTheNetwork()
    {
        Type handler = Handler("GameInterface.Services.GameMenus.Handlers.ExecuteTroopActionHandler");

        // Publishing the request is the whole client side. A stub returns before SendAll.
        Assert.Contains("SendAll", CalledNames(Method(handler, "Handle_MenuHeroTakenToParty")));
    }

    [Fact]
    public void HeroTransfer_ServerSubscribesAndAppliesUnderAuthorization()
    {
        Type handler = Handler("GameInterface.Services.GameMenus.Handlers.ExecuteTroopActionHandler");

        // The apply message was defined and handled but never subscribed, so the server ignored it.
        Assert.Contains("MenuTakeHeroToParty", SubscribedMessageNames(handler));

        // The apply itself runs inside the GameThread.RunSafe closure, which the compiler lifts out.
        string[] applied = CalledNames(Method(handler, "Handle_MenuTakeHeroToParty"))
            .Concat(NestedClosureCalls(handler, "Handle_MenuTakeHeroToParty"))
            .ToArray();

        // The request names its own target party, so the ownership gate is not optional.
        Assert.Contains("IsAuthorized", applied);
        Assert.Contains("Apply", applied);
    }

    [Fact]
    public void HeroTransfer_AuthorizationChecksOwnershipAgainstServerState()
    {
        Type handler = Handler("GameInterface.Services.GameMenus.Handlers.ExecuteTroopActionHandler");
        MethodBase authorize = Method(handler, "IsAuthorized");

        // Resolve the requesting peer to a player, then compare against that player's own party.
        // Player exposes its ids as fields, so the party check is a field read rather than a call.
        Assert.Contains("TryGetPlayer", CalledNames(authorize));
        Assert.Contains("MobilePartyId", ReadFieldNames(authorize));
    }

    [Fact]
    public void CompanionDismissal_ClientRequestReachesTheNetwork()
    {
        Type handler = Handler("GameInterface.Services.Companions.Handlers.CompanionRolesHandler");

        Assert.Contains("SendAll", CalledNames(Method(handler, "Handle_CompanionFired")));
    }

    [Fact]
    public void CompanionDismissal_ServerSubscribesAndAppliesUnderAuthorization()
    {
        Type handler = Handler("GameInterface.Services.Companions.Handlers.CompanionRolesHandler");

        Assert.Contains("FireCompanion", SubscribedMessageNames(handler));

        // The apply body is a closure the compiler lifts out of Handle_FireCompanion, so look there too.
        string[] applied = CalledNames(Method(handler, "Handle_FireCompanion"))
            .Concat(NestedClosureCalls(handler, "Handle_FireCompanion"))
            .ToArray();

        // The pre-existing checks only confirm the world matches what the client expected; the player
        // lookup is what confirms the requester may dismiss this companion at all.
        Assert.Contains("TryGetPlayer", applied);
        Assert.Contains("ApplyByFire", applied);
    }

    /// <summary>
    /// The five lord-conversation outcomes — liberate, take prisoner, released-after-help, let-go on
    /// defeat, freed — were all stubbed the same way, and none of their five server-side apply handlers
    /// was ever subscribed either. Prisoner taking in particular is core campaign behaviour.
    /// </summary>
    [Theory]
    [InlineData("LiberateLordPrisoner")]
    [InlineData("TakeLordPrisoner")]
    [InlineData("LordHelpedInBattle")]
    [InlineData("LordDefeatToRelease")]
    [InlineData("LordFreedToRelease")]
    public void LordConversationOutcome_IsWiredEndToEndAndAuthorized(string message)
    {
        Type handler = Handler("GameInterface.Services.Heroes.Handlers.LordConversationsCampaignBehaviorHandler");
        string[] subscribed = SubscribedMessageNames(handler).ToArray();

        // Client request, then the server apply that was defined but never listened for.
        Assert.Contains(message, subscribed);
        Assert.Contains("Network" + message, subscribed);

        Assert.Contains("SendAll", CalledNames(Method(handler, "Handle_" + message)));

        // The apply runs inside a GameThread.RunSafe closure, and so does its authorization.
        Assert.Contains("IsConversationAuthorized",
            NestedClosureCalls(handler, "Handle_Network" + message));
    }

    [Fact]
    public void LordConversationAuthorization_RequiresIdentityAndEitherLeaseOrCustody()
    {
        Type handler = Handler("GameInterface.Services.Heroes.Handlers.LordConversationsCampaignBehaviorHandler");
        string[] checks = CalledNames(Method(handler, "IsConversationAuthorized")).ToArray();

        // Identity: the actor named in the request must be the requesting peer's own.
        Assert.Contains("TryGetPlayer", checks);

        // Liveness: a server-issued conversation lease, or custody of the prisoner as the fallback
        // for party-screen conversations that never open a map conversation.
        Assert.Contains("TryGetActiveLeaseByOwner", checks);
        Assert.Contains("HoldsAsPrisoner", checks);
    }

    /// <summary>
    /// Releasing a prisoner your own party holds — the party screen's release action — reached the
    /// client handler and stopped there: there was no network message at all, so the request had
    /// nowhere to go. The server now applies it under a custody check.
    /// </summary>
    [Fact]
    public void PrisonerRelease_IsWiredEndToEndUnderCustody()
    {
        Type client = Handler("GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityClientHandler");
        Assert.Contains("SendAll", CalledNames(Method(client, "Handle_EndCaptivityAttempted")));

        Type server = Handler("GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityServerHandler");
        Assert.Contains("NetworkEndCaptivityAttempted", SubscribedMessageNames(server));

        string[] applied = CalledNames(Method(server, "Handle_NetworkEndCaptivityAttempted"))
            .Concat(NestedClosureCalls(server, "Handle_NetworkEndCaptivityAttempted"))
            .ToArray();

        // Custody is the authorization: resolve the peer's player, then confirm the captor party is
        // theirs before releasing. Without the captor read a client could empty anyone's dungeon.
        Assert.Contains("TryGetPlayer", applied);
        Assert.Contains("get_PartyBelongedToAsPrisoner", applied);
        Assert.Contains("ApplyByReleasedByChoice", applied);
    }

    /// <summary>Message type names passed to IMessageBroker.Subscribe&lt;T&gt; in the constructor.</summary>
    private static IEnumerable<string> SubscribedMessageNames(Type handler)
    {
        foreach (ConstructorInfo ctor in handler.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodBase called in ResolvedCalls(ctor))
            {
                if (called.Name != "Subscribe" || !called.IsGenericMethod) continue;
                yield return called.GetGenericArguments()[0].Name;
            }
        }
    }

    private static IEnumerable<string> CalledNames(MethodBase method) =>
        ResolvedCalls(method).Select(called => called.Name);

    /// <summary>
    /// Bodies passed to GameThread.RunSafe become methods on a compiler-generated display class, so the
    /// calls that matter are not in the declaring method's own IL.
    /// </summary>
    private static IEnumerable<string> NestedClosureCalls(Type handler, string methodNameFragment)
    {
        foreach (Type nested in handler.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            foreach (MethodInfo lifted in nested.GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!lifted.Name.Contains(methodNameFragment)) continue;
                foreach (string name in CalledNames(lifted)) yield return name;
            }
        }
    }

    /// <summary>Names of fields read by the method (ldfld / ldsfld).</summary>
    private static IEnumerable<string> ReadFieldNames(MethodBase method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x7B && il[i] != 0x7E) continue; // ldfld / ldsfld
            string name = null;
            try { name = module.ResolveField(BitConverter.ToInt32(il, i + 1))?.Name; }
            catch { }
            if (name != null) yield return name;
        }
    }

    private static IEnumerable<MethodBase> ResolvedCalls(MethodBase method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        Type[] typeArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call / callvirt / newobj. Scanning every offset can decode an operand byte as an opcode,
            // which is why unresolvable tokens are skipped rather than treated as a failure.
            if (il[i] != 0x28 && il[i] != 0x6F && il[i] != 0x73) continue;

            MethodBase target = null;
            try { target = module.ResolveMethod(BitConverter.ToInt32(il, i + 1), typeArgs, null); }
            catch { }
            if (target != null) yield return target;
        }
    }
}
