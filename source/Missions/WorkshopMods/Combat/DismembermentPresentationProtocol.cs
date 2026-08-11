using Common.Messaging;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.WorkshopMods.Combat;

/// <summary>
/// Cosmetic-only outcome selected after the victim-authority peer has accepted a real blow. Receivers
/// invoke DismembermentPlus's visual routine directly; this envelope is never fed to RegisterBlow and
/// therefore cannot apply health, death, reward, or campaign consequences a second time.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class DismembermentPresentationEvent : IEvent
{
    [ProtoMember(1)] public string InstanceId { get; }
    [ProtoMember(2)] public string SourceControllerId { get; }
    [ProtoMember(3)] public long Sequence { get; }
    [ProtoMember(4)] public Guid EventId { get; }
    [ProtoMember(5)] public Guid VictimAgentId { get; }
    [ProtoMember(6)] public Guid AttackerAgentId { get; }
    [ProtoMember(7)] public int CollisionBoneIndex { get; }
    [ProtoMember(8)] public int VisualSeed { get; }
    [ProtoMember(9)] public Blow Blow { get; }
    [ProtoMember(10)] public AttackCollisionData CollisionData { get; }

    public DismembermentPresentationEvent(
        string instanceId,
        string sourceControllerId,
        long sequence,
        Guid eventId,
        Guid victimAgentId,
        Guid attackerAgentId,
        int collisionBoneIndex,
        int visualSeed,
        Blow blow,
        AttackCollisionData collisionData)
    {
        InstanceId = instanceId;
        SourceControllerId = sourceControllerId;
        Sequence = sequence;
        EventId = eventId;
        VictimAgentId = victimAgentId;
        AttackerAgentId = attackerAgentId;
        CollisionBoneIndex = collisionBoneIndex;
        VisualSeed = visualSeed;
        Blow = blow;
        CollisionData = collisionData;
    }
}

public enum DismembermentEventValidation
{
    Valid,
    Malformed,
    WrongBattle,
    Unauthorized,
}

public enum DismembermentApplyResult
{
    Apply,
    Duplicate,
    Stale,
    Conflict,
    PresentationUnavailable,
}

internal static class DismembermentEventPolicy
{
    internal const int MaximumIdentityLength = 128;

    internal static DismembermentEventValidation Validate(
        DismembermentPresentationEvent message,
        string currentInstanceId,
        string expectedSourceControllerId,
        Guid expectedVictimId)
    {
        if (!ValidIdentity(message.InstanceId) ||
            !ValidIdentity(message.SourceControllerId) ||
            message.Sequence <= 0 ||
            message.EventId == Guid.Empty ||
            message.VictimAgentId == Guid.Empty ||
            message.AttackerAgentId == Guid.Empty ||
            message.CollisionBoneIndex < sbyte.MinValue ||
            message.CollisionBoneIndex > sbyte.MaxValue)
        {
            return DismembermentEventValidation.Malformed;
        }

        if (!string.Equals(message.InstanceId, currentInstanceId, StringComparison.Ordinal))
            return DismembermentEventValidation.WrongBattle;
        if (!string.Equals(message.SourceControllerId, expectedSourceControllerId, StringComparison.Ordinal) ||
            message.VictimAgentId != expectedVictimId)
            return DismembermentEventValidation.Unauthorized;

        int expectedSeed = CreateVisualSeed(
            message.InstanceId,
            message.SourceControllerId,
            message.VictimAgentId,
            message.AttackerAgentId,
            message.Sequence,
            message.CollisionBoneIndex);
        if (message.VisualSeed != expectedSeed ||
            message.EventId != CreateEventId(
                message.InstanceId,
                message.SourceControllerId,
                message.VictimAgentId,
                message.Sequence,
                expectedSeed))
        {
            return DismembermentEventValidation.Malformed;
        }

        return DismembermentEventValidation.Valid;
    }

