using Common.Messaging;
using System;

namespace GameInterface.Services.Modules.Messages;

/// <summary>Asks the module interface to run module validation. Handled by <c>NewHeroHandler</c>.</summary>
public readonly struct ValidateModules : ICommand
{
    /// <summary>
    /// This carries no correlation of its own: it is a local, fire-and-forget command with a single
    /// subscriber, not a request/response pair. It previously threw NotImplementedException here,
    /// which would have taken down any broker path that reads the id for logging or correlation.
    /// </summary>
    public Guid TransactionID => Guid.Empty;
}

/// <summary>Raised once module validation has run.</summary>
public readonly struct ModulesProcessed : IEvent
{
    private readonly ModuleInfo[] modules;

    public ModulesProcessed(ModuleInfo[] modules) => this.modules = modules;

    /// <summary>
    /// Never null. The only publisher constructs this with the default constructor, so the backing
    /// array is unset; returning it raw handed every future subscriber a null to trip over.
    /// </summary>
    public ModuleInfo[] Modules => modules ?? Array.Empty<ModuleInfo>();
}
