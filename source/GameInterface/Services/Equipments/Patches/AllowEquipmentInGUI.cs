using Common.Util;
using HarmonyLib;
using SandBox.GauntletUI;
using SandBox.ViewModelCollection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.ViewModelCollection;
using TaleWorlds.CampaignSystem.ViewModelCollection.CharacterDeveloper;
using TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement;
using TaleWorlds.CampaignSystem.ViewModelCollection.Inventory;
using TaleWorlds.CampaignSystem.ViewModelCollection.Party;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Scripts;
using TaleWorlds.MountAndBlade.View.Tableaus;
using TaleWorlds.MountAndBlade.View.Tableaus.Thumbnails;

namespace GameInterface.Services.Equipments.Patches;

[HarmonyPatch]
internal class AllowEquipmentInGUI
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var explicitMethods = new MethodBase[]
        {
            AccessTools.Method(typeof(CampaignUIHelper), nameof(CampaignUIHelper.GetCharacterCode)),
            AccessTools.Method(typeof(SandBoxUIHelper), nameof(SandBoxUIHelper.GetCharacterCode)),
            AccessTools.Method(typeof(Mission), nameof(Mission.SpawnAgent)),
            AccessTools.Method(typeof(CharacterSpawner), nameof(CharacterSpawner.InitWithCharacter)),
            AccessTools.Method(typeof(CharacterThumbnailCache), "GetPoseParamsFromCharacterCode")
        };

        if (Common.ModInformation.IsServer)
        {
            return explicitMethods.Where(m => m != null);
        }

        var typesToWrap = new Type[]
        {
            typeof(GauntletCharacterDeveloperScreen),
            typeof(GauntletInventoryScreen),
            typeof(GauntletClanScreen),
            typeof(GauntletPartyScreen),
            typeof(CharacterDeveloperVM),
            typeof(SPInventoryVM),
            typeof(ClanManagementVM),
            typeof(PartyVM),
            typeof(InventoryLogic)
        };

        var discoveredMethods = new List<MethodBase>();
        foreach (var type in typesToWrap)
        {
            if (type == null) continue;

            try
            {
                foreach (var ctor in AccessTools.GetDeclaredConstructors(type))
                {
                    if (ctor != null) discoveredMethods.Add(ctor);
                }

                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method != null && !method.IsAbstract && !method.IsGenericMethod)
                    {
                        discoveredMethods.Add(method);
                    }
                }
            }
            catch
            {
            }
        }

        return explicitMethods.Where(m => m != null).Concat(discoveredMethods);
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        AllowedThread.AllowThisThread();
    }

    [HarmonyFinalizer]
    private static void Finalizer()
    {
        AllowedThread.RevokeThisThread();
    }
}

