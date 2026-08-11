using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>Why a routed Fourberie create-action was or was not applied.</summary>
public enum FourberieCreateActionVerdict
{
    Applied,

    /// <summary>The method is not on the routed allow-list — refused.</summary>
    NotAllowed,

    /// <summary>Fourberie / the target method is not loaded.</summary>
    ModuleNotInstalled,

    /// <summary>The mod routine threw; nothing may be assumed about resulting state.</summary>
    ApplyFailed,
}

/// <summary>
/// [Server, game thread] Runs one of Fourberie's whitelisted static create-actions so the parties
/// and rosters it mints are created on the server and replicate through Coop's create funnels.
/// </summary>
public interface IFourberieCreateActionInterface : IGameAbstraction
{
    FourberieCreateActionVerdict TryRun(string declaringTypeName, string methodName, int arg);
}

/// <summary>
/// Invokes Fourberie's own <c>static void M(int)</c> create routines by reflection, restricted to
/// an explicit allow-list so a peer can never drive arbitrary code. On the server the routing
/// prefix (<see cref="FourberieAuthorityPatches.RoutedCreateActionPrefix"/>) lets the original
/// run; its <c>MobileParty.CreateParty</c>/<c>TroopRoster</c> writes then flow through Coop's
/// authoritative funnels. Resolved by reflection so it stays inert when Fourberie is absent.
/// </summary>
internal sealed class FourberieCreateActionInterface : IFourberieCreateActionInterface
{
    private static readonly ILogger Logger = LogManager.GetLogger<FourberieCreateActionInterface>();

    // The only Fourberie static create-actions clients may route. Same list the routing guards mark.
    private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fourberie.CriminalVM.AgentsEnlistRoutine",
        "Fourberie.FourbBanditBehavior.FourbRecruitBandit",
        "Fourberie.HelperSubInsuScam.SpawnBandits",
    };

    public FourberieCreateActionVerdict TryRun(string declaringTypeName, string methodName, int arg)
    {
        var key = declaringTypeName + "." + methodName;
        if (!Allowed.Contains(key)) return FourberieCreateActionVerdict.NotAllowed;

        var type = AccessTools.TypeByName(declaringTypeName);
        var method = type == null ? null : AccessTools.Method(type, methodName, new[] { typeof(int) });
        if (method == null) return FourberieCreateActionVerdict.ModuleNotInstalled;

        try
        {
            method.Invoke(null, new object[] { arg });
            return FourberieCreateActionVerdict.Applied;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fourberie {Key}({Arg}) threw during authoritative apply", key, arg);
            return FourberieCreateActionVerdict.ApplyFailed;
        }
    }
}
