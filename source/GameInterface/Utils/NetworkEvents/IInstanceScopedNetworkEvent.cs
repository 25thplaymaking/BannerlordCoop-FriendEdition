using System;

namespace GameInterface.Utils.NetworkEvents
{
    /// <summary>
    /// A generated replication message, reduced to the two things a relevance filter needs: which game
    /// object it is about, and what type that id names.
    /// </summary>
    /// <remarks>
    /// <see cref="GenericNetworkEvent{TInstance, TValue}"/> already carries both, but only through a
    /// generic base, so nothing downstream could read them without reflection. This non-generic view
    /// lets one filter at the send chokepoint cover every AutoSync route at once.
    /// </remarks>
    public interface IInstanceScopedNetworkEvent
    {
        /// <summary>Wire id of the object this message updates, with its type prefix already stripped.</summary>
        string InstanceId { get; }

        /// <summary>Type the id names, needed to put the prefix back and resolve the object.</summary>
        Type InstanceType { get; }
    }
}
