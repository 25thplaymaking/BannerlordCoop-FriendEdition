using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;

// Exact field-shape fixture for the late-bound Fourberie state adapter. The production project
// never references this type; its full name intentionally matches the creator binary's behavior.
namespace Fourberie
{
    public static class FourberieBehavior
    {
        public static Dictionary<string, CampaignTime> _townScamTiming = new();
        public static Dictionary<string, CampaignTime> _townTributeTiming = new();
        public static Dictionary<string, CampaignTime> _townExtoTiming = new();
        public static Dictionary<string, CampaignTime> _townRobTiming = new();
        public static Dictionary<string, CampaignTime> _townInsuScamTiming = new();
        public static Dictionary<string, CampaignTime> _townGreedyTiming = new();
        public static Dictionary<string, CampaignTime> _townCarambushTiming = new();
        public static Dictionary<string, CampaignTime> _townDomiTiming = new();
        public static Dictionary<string, CampaignTime> _lastVisitSetAlley = new();
        public static Dictionary<string, CampaignTime> _larcenyDailyTiming = new();
        public static Dictionary<string, CampaignTime> _larcenyJobsTiming = new();
        public static Dictionary<string, CampaignTime> _InfiltrationAlertTiming = new();

        public static Dictionary<string, int> _supportedBandits = new();
        public static Dictionary<string, int> _stringIntDico = new();
        public static Dictionary<string, int> _stringClanDico = new();
        public static Dictionary<string, string> _assignedGl = new();
        public static Dictionary<string, string> _stringHeroIdDico = new();
        public static List<string> _partnerRecomList = new();
        public static List<string> _territoryList = new();
        public static List<string> _partnershipList = new();
        public static Dictionary<int, int> _crimeValue = new();
        public static Dictionary<int, CampaignTime> _campaignTimeDictio = new();
        public static Dictionary<string, Hero> _stringHeroDico = new();

        public static bool _getSomeHelp;
        public static Hero _gangLeader;
        public static MobileParty _FourbParty;
        public static Village _extoVillage;
        public static Settlement _robCastle;
        public static Settlement _crimeBase;
        public static MobileParty _crimeBaseParty;
        public static MobileParty _insucaraF;
        public static MobileParty _insubandF;
        public static MobileParty _catchbandF;
        public static MobileParty _agentsParty;
        public static List<MobileParty> _banditsFollowers = new();
        public static List<TroopRosterElement> _playerTroopsF = new();
        public static ItemRoster _stash = new();

        public static void Reset()
        {
            foreach (var field in typeof(FourberieBehavior).GetFields())
            {
                if (field.FieldType == typeof(bool))
                {
                    field.SetValue(null, false);
                }
                else if (field.FieldType.IsGenericType || field.FieldType == typeof(ItemRoster))
                {
                    field.SetValue(null, System.Activator.CreateInstance(field.FieldType));
                }
                else
                {
                    field.SetValue(null, null);
                }
            }
        }
    }
}
