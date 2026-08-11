using Common;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal sealed class DiplomacyMessengerAuthorityRecord
{
    [SaveableField(1)] internal int MessengerId;
    [SaveableField(2)] internal string ControllerId;
    [SaveableField(3)] internal string TargetHeroId;
    [SaveableField(4)] internal long ArrivalTicks;
    [SaveableField(5)] internal int AccidentIndex;

    private DiplomacyMessengerAuthorityRecord()
    {
    }

    internal DiplomacyMessengerAuthorityRecord(
        int messengerId,
        string controllerId,
        string targetHeroId,
        long arrivalTicks,
        int accidentIndex = -1)
    {
        MessengerId = messengerId;
        ControllerId = controllerId;
        TargetHeroId = targetHeroId;
        ArrivalTicks = arrivalTicks;
        AccidentIndex = accidentIndex;
    }

    internal DiplomacyMessengerAuthorityRecord Copy() => new(
        MessengerId, ControllerId, TargetHeroId, ArrivalTicks, AccidentIndex);
}

internal sealed class DiplomacyMessengerAuthorityStore
{
    internal const int MaximumActivePerController = 16;
    private readonly List<DiplomacyMessengerAuthorityRecord> records;

    internal DiplomacyMessengerAuthorityStore(
        IEnumerable<DiplomacyMessengerAuthorityRecord> restored = null,
        int nextId = 1)
    {
        records = (restored ?? Enumerable.Empty<DiplomacyMessengerAuthorityRecord>())
            .Where(IsRecordValid)
            .GroupBy(record => record.MessengerId)
            .Select(group => group.First().Copy())
            .OrderBy(record => record.MessengerId)
            .ToList();
        int afterRestored = records.Count == 0 ? 1 : records.Max(record => record.MessengerId) + 1;
        NextId = Math.Max(Math.Max(nextId, 1), afterRestored);
    }

    internal int NextId { get; private set; }

    internal bool CanDispatch(string controllerId) =>
        IsStableIdentity(controllerId) &&
        records.Count(record => string.Equals(
            record.ControllerId, controllerId, StringComparison.Ordinal)) < MaximumActivePerController &&
        NextId > 0 && NextId < int.MaxValue;

    internal bool TryDispatch(
        string controllerId,
        string targetHeroId,
        long arrivalTicks,
        out int messengerId)
    {
        messengerId = 0;
        if (!CanDispatch(controllerId) || !IsStableIdentity(targetHeroId) || arrivalTicks < 0)
            return false;

        messengerId = NextId++;
        records.Add(new DiplomacyMessengerAuthorityRecord(
            messengerId, controllerId, targetHeroId, arrivalTicks));
        return true;
    }

    internal bool TryGetArrived(
        string controllerId,
        int messengerId,
        string targetHeroId,
        long nowTicks,
        out DiplomacyMessengerAuthorityRecord record)
    {
        record = Find(controllerId, messengerId, targetHeroId);
        return record != null && record.AccidentIndex < 0 && record.ArrivalTicks <= nowTicks;
    }

    internal IReadOnlyList<DiplomacyMessengerAuthorityRecord> ArrivalsFor(
        string controllerId,
        long nowTicks) => records
        .Where(record => string.Equals(record.ControllerId, controllerId, StringComparison.Ordinal) &&
                         record.AccidentIndex < 0 && record.ArrivalTicks <= nowTicks)
        .Select(record => record.Copy())
        .ToArray();

    internal IReadOnlyList<DiplomacyMessengerAuthorityRecord> PendingForAccident(long nowTicks) => records
        .Where(record => record.AccidentIndex < 0 && record.ArrivalTicks > nowTicks)
        .Select(record => record.Copy())
        .ToArray();

    internal IReadOnlyList<DiplomacyMessengerAuthorityRecord> AccidentsFor(string controllerId) => records
        .Where(record => string.Equals(record.ControllerId, controllerId, StringComparison.Ordinal) &&
                         record.AccidentIndex >= 0)
        .Select(record => record.Copy())
        .ToArray();

