using Missions.WorkshopMods.Combat;
using Common.Messaging;
using Common.PacketHandlers;
using Common.Serialization;
using GameInterface.Surrogates;
using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.WorkshopMods.Combat;

public sealed class DismembermentPresentationTests
{
    private const string Instance = "battle-1";
    private const string Source = "controller-a";
    private static readonly Guid Victim = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Attacker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int CanonicalSeed = 784512833;
    private static readonly Guid Event = Guid.Parse("593675bf-407e-2300-6de9-12f267cbe976");

    public DismembermentPresentationTests() => new SurrogateCollection();

    [Fact]
    public void EventValidation_RequiresCurrentBattleVictimAuthorityAndBoundedIdentity()
    {
        var valid = Message(sequence: 1);

        Assert.Equal(DismembermentEventValidation.Valid,
            DismembermentEventPolicy.Validate(valid, Instance, Source, Victim));
        Assert.Equal(DismembermentEventValidation.WrongBattle,
            DismembermentEventPolicy.Validate(valid, "other", Source, Victim));
        Assert.Equal(DismembermentEventValidation.Unauthorized,
            DismembermentEventPolicy.Validate(valid, Instance, "forged", Victim));
        Assert.Equal(DismembermentEventValidation.Malformed,
            DismembermentEventPolicy.Validate(Message(sequence: 0), Instance, Source, Victim));
        Assert.Equal(DismembermentEventValidation.Malformed,
            DismembermentEventPolicy.Validate(Message(sequence: 1, eventId: Guid.Empty), Instance, Source, Victim));
        Assert.Equal(DismembermentEventValidation.Malformed,
            DismembermentEventPolicy.Validate(
                Message(sequence: 1, visualSeed: CanonicalSeed + 1),
                Instance,
                Source,
                Victim));
        Assert.Equal(DismembermentEventValidation.Malformed,
            DismembermentEventPolicy.Validate(
                Message(
                    sequence: 1,
                    eventId: Guid.Parse("44444444-4444-4444-4444-444444444444")),
                Instance,
                Source,
                Victim));
    }

    [Fact]
    public void Ledger_AppliesOnceAndRejectsDuplicateStaleAndConflictingSequence()
    {
        var ledger = new DismembermentPresentationLedger(maxStreams: 16);
        DismembermentPresentationEvent first = Message(sequence: 7);

        Assert.Equal(DismembermentApplyResult.Apply, ledger.Accept(first));
        Assert.Equal(DismembermentApplyResult.Duplicate, ledger.Accept(first));
        Assert.Equal(DismembermentApplyResult.Stale, ledger.Accept(Message(sequence: 6)));
        Assert.Equal(DismembermentApplyResult.Conflict,
            ledger.Accept(Message(sequence: 7, eventId: Guid.Parse("44444444-4444-4444-4444-444444444444"))));
        Assert.Equal(DismembermentApplyResult.Apply, ledger.Accept(Message(sequence: 8)));
    }

    [Fact]
    public void Ledger_ScopesSequencesToTheBattleInstance()
    {
        var ledger = new DismembermentPresentationLedger(maxStreams: 16);

        Assert.Equal(DismembermentApplyResult.Apply, ledger.Accept(Message(sequence: 9)));
        Assert.Equal(
            DismembermentApplyResult.Apply,
            ledger.Accept(Message(sequence: 1, instanceId: "battle-2")));
    }

    [Fact]
    public void Ledger_DoesNotConsumeAnEventUntilPresentationSucceeds()
    {
        var ledger = new DismembermentPresentationLedger(maxStreams: 16);
        DismembermentPresentationEvent message = Message(sequence: 1);
        int attempts = 0;

        Assert.Equal(
            DismembermentApplyResult.PresentationUnavailable,
            ledger.TryApply(message, () =>
            {
                attempts++;
                return false;
            }));
        Assert.Equal(
            DismembermentApplyResult.Apply,
            ledger.TryApply(message, () =>
            {
                attempts++;
                return true;
            }));
        Assert.Equal(
            DismembermentApplyResult.Duplicate,
            ledger.TryApply(message, () => throw new InvalidOperationException("duplicate replay ran")));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void SourceSequenceLedger_IsBattleScopedAndBounded()
    {
        var sequences = new DismembermentSourceSequenceLedger(maxStreams: 2);

        Assert.Equal(1, sequences.Next(Instance, Victim));
        Assert.Equal(2, sequences.Next(Instance, Victim));
        Assert.Equal(1, sequences.Next("battle-2", Victim));
        Assert.Equal(1, sequences.Next("battle-3", Attacker));
        Assert.Equal(1, sequences.Next(Instance, Victim));
    }

    [Fact]
    public void Seed_IsStableForTheAcceptedBlowAndChangesWithSequence()
    {
        int first = DismembermentEventPolicy.CreateVisualSeed(Instance, Source, Victim, Attacker, 9, 12);
        int replay = DismembermentEventPolicy.CreateVisualSeed(Instance, Source, Victim, Attacker, 9, 12);
        int next = DismembermentEventPolicy.CreateVisualSeed(Instance, Source, Victim, Attacker, 10, 12);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, next);
        Assert.InRange(DismembermentEventPolicy.ToChance(first), 0, 99);
    }

    [Fact]
    public void Event_RoundTripsTheExactSelectedOutcomeWithoutASecondDamageCommand()
    {
        var blow = new Blow(17)
        {
            InflictedDamage = 42,
            DamageType = DamageTypes.Cut,
        };
        var original = new DismembermentPresentationEvent(
            Instance,
            Source,
            sequence: 12,
            Event,
            Victim,
            Attacker,
            collisionBoneIndex: 9,
            visualSeed: 9876,
            blow,
            collisionData: default);
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());

        MessagePacket packet = MessagePacket.Create(original, serializer);
        var result = Assert.IsType<DismembermentPresentationEvent>(
            serializer.Deserialize<IMessage>(packet.Data));

        Assert.Equal(original.InstanceId, result.InstanceId);
        Assert.Equal(original.SourceControllerId, result.SourceControllerId);
        Assert.Equal(original.Sequence, result.Sequence);
        Assert.Equal(original.EventId, result.EventId);
        Assert.Equal(original.VictimAgentId, result.VictimAgentId);
        Assert.Equal(original.AttackerAgentId, result.AttackerAgentId);
        Assert.Equal(original.CollisionBoneIndex, result.CollisionBoneIndex);
        Assert.Equal(original.VisualSeed, result.VisualSeed);
    }

    private static DismembermentPresentationEvent Message(
        long sequence,
        Guid? eventId = null,
        string instanceId = Instance,
        int visualSeed = CanonicalSeed) =>
        new(
            instanceId,
            Source,
            sequence,
            eventId ?? Event,
            Victim,
            Attacker,
            collisionBoneIndex: 12,
            visualSeed,
            blow: default,
            collisionData: default);
}
