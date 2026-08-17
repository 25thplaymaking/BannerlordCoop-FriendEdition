using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.PlayerCaptivityService;

/// <summary>
/// Player self-release from captivity (audit F15). The old wire message let a client name its own ransom
/// and its own reappearance position, and the server had no number to check either against, so it was
/// left disabled. The fix is a server-issued offer: the server prices the capture when it records it, and
/// the client can only quote the offer id back.
/// </summary>
public class PlayerCaptivityReleaseOfferTests
{
    private static readonly Assembly GameInterfaceAssembly =
        typeof(global::GameInterface.Services.ObjectManager.IObjectManager).Assembly;

    private static Type Type(string fullName) => GameInterfaceAssembly.GetType(fullName, throwOnError: true);

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static |
                             BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(type.FullName, name);

    private static int Compute(float roll, int gold, bool settlement, bool kingdom,
        bool mobile, bool lordParty, bool perk, float perkBonus)
    {
        Type ransom = Type("GameInterface.Services.PlayerCaptivityService.PlayerCaptivityRansom");
        return (int)Method(ransom, "Compute").Invoke(null,
            new object[] { roll, gold, settlement, kingdom, mobile, lordParty, perk, perkBonus });
    }

    /// <summary>
    /// The arithmetic is a port of PlayerCaptivity.GetPlayerRansomValue, read out of the shipped IL. It
    /// cannot be compared against the original at runtime — native reads Hero.MainHero at every step, so
    /// on a headless host it prices the wrong hero — which is exactly why the multipliers are pinned here.
    /// </summary>
    [Theory]
    // roll 0 => the 0.5 floor; 1000 gold => 1000*0.05 + 300 = 350. 0.5 * 350 = 175.
    [InlineData(0f, 1000, false, false, false, false, 175)]
    // roll 1 => factor 1.0. Same base, undoubled.
    [InlineData(1f, 1000, false, false, false, false, 350)]
    // Held in a settlement of a non-kingdom faction: x2.
    [InlineData(1f, 1000, true, false, false, false, 700)]
    // Held in a kingdom settlement: x4.
    [InlineData(1f, 1000, true, true, false, false, 1400)]
    // Held by a mobile party that is not a lord party: x1.
    [InlineData(1f, 1000, false, false, true, false, 350)]
    // Held by a lord party: x2.
    [InlineData(1f, 1000, false, false, true, true, 700)]
    // Penniless captive still owes the 300 floor.
    [InlineData(1f, 0, false, false, false, false, 300)]
    public void Compute_MatchesNativeRansomArithmetic(
        float roll, int gold, bool settlement, bool kingdom, bool mobile, bool lordParty, int expected)
    {
        Assert.Equal(expected, Compute(roll, gold, settlement, kingdom, mobile, lordParty, false, 0f));
    }

    [Fact]
    public void Compute_AppliesManOfMeansAsAMultiplierOnTop()
    {
        int without = Compute(1f, 1000, false, false, false, false, false, 0.25f);
        int with = Compute(1f, 1000, false, false, false, false, true, 0.25f);

        // 1 + secondaryBonus, not a flat add.
        Assert.Equal(350, without);
        Assert.Equal(437, with);
    }

    [Fact]
    public void Compute_ScalesWithGoldSoARicherCaptiveIsWorthMore()
    {
        Assert.True(Compute(1f, 10_000, false, false, false, false, false, 0f) >
                    Compute(1f, 1_000, false, false, false, false, false, 0f));
    }

    /// <summary>
    /// The security property, expressed as a shape: the request a client sends must not be able to carry
    /// a price, a position, or somebody else's hero. If a field like that is ever added back, the server
    /// has something client-chosen to read again and the F15 exploit returns.
    /// </summary>
    [Fact]
    public void ReleaseRequest_CarriesOnlyAnOfferIdAndTheKindOfRelease()
    {
        Type request = Type(
            "GameInterface.Services.PlayerCaptivityService.Messages.NetworkPlayerCaptivityReleaseRequest");

        string[] fields = request
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => f.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { "Detail", "OfferId" }, fields);
    }

    [Fact]
    public void Server_PricesTheCaptureItselfAndOffersTerms()
    {
        Type server = Type(
            "GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityServerHandler");

        // Priced when the capture is recorded, not when the release is requested.
        Assert.Contains("IssueReleaseOffer", CalledNames(Method(server, "Handle_PrisonerTaken")));

        string[] issuing = CalledNames(Method(server, "IssueReleaseOffer")).ToArray();
        Assert.Contains("ForCaptive", issuing);
        Assert.Contains("Send", issuing);
    }

    [Fact]
    public void Server_ResolvesEverythingFromItsOwnOfferAndConsumesIt()
    {
        Type server = Type(
            "GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityServerHandler");

        string[] applied = CalledNames(Method(server, "Handle_NetworkPlayerCaptivityReleaseRequest"))
            .Concat(NestedClosureCalls(server, "Handle_NetworkPlayerCaptivityReleaseRequest"))
            .ToArray();

        // Identity, then the offer, then a server-derived position — never the client's.
        Assert.Contains("TryGetPlayer", applied);
        Assert.Contains("TryGetValue", applied);
        Assert.Contains("Remove", applied);
        Assert.Contains("GetReleasePosition", applied);
        Assert.Contains("ReleasePlayerFromCaptivity", applied);
    }

    [Fact]
    public void Client_QuotesTheOfferRatherThanNamingItsOwnTerms()
    {
        Type client = Type(
            "GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityClientHandler");

        Assert.Contains("NetworkPlayerCaptivityReleaseOffer", SubscribedMessageNames(client));
        Assert.Contains("SendAll", CalledNames(Method(client, "Handle_EndPlayerCaptivityAttempted")));
    }

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

    private static IEnumerable<MethodBase> ResolvedCalls(MethodBase method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        Type[] typeArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F && il[i] != 0x73) continue;
            MethodBase target = null;
            try { target = module.ResolveMethod(BitConverter.ToInt32(il, i + 1), typeArgs, null); }
            catch { }
            if (target != null) yield return target;
        }
    }
}
