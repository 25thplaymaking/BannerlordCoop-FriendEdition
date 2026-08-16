using Common.Messaging;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Server.Services.Time.Messages;

/// <summary>
/// Request time speed change command from a client
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("time.speed.request", AuthorityRouteKind.Command)]
public record NetworkRequestTimeSpeedChange : ICommand
{
    [ProtoMember(1)]
    public TimeControlEnum NewControlMode { get; }

    [ProtoMember(2)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestTimeSpeedChange(TimeControlEnum newControlMode)
        : this(newControlMode, default)
    {
    }

    public NetworkRequestTimeSpeedChange(TimeControlEnum newControlMode, AuthorityRequestHeader header)
    {
        NewControlMode = newControlMode;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTimeSpeedChangeResult : ICommand
{
    [ProtoMember(1)] public readonly TimeControlEnum EffectiveMode;
    [ProtoMember(2)] public readonly AuthorityResultHeader Header;

    public NetworkTimeSpeedChangeResult(TimeControlEnum effectiveMode, AuthorityResultHeader header)
    {
        EffectiveMode = effectiveMode;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTimeSpeedAuthorityState : IEvent
{
    [ProtoMember(1)] public readonly TimeControlEnum EffectiveMode;
    [ProtoMember(2)] public readonly AuthorityResultHeader Header;

    public NetworkTimeSpeedAuthorityState(TimeControlEnum effectiveMode, AuthorityResultHeader header)
    {
        EffectiveMode = effectiveMode;
        Header = header;
    }
}
