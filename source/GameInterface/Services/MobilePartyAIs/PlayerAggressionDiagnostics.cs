using Common.Logging;
using GameInterface.Services.MobileParties.Extensions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobilePartyAIs;

/// <summary>
/// Records why AI parties do, or do not, decide to attack a player-controlled party.
/// </summary>
/// <remarks>
/// Players report that hostile AI never initiates: bandits will happily join a fight already in
/// progress, but walk past a lone player on the map. The evidence agrees — over 26 hours the host
/// published <c>ConversationRequested</c> exactly zero times, which is the only bridge an AI/player
/// encounter can cross on a dedicated host, while 625 map events started between AI parties in the
/// same window. So the machinery works and the decision never happens.
/// <para>
/// The decision runs through <c>DefaultMobilePartyAIModel.ShouldConsiderAttacking</c>, called from
/// <c>CalculateInitiativeScoresForEnemy</c> for every enemy an AI party evaluates. Native's own
/// version only consults <c>ShouldBeIgnored</c> for <c>MobileParty.MainParty</c>; the coop postfix
/// applies it to every target, and carries a "TODO test with player parties" that was never
/// resolved. On a dedicated host a player's party is NOT the main party, so that postfix is the one
/// rule in the chain that treats player parties differently from how native would — which makes it
/// the prime suspect and the reason this counts outcomes per reason rather than assuming one.
/// </para>
/// <para>
/// Two stages are recorded, because "considered" is not "attacked": whether the AI was ALLOWED to
/// consider the player at all, and whether it went on to actually set <c>EngageParty</c> on them.
/// Allowed-but-never-engaged points at scoring or <c>CanPartyInteract</c>; a dominant refusal reason
/// names the culprit outright. Nothing here changes a decision — it only observes one, deliberately,
/// because guessing wrong on hostility either makes the world passive or has every bandit on the map
/// converge on one player.
/// </para>
/// <para>
/// Off by default and toggled at runtime with <c>coop.debug.mobileparty.player_aggression</c>: the
/// model runs on every party think, on parallel threads, so it must cost nothing when not in use.
/// </para>
/// </remarks>
internal static class PlayerAggressionDiagnostics
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(PlayerAggressionDiagnostics));

    /// <summary>Native's own checks refused before the coop postfix ran.</summary>
    internal const string NativeDeclined = "native-declined";

    /// <summary>The coop postfix refused because the player's party is inside an ignore window.</summary>
    internal const string TargetIgnored = "target-should-be-ignored";

    /// <summary>An explicit coop attack protection (safe passage, captivity release) is in force.</summary>
    internal const string AttackPrevented = "attack-protection";

    /// <summary>The player is held in a conversation, where an attack could not resolve anyway.</summary>
    internal const string InConversation = "player-in-conversation";

    /// <summary>Nothing refused: the AI was free to attack this player.</summary>
    internal const string Allowed = "allowed";

    /// <summary>An AI party actually committed to engaging a player party.</summary>
    internal const string Engaged = "engage-party-set";

    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, int> Outcomes = new Dictionary<string, int>();
    private static DateTime nextReport = DateTime.MinValue;

    /// <summary>
    /// Whether to record. A plain static read on the AI hot path, checked before anything else costs
    /// anything — <see cref="MobilePartyExtensions.IsPlayerParty"/> is a registry lookup and must not
    /// run per enemy per party per tick when nobody is watching.
    /// </summary>
    internal static bool Enabled { get; private set; }

    internal static void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            Enabled = enabled;
            Outcomes.Clear();
            nextReport = enabled ? DateTime.UtcNow + ReportInterval : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Records one AI-versus-player decision. Ignores everything that does not target a player party,
    /// which is almost all of them.
    /// </summary>
    internal static void Record(MobileParty party, MobileParty targetParty, string outcome)
    {
        if (!Enabled) return;

        try
        {
            if (party == null || targetParty == null) return;
            if (!targetParty.IsPlayerParty()) return;

            bool due;
            lock (Gate)
            {
                Outcomes.TryGetValue(outcome, out int count);
                Outcomes[outcome] = count + 1;

                due = DateTime.UtcNow >= nextReport;
                if (due) nextReport = DateTime.UtcNow + ReportInterval;
            }

            if (due) Report();
        }
        catch (Exception error)
        {
            // A diagnostic must never be able to break the AI it is measuring.
            Logger.Error(error, "Player-aggression diagnostics failed; disabling them.");
            SetEnabled(false);
        }
    }

    private static void Report()
    {
        string summary;
        lock (Gate)
        {
            if (Outcomes.Count == 0) return;

            summary = string.Join(", ", Outcomes
                .OrderByDescending(entry => entry.Value)
                .Select(entry => $"{entry.Key}={entry.Value}"));
            Outcomes.Clear();
        }

        Logger.Information("[PlayerAggression] over {Seconds:N0}s: {Summary}", ReportInterval.TotalSeconds, summary);
    }
}
