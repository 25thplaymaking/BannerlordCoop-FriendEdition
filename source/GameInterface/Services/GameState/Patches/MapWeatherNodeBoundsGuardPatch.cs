using HarmonyLib;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.GameState.Patches;

/// <summary>
/// Keeps <see cref="DefaultMapWeatherModel.GetWeatherEventInPosition(Vec2)"/> inside its weather-node
/// array when the campaign map is larger than the map scene reports.
/// </summary>
/// <remarks>
/// The node index is derived from the scene, but the array it indexes is sized from the campaign:
/// <code>
/// Vec2 terrainSize = Campaign.Current.MapSceneWrapper.GetTerrainSize();
/// xIndex = (int)(pos.x / (terrainSize.X / DefaultWeatherNodeDimension));
/// return _weatherDataCache[yIndex * DefaultWeatherNodeDimension + xIndex];
/// </code>
/// Neither index is bounds-checked. So any position beyond the terrain size the scene reports indexes
/// past <c>_weatherDataCache</c> and throws <see cref="System.IndexOutOfRangeException"/>.
/// <para>
/// That is the state the dedicated host runs in under a total conversion: its headless map scene loads
/// the proven native set and reports Native's terrain size, while the conversion's parties sit at
/// coordinates far outside it. The throw lands in <c>MobileParty.CalculateSpeed</c>, so it fires once
/// per moving party per tick — measured at ~9700 in five minutes on the Europe 1100 host — and every
/// one of those parties fails its tick in <c>ParallelTickMovingParties</c>. The campaign then advances
/// in visible jerks and AI parties barely move: the cost is not the wrong weather, it is throwing
/// thousands of exceptions on the game thread.
/// </para>
/// <para>
/// Clamping to the nearest valid node keeps weather spatially continuous and makes the lookup total,
/// which is what the native code should have done. An in-range position is left entirely alone — the
/// original runs and returns the accurate value — so on a client, where the scene and the campaign
/// agree, this patch never takes effect.
/// </para>
/// <para>
/// This treats the symptom. The cause is the headless map scene reporting a terrain size that does not
/// match the loaded campaign, and it belongs in that scene: anything else deriving a grid index from
/// <c>GetTerrainSize()</c> is wrong in the same way, silently rather than loudly.
/// </para>
/// </remarks>
[HarmonyPatch]
internal class MapWeatherNodeBoundsGuardPatch
{
    private static readonly FieldInfo WeatherDataCacheField =
        AccessTools.Field(typeof(DefaultMapWeatherModel), "_weatherDataCache");

    static MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(DefaultMapWeatherModel),
            nameof(DefaultMapWeatherModel.GetWeatherEventInPosition),
            new[] { typeof(Vec2) });

    static bool Prefix(DefaultMapWeatherModel __instance, Vec2 pos, ref MapWeatherModel.WeatherEvent __result)
        => !TryAnswerOutOfBounds(__instance, pos, ref __result);

    /// <summary>
    /// True when <paramref name="pos"/> falls outside the weather grid and <paramref name="result"/>
    /// has been filled from the nearest valid node instead.
    /// </summary>
    internal static bool TryAnswerOutOfBounds(
        DefaultMapWeatherModel instance, Vec2 pos, ref MapWeatherModel.WeatherEvent result)
    {
        Campaign campaign = Campaign.Current;
        var mapScene = campaign?.MapSceneWrapper;
        if (mapScene == null) return false;

        int dimension = campaign.DefaultWeatherNodeDimension;
        if (dimension <= 0) return false;

        Vec2 terrainSize = mapScene.GetTerrainSize();
        if (terrainSize.X <= 0f || terrainSize.Y <= 0f) return false;

        int xIndex = (int)(pos.x / (terrainSize.X / dimension));
        int yIndex = (int)(pos.y / (terrainSize.Y / dimension));

        // In range: the original is correct and cheaper than anything done here.
        if (xIndex >= 0 && xIndex < dimension && yIndex >= 0 && yIndex < dimension) return false;

        if (WeatherDataCacheField?.GetValue(instance) is not MapWeatherModel.WeatherEvent[] cache || cache.Length == 0)
        {
            // Reached before InitializeWeatherData, or the field moved in a game update. Answering
            // Clear is still better than the alternative, which is the exception this exists to stop.
            result = MapWeatherModel.WeatherEvent.Clear;
            return true;
        }

        int clampedX = MathF.Max(0, MathF.Min(dimension - 1, xIndex));
        int clampedY = MathF.Max(0, MathF.Min(dimension - 1, yIndex));

        int index = clampedY * dimension + clampedX;
        result = (index >= 0 && index < cache.Length) ? cache[index] : MapWeatherModel.WeatherEvent.Clear;
        return true;
    }
}

/// <summary>
/// The same bounds problem on the WRITE path.
/// </summary>
/// <remarks>
/// <c>UpdateWeatherForPosition</c> routes into <c>SetIsRainingOrWetFromFunction</c> /
/// <c>SetIsBlizzardOrSnowFromFunction</c>, which assign <c>_weatherDataCache[yIndex * dim + xIndex]</c>
/// with the same unchecked indices as the read path. Guarding only the read left this reachable, and
/// it is: <c>BattleMissionStartHandler.GetAtmosphereOnCampaign</c> calls
/// <c>GetAtmosphereModel</c> -> <c>UpdateWeatherForPosition</c> when a battle starts, so a fight far
/// enough out on a conversion's map throws on the map-event.battle-start route.
/// </remarks>
[HarmonyPatch]
internal class MapWeatherUpdateBoundsGuardPatch
{
    static MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(DefaultMapWeatherModel),
            nameof(DefaultMapWeatherModel.UpdateWeatherForPosition));

    static bool Prefix(
        DefaultMapWeatherModel __instance, CampaignVec2 position, ref MapWeatherModel.WeatherEvent __result)
    {
        var result = default(MapWeatherModel.WeatherEvent);
        if (!MapWeatherNodeBoundsGuardPatch.TryAnswerOutOfBounds(__instance, position.ToVec2(), ref result))
            return true;

        // Out of grid: answer from the nearest valid node and skip the unchecked write entirely.
        __result = result;
        return false;
    }
}
