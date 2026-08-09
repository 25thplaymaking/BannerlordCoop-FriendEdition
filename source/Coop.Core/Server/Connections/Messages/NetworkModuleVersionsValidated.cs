using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;

namespace Coop.Core.Server.Connections.Messages;

/// <summary>
/// Response to <see cref="NetworkModuleVersionsValidate"/>.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkModuleVersionsValidated : IEvent
{
    [ProtoMember(1)]
    public bool Matches { get; }
    [ProtoMember(2)]
    public string Reason { get; }
    [ProtoMember(3)]
    public WorkshopCompatibilityManifest ServerWorkshopManifest { get; }
    [ProtoMember(4)]
    public ModConfigSnapshot HostModConfig { get; }

    public NetworkModuleVersionsValidated(bool matches, string reason)
        : this(matches, reason, null, null)
    {
    }

    public NetworkModuleVersionsValidated(
        bool matches,
        string reason,
        WorkshopCompatibilityManifest serverWorkshopManifest)
        : this(matches, reason, serverWorkshopManifest, null)
    {
    }

    public NetworkModuleVersionsValidated(
        bool matches,
        string reason,
        WorkshopCompatibilityManifest serverWorkshopManifest,
        ModConfigSnapshot hostModConfig)
    {
        Matches = matches;
        Reason = reason != null && reason.Length > 2048 ? reason.Substring(0, 2048) : reason;
        ServerWorkshopManifest = serverWorkshopManifest;
        HostModConfig = hostModConfig;
    }
}
