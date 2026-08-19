using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Incidents;

namespace GameInterface.Services.UI.Patches
{
    /// <summary>
    /// Suppresses Bannerlord's Incidents — the choice popups offered on leaving a settlement, ending a
    /// battle or finishing a conversation ("Rooster theft", "bury your dead", "let the men hunt").
    /// </summary>
    /// <remarks>
    /// This is why no player has ever seen one in co-op. It is unconditional on purpose but was
    /// undocumented, which made a deliberate suppression look like a missing feature.
    /// <para>
    /// <c>InvokeIncident</c> does nothing but <c>mapState.NextIncident = incident</c> — it queues the
    /// popup for the map UI. Blocking that assignment is the whole of the suppression; the incident is
    /// never presented, so no option is ever chosen and no consequence ever runs.
    /// </para>
    /// <para>
    /// It cannot simply be deleted, for two separate reasons.
    /// </para>
    /// <para>
    /// <b>The host can never raise one anyway.</b> Every trigger in
    /// <c>IncidentsCampaignBehaviour</c> is gated on <c>MobileParty.MainParty</c> —
    /// <c>OnSettlementEntered</c>, <c>OnSettlementLeft</c>, <c>OnMapEventEnded</c>
    /// (<c>evt.IsPlayerMapEvent</c>), <c>ConversationEnded</c>, the siege check, and
    /// <c>TryInvokeIncident</c>'s own <c>Hero.MainHero.IsPrisoner</c> guard. On a dedicated host
    /// MainParty is a placeholder that never enters a settlement or fights, so incidents are a
    /// client-side feature by construction, not a server one.
    /// </para>
    /// <para>
    /// <b>Their consequences are authoritative writes made on a replica.</b> Options resolve through
    /// <c>GiveGoldAction</c>, <c>ChangeRelationAction.ApplyPlayerRelation</c> and
    /// <c>SiegeAftermathAction</c>, and <c>IncidentEffect.Consequence</c> gates each on
    /// <c>MBRandom.RandomFloat</c>. Letting a client present the popup and run the consequence locally
    /// would have it roll its own outcome and mutate gold, relations and crime rating on a replica —
    /// precisely the divergence class that produced the stranded-health bug, where a client-side write
    /// was refused by the authority and the two sides never reconciled.
    /// </para>
    /// <para>
    /// Enabling them properly means routing the RESOLUTION rather than the presentation: the client
    /// shows the popup and submits the chosen option id, the server runs the consequence, and the
    /// resulting gold, relation and morale changes replicate as they already do. That is the same
    /// <c>AuthorityRoute</c> shape kingdom creation uses. It is a feature, not a patch deletion, and it
    /// stays suppressed until it is built that way.
    /// </para>
    /// </remarks>
    [HarmonyPatch(typeof(IncidentsCampaignBehaviour))]
    internal class IncidentDisable
    {
        [HarmonyPatch("InvokeIncident")]
        [HarmonyPrefix]
        public static bool InvokeIncidentPatch(Incident incident)
        {
            return false;
        }
    }
}