    internal bool TryMarkAccident(int messengerId, int accidentIndex)
    {
        if (accidentIndex is < 0 or > 6) return false;
        DiplomacyMessengerAuthorityRecord record = records.FirstOrDefault(candidate =>
            candidate.MessengerId == messengerId && candidate.AccidentIndex < 0);
        if (record == null) return false;
        record.AccidentIndex = accidentIndex;
        return true;
    }

    internal bool TryRemoveArrived(
        string controllerId,
        int messengerId,
        string targetHeroId,
        long nowTicks)
    {
        DiplomacyMessengerAuthorityRecord record = Find(controllerId, messengerId, targetHeroId);
        if (record == null || record.AccidentIndex >= 0 || record.ArrivalTicks > nowTicks) return false;
        return records.Remove(record);
    }

    internal bool TryRemoveAccident(
        string controllerId,
        int messengerId,
        string targetHeroId)
    {
        DiplomacyMessengerAuthorityRecord record = Find(controllerId, messengerId, targetHeroId);
        if (record == null || record.AccidentIndex < 0) return false;
        return records.Remove(record);
    }

    internal IReadOnlyList<DiplomacyMessengerAuthorityRecord> Export() =>
        records.Select(record => record.Copy()).ToArray();

    private DiplomacyMessengerAuthorityRecord Find(
        string controllerId,
        int messengerId,
        string targetHeroId) => records.FirstOrDefault(record =>
        record.MessengerId == messengerId &&
        string.Equals(record.ControllerId, controllerId, StringComparison.Ordinal) &&
        string.Equals(record.TargetHeroId, targetHeroId, StringComparison.Ordinal));

    private static bool IsRecordValid(DiplomacyMessengerAuthorityRecord record) =>
        record != null && record.MessengerId > 0 && record.ArrivalTicks >= 0 &&
        record.AccidentIndex is >= -1 and <= 6 && IsStableIdentity(record.ControllerId) &&
        IsStableIdentity(record.TargetHeroId);

    private static bool IsStableIdentity(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= DiplomacyOperationProtocol.MaximumStableIdLength &&
        value.All(character => !char.IsControl(character) && character != '|');
}

/// <summary>
/// Save/restart owner for controller-scoped messenger travel. Only the server registers ticks;
/// clients receive arrival/accident prompts and never own travel RNG or campaign consequences.
/// </summary>
public sealed class DiplomacyMessengerAuthorityBehavior : CampaignBehaviorBase
{
    private const string RecordsSaveKey = "_coop_diplomacy_messenger_records_v1";
    private const string NextIdSaveKey = "_coop_diplomacy_messenger_next_id_v1";
    private DiplomacyMessengerAuthorityStore store = new();

    internal DiplomacyMessengerAuthorityStore Store => store;

    public override void RegisterEvents()
    {
        if (!ModInformation.IsServer) return;
        CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, PublishOutcomes);
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, ProcessAccidents);
    }

    public override void SyncData(IDataStore dataStore)
    {
        if (!ModInformation.IsServer) return;

        List<DiplomacyMessengerAuthorityRecord> records = store.Export().ToList();
        int nextId = store.NextId;
        dataStore.SyncData(RecordsSaveKey, ref records);
        dataStore.SyncData(NextIdSaveKey, ref nextId);
        if (dataStore.IsLoading)
            store = new DiplomacyMessengerAuthorityStore(records, nextId);
    }

    private void PublishOutcomes()
    {
        if (ContainerProvider.TryResolve<DiplomacyOperationHandler>(out var handler))
            handler.PublishPendingMessengerOutcomes();
    }

    private void ProcessAccidents()
    {
        if (ContainerProvider.TryResolve<DiplomacyOperationHandler>(out var handler))
            handler.ProcessMessengerAccidents();
    }
}

public sealed class DiplomacyMessengerSaveableTypeDefiner : SaveableTypeDefiner
{
    private const int SaveBaseId = 44_181_000;

    public DiplomacyMessengerSaveableTypeDefiner() : base(SaveBaseId)
    {
    }

    public override void DefineClassTypes() =>
        AddClassDefinition(typeof(DiplomacyMessengerAuthorityRecord), 1);

    public override void DefineContainerDefinitions() =>
        ConstructContainerDefinition(typeof(List<DiplomacyMessengerAuthorityRecord>));
}
