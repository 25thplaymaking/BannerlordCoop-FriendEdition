using GameInterface.AutoSync;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Drives <see cref="IWorkshopModule.RegisterSync"/> for every module whose pinned build is actually
/// loaded. Auto-discovered like every other <see cref="IAutoSync"/> class, so a new module's synced
/// members arrive through the same path as Coop's own — no extra wiring per mod.
/// </summary>
/// <remarks>
/// Only installed modules are asked. A module that is not there would be resolving types out of an
/// assembly that does not exist, and the registry has no way to express "sync this member later".
/// <para>
/// Exceptions are not swallowed. RegisterSync runs only for a module whose assembly matched its pin
/// byte for byte, so a failure here cannot be an environment problem — it means the declaration
/// itself is wrong, and a wrong declaration that carries on quietly ships a session that looks
/// synchronized and is not.
/// </para>
/// </remarks>
internal sealed class WorkshopModuleAutoSync : IAutoSync
{
    public WorkshopModuleAutoSync(AutoSyncRegistry registry, IEnumerable<IWorkshopModule> modules)
    {
        foreach (var module in WorkshopModuleRegistrar.ResolveInstalledModules(modules))
        {
            module.RegisterSync(registry);
        }
    }
}