    internal static int CreateVisualSeed(
        string instanceId,
        string sourceControllerId,
        Guid victimAgentId,
        Guid attackerAgentId,
        long sequence,
        int collisionBoneIndex)
    {
        string canonical = string.Join("|", new[]
        {
            instanceId ?? string.Empty,
            sourceControllerId ?? string.Empty,
            victimAgentId.ToString("N"),
            attackerAgentId.ToString("N"),
            sequence.ToString(CultureInfo.InvariantCulture),
            collisionBoneIndex.ToString(CultureInfo.InvariantCulture),
        });
        using var sha256 = SHA256.Create();
        byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        int seed = digest[0] |
            digest[1] << 8 |
            digest[2] << 16 |
            (digest[3] & 0x7f) << 24;
        return seed;
    }

    internal static int ToChance(int visualSeed) => (visualSeed & int.MaxValue) % 100;

    internal static Guid CreateEventId(
        string instanceId,
        string sourceControllerId,
        Guid victimId,
        long sequence,
        int visualSeed)
    {
        string canonical = string.Join("|", new[]
        {
            instanceId ?? string.Empty,
            sourceControllerId ?? string.Empty,
            victimId.ToString("N"),
            sequence.ToString(CultureInfo.InvariantCulture),
            visualSeed.ToString(CultureInfo.InvariantCulture),
        });
        using var sha256 = SHA256.Create();
        byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var guid = new byte[16];
        Array.Copy(digest, guid, guid.Length);
        return new Guid(guid);
    }

    private static bool ValidIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentityLength) return false;
        foreach (char character in value)
            if (char.IsControl(character) || character == '|') return false;
        return true;
    }
}

internal sealed class DismembermentPresentationLedger
{
    private readonly int maxStreams;
    private readonly Dictionary<(string Instance, string Source, Guid Victim), (long Sequence, Guid EventId)> streams = new();
    private readonly Queue<(string Instance, string Source, Guid Victim)> insertionOrder = new();

    internal DismembermentPresentationLedger(int maxStreams = 4096)
    {
        if (maxStreams <= 0) throw new ArgumentOutOfRangeException(nameof(maxStreams));
        this.maxStreams = maxStreams;
    }

    internal DismembermentApplyResult Accept(DismembermentPresentationEvent message) =>
        TryApply(message, () => true);

    internal DismembermentApplyResult TryApply(
        DismembermentPresentationEvent message,
        Func<bool> present)
    {
        var key = (message.InstanceId, message.SourceControllerId, message.VictimAgentId);
        if (streams.TryGetValue(key, out var current))
        {
            if (message.Sequence < current.Sequence) return DismembermentApplyResult.Stale;
            if (message.Sequence == current.Sequence)
                return message.EventId == current.EventId
                    ? DismembermentApplyResult.Duplicate
                    : DismembermentApplyResult.Conflict;
        }

        if (!present()) return DismembermentApplyResult.PresentationUnavailable;

        if (streams.ContainsKey(key))
        {
            streams[key] = (message.Sequence, message.EventId);
            return DismembermentApplyResult.Apply;
        }
        while (streams.Count >= maxStreams && insertionOrder.Count > 0)
            streams.Remove(insertionOrder.Dequeue());

        streams.Add(key, (message.Sequence, message.EventId));
        insertionOrder.Enqueue(key);
        return DismembermentApplyResult.Apply;
    }
}

internal sealed class DismembermentSourceSequenceLedger
{
    private readonly int maxStreams;
    private readonly Dictionary<(string Instance, Guid Victim), long> sequences = new();
    private readonly Queue<(string Instance, Guid Victim)> insertionOrder = new();

    internal DismembermentSourceSequenceLedger(int maxStreams = 4096)
    {
        if (maxStreams <= 0) throw new ArgumentOutOfRangeException(nameof(maxStreams));
        this.maxStreams = maxStreams;
    }

    internal long Next(string instanceId, Guid victimId)
    {
        var key = (instanceId, victimId);
        if (sequences.TryGetValue(key, out long current))
        {
            long next = current + 1;
            sequences[key] = next;
            return next;
        }

        while (sequences.Count >= maxStreams && insertionOrder.Count > 0)
            sequences.Remove(insertionOrder.Dequeue());

        sequences.Add(key, 1);
        insertionOrder.Enqueue(key);
        return 1;
    }
}
