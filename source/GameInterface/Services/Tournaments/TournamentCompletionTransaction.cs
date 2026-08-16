using System;
using System.Collections.Generic;

namespace GameInterface.Services.Tournaments;

internal enum TournamentCompletionStep
{
    Leaderboard,
    Influence,
    Prize,
    BetPayout,
    BetSettlement,
    SimulationProgression,
    FinishedEvent,
    NativeRemoval,
    SessionRemoval
}

internal enum TournamentCompletionState
{
    NotStarted,
    InProgress,
    Completed
}

internal sealed class TournamentCompletionTransaction
{
    private readonly Dictionary<TournamentCompletionStep, TournamentCompletionState> steps = new();

    public bool IsCompleted => StateOf(TournamentCompletionStep.SessionRemoval) == TournamentCompletionState.Completed;
    public bool IsReadyForRemoval =>
        StateOf(TournamentCompletionStep.Leaderboard) == TournamentCompletionState.Completed &&
        StateOf(TournamentCompletionStep.Influence) == TournamentCompletionState.Completed &&
        StateOf(TournamentCompletionStep.Prize) == TournamentCompletionState.Completed &&
        StateOf(TournamentCompletionStep.BetPayout) == TournamentCompletionState.Completed &&
        StateOf(TournamentCompletionStep.BetSettlement) == TournamentCompletionState.Completed &&
        StateOf(TournamentCompletionStep.SimulationProgression) == TournamentCompletionState.Completed;

    public TournamentCompletionState StateOf(TournamentCompletionStep step) =>
        steps.TryGetValue(step, out var state) ? state : TournamentCompletionState.NotStarted;

    public void Run(TournamentCompletionStep step, Action action)
    {
        if (StateOf(step) != TournamentCompletionState.NotStarted)
            return;
        // Persist the consumption boundary before any native action. A throw cannot cause a duplicate award.
        steps[step] = TournamentCompletionState.InProgress;
        action();
        steps[step] = TournamentCompletionState.Completed;
    }
}
