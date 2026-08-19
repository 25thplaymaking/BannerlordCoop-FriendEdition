using Common.Messaging;
using GameInterface.Services.ObjectManager;

namespace GameInterface.Utils.NetworkEvents
{
    public abstract record GenericNetworkEvent<TInstance, TValue> : IEvent, IInstanceScopedNetworkEvent
    {
        public abstract string InstanceId { get; set; }

        /// <summary>Exposes the generic parameter so a non-generic filter can resolve the instance.</summary>
        public System.Type InstanceType => typeof(TInstance);

        string IInstanceScopedNetworkEvent.InstanceId => InstanceId;

        public GenericNetworkEvent()
        {
        }

        public GenericNetworkEvent(string instanceId)
        {
            // Compact the id for the wire; the receiver re-adds the "{TInstance}_" prefix by type.
            InstanceId = ObjectManager.Compact(instanceId, typeof(TInstance));
        }
    }
}
