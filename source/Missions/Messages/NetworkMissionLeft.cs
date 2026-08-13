using Common.Messaging;
using ProtoBuf;

namespace Missions.Messages;

/// <summary>
/// Sent by a client to the server as it leaves a mission instance, so the server drops the client from the
/// instance's relay routing table. <see cref="InstanceId"/> has the same two forms as
/// <see cref="NetworkMissionEntered"/>.
/// </summary>
[ProtoContract]
public readonly struct NetworkMissionLeft : IEvent
{
    [ProtoMember(1)]
    public readonly string ControllerId;

    [ProtoMember(2)]
    public readonly string InstanceId;

    /// <summary>
    /// True only for a battle mission that ended without an accepted attacker/defender victory. The server
    /// uses the same authenticated departure packet to detach the peer's campaign party from the map event;
    /// this makes campaign cleanup inseparable from mission-membership cleanup.
    /// </summary>
    [ProtoMember(3)]
    public readonly bool LeaveUnresolvedBattle;

    public NetworkMissionLeft(string controllerId, string instanceId, bool leaveUnresolvedBattle = false)
    {
        ControllerId = controllerId;
        InstanceId = instanceId;
        LeaveUnresolvedBattle = leaveUnresolvedBattle;
    }
}
