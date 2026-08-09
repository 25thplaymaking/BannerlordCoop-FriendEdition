using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Players.Data;
using ProtoBuf;
using Serilog;

namespace Coop.Core.Server.Connections.Messages;

/// <summary>
/// Message from Client to Server for validating the client
/// Responsibilities
/// 1. Associate client with existing hero
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkClientValidate : ICommand
{
    private static readonly ILogger Logger = LogManager.GetLogger<NetworkClientValidate>();

    [ProtoMember(1)]
    public string PlayerId { get; }
    [ProtoMember(2)] public string ConfigSessionId { get; }
    [ProtoMember(3)] public long ConfigRevision { get; }
    [ProtoMember(4)] public string ConfigSha256 { get; }

    public NetworkClientValidate(string playerId)
        : this(playerId, null)
    {
    }

    public NetworkClientValidate(string playerId, ModConfigSnapshot acceptedConfig)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            Logger.Error("Controller Id was not set properly before validation has started");
        }

        PlayerId = playerId;
        ConfigSessionId = acceptedConfig?.SessionId;
        ConfigRevision = acceptedConfig?.Revision ?? 0;
        ConfigSha256 = acceptedConfig?.Sha256;
    }

    public bool Acknowledges(ModConfigSnapshot snapshot) =>
        snapshot != null &&
        ConfigRevision == snapshot.Revision &&
        string.Equals(ConfigSessionId, snapshot.SessionId, System.StringComparison.Ordinal) &&
        string.Equals(ConfigSha256, snapshot.Sha256, System.StringComparison.Ordinal);
}

/// <summary>
/// Response to <see cref="NetworkClientValidate"/> when successful
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkClientValidated : IEvent
{
    [ProtoMember(1)]
    public bool HeroExists { get; }
    [ProtoMember(2)]
    public Player Player { get; }

    public NetworkClientValidated(bool heroExists, Player player)
    {
        HeroExists = heroExists;
        Player = player;
    }
}
