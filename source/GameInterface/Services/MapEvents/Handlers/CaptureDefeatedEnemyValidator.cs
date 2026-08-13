using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

internal readonly struct CaptureDefeatedEnemyState
{
    public readonly bool IsFieldBattle;
    public readonly bool HasSettlement;
    public readonly bool IsFinalized;
    public readonly BattleState BattleState;
    public readonly bool PlayerIsAttacker;
    public readonly bool PlayerIsDefender;
    public readonly bool FriendlyHasHealthyMember;
    public readonly int EnemyPartyCount;
    public readonly bool EnemyHasSettlement;
    public readonly bool EnemyHasPlayerParty;
    public readonly bool EnemyHasHealthyMember;

    public CaptureDefeatedEnemyState(
        bool isFieldBattle,
        bool hasSettlement,
        bool isFinalized,
        BattleState battleState,
        bool playerIsAttacker,
        bool playerIsDefender,
        bool friendlyHasHealthyMember,
        int enemyPartyCount,
        bool enemyHasSettlement,
        bool enemyHasPlayerParty,
        bool enemyHasHealthyMember)
    {
        IsFieldBattle = isFieldBattle;
        HasSettlement = hasSettlement;
        IsFinalized = isFinalized;
        BattleState = battleState;
        PlayerIsAttacker = playerIsAttacker;
        PlayerIsDefender = playerIsDefender;
        FriendlyHasHealthyMember = friendlyHasHealthyMember;
        EnemyPartyCount = enemyPartyCount;
        EnemyHasSettlement = enemyHasSettlement;
        EnemyHasPlayerParty = enemyHasPlayerParty;
        EnemyHasHealthyMember = enemyHasHealthyMember;
    }
}

/// <summary>Pure policy for the server-authoritative post-battle capture request.</summary>
internal static class CaptureDefeatedEnemyValidator
{
    internal static bool TryGetWinner(CaptureDefeatedEnemyState state, out BattleState winner)
    {
        winner = BattleState.None;

        if (!state.IsFieldBattle ||
            state.HasSettlement ||
            state.IsFinalized ||
            state.BattleState != BattleState.None ||
            state.PlayerIsAttacker == state.PlayerIsDefender ||
            !state.FriendlyHasHealthyMember ||
            state.EnemyPartyCount <= 0 ||
            state.EnemyHasSettlement ||
            state.EnemyHasPlayerParty ||
            state.EnemyHasHealthyMember)
        {
            return false;
        }

        winner = state.PlayerIsAttacker
            ? BattleState.AttackerVictory
            : BattleState.DefenderVictory;
        return true;
    }
}
