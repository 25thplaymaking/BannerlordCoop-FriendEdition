using Common.Messaging;
using GameInterface.Services.Modules;
using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Coop.Core.Server.Connections.Messages;

/// <summary>
/// Message from Client to Server for validating the module versions.
/// Responsibilities
/// 1. Make sure that all active modules have the same version as on the server
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkModuleVersionsValidate : ICommand
{
    public const int MaximumModules = 128;
    public const int MaximumModuleIdLength = 96;

    [ProtoMember(1)]
    public NetworkModuleInfo[] Modules { get; }
    [ProtoMember(2)]
    public WorkshopCompatibilityManifest WorkshopManifest { get; }

    public NetworkModuleVersionsValidate(IEnumerable<ModuleInfo> modules)
        : this(modules, null)
    {
    }

    public NetworkModuleVersionsValidate(
        IEnumerable<ModuleInfo> modules,
        WorkshopCompatibilityManifest workshopManifest)
    {
        if (modules is null)
        {
            Modules = Array.Empty<NetworkModuleInfo>();
        }
        else
        {
            var bounded = modules.Take(MaximumModules + 1).ToArray();
            if (bounded.Length > MaximumModules)
                throw new ArgumentOutOfRangeException(nameof(modules),
                    $"A module validation request cannot exceed {MaximumModules} entries.");

            Modules = bounded
                .Select(m => new NetworkModuleInfo(m.Id, m.IsOfficial, m.IsDlc, m.Version))
                .ToArray();
        }

        WorkshopManifest = workshopManifest;
    }

    public bool TryValidateWireShape(out string error, bool requireOfficialModule = true)
    {
        if (Modules == null || Modules.Length > MaximumModules)
        {
            error = $"Module validation request exceeds the {MaximumModules}-entry limit.";
            return false;
        }

        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool officialModuleFound = false;
        foreach (var module in Modules)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.Id) ||
                module.Id.Length > MaximumModuleIdLength || module.Version == null)
            {
                error = "Module validation request contains an invalid or oversized module identity.";
                return false;
            }

            if (!moduleIds.Add(module.Id))
            {
                error = $"Module validation request contains duplicate module '{module.Id}'.";
                return false;
            }

            if (module.Version.ApplicationVersionType < 0 ||
                module.Version.Major < 0 || module.Version.Minor < 0 ||
                module.Version.Revision < 0 || module.Version.ChangeSet < 0)
            {
                error = $"Module validation request contains an invalid version for '{module.Id}'.";
                return false;
            }

            // A missing protobuf version submessage is caught above. Its other default wire form is
            // a present object whose every scalar is zero; do not pass that through to
            // ApplicationVersion/ModuleValidator and rely on them throwing or misidentifying the
            // official game build. Legacy non-enforcing unit-test compositions may still use empty
            // versions for their synthetic modules.
            if (requireOfficialModule && module.IsOfficial &&
                module.Version.ApplicationVersionType == 0 &&
                module.Version.Major == 0 && module.Version.Minor == 0 &&
                module.Version.Revision == 0 && module.Version.ChangeSet == 0)
            {
                error = $"Official module '{module.Id}' does not provide a valid game version.";
                return false;
            }

            officialModuleFound |= module.IsOfficial;
        }

        if (requireOfficialModule && !officialModuleFound)
        {
            error = "Module validation request does not identify the client's official game version.";
            return false;
        }

        error = null;
        return true;
    }
}
