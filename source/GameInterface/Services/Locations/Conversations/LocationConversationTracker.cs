using Common.Messaging;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Locations.Messages.Conversation;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.Locations.Conversations;

/// <summary>
/// Server-side registry of which settlement-location NPC each player is currently talking to, so no two
/// players can hold a conversation with the same NPC at once.
/// </summary>
/// <remarks>
/// Each client runs its own settlement mission, so the "same NPC" is a logical identity - the synced
/// <see cref="TaleWorlds.CampaignSystem.CharacterObject"/> within a <see cref="TaleWorlds.CampaignSystem.Settlements.Locations.Location"/>,
/// keyed by their co-op ids - not a shared agent. When a client opens a conversation with such an NPC it
/// asks the server; the server records the engagement here and refuses any other player's request for the
/// same NPC until the conversation ends. Engagements are keyed per player by the requesting client's
/// <see cref="LiteNetLib.NetPeer"/>, so each player holds at most one at a time. Unlike a map-party hold
/// there is nothing to freeze - this is pure bookkeeping, so it is unit-testable without the game.
/// </remarks>
internal sealed class LocationConversationTracker : IHandler
{
    private readonly struct Engagement
    {
        public readonly string EngagerNpcKey;
        public readonly string TargetNpcKey;

        public Engagement(string engagerNpcKey, string targetNpcKey)
        {
            EngagerNpcKey = engagerNpcKey;
            TargetNpcKey = targetNpcKey;
        }
    }

    /// <summary>
    /// DI-wired instance, statically accessible so (static) Harmony patches can reach the object manager.
    /// Set on construction by the auto-activated handler registration.
    /// </summary>
    internal static LocationConversationTracker Instance { get; private set; }

    private readonly object stateLock = new object();
    private readonly Dictionary<string, object> engagerByNpcKey = new Dictionary<string, object>();
    private readonly Dictionary<object, Engagement> engagementByEngager = new Dictionary<object, Engagement>();
    private readonly Dictionary<string, Lease> leasesById = new Dictionary<string, Lease>();
    private readonly Dictionary<object, string> leaseByOwner = new Dictionary<object, string>();
    private readonly Dictionary<string, Lease> replicas = new Dictionary<string, Lease>();
    private string replicaSessionId;
    private long leaseRevision;

    private volatile bool isEmpty = true;

    /// <summary>Lock-free fast path: true when no engagements exist.</summary>
    public bool IsEmpty => isEmpty;

    /// <summary>
    /// Object manager shared with the acquire patch so it can resolve NPC/location ids without resolving
    /// the container on every interaction. The tracker itself never uses it.
    /// </summary>
    internal IObjectManager ObjectManager { get; }

    public LocationConversationTracker(IObjectManager objectManager)
    {
        ObjectManager = objectManager;
        Instance = this;
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            engagerByNpcKey.Clear();
            engagementByEngager.Clear();
            leasesById.Clear(); leaseByOwner.Clear(); replicas.Clear(); replicaSessionId = null;
            isEmpty = true;
        }

        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Composes the per-NPC lock key from the NPC's character id and its location id, so the same
    /// character template in two different locations is tracked separately.
    /// </summary>
    public static string ComposeKey(string locationId, string characterId) => $"{locationId}|{characterId}";

    internal readonly struct Lease
    {
        public Lease(string id, long revision, bool active, object owner, string sessionId, string locationId, string ownerCharacterId, string targetCharacterId)
        { Id=id; Revision=revision; Active=active; Owner=owner; SessionId=sessionId; LocationId=locationId; OwnerCharacterId=ownerCharacterId; TargetCharacterId=targetCharacterId; }
        public string Id { get; } public long Revision { get; } public bool Active { get; } public object Owner { get; }
        public string SessionId { get; } public string LocationId { get; } public string OwnerCharacterId { get; } public string TargetCharacterId { get; }
    }

    internal Lease BeginLease(object owner, string sessionId, string locationId, string ownerCharacterId, string targetCharacterId)
    {
        lock (stateLock)
        {
            var lease = new Lease(Guid.NewGuid().ToString("N"), ++leaseRevision, true, owner, sessionId, locationId, ownerCharacterId, targetCharacterId);
            leasesById[lease.Id] = lease; leaseByOwner[owner] = lease.Id; return lease;
        }
    }
    internal bool TryGetLease(string id, out Lease lease) { lock (stateLock) return leasesById.TryGetValue(id, out lease); }
    internal bool TryGetActiveLeaseByOwner(object owner, out Lease lease)
    { lock (stateLock) { lease=default; return owner != null && leaseByOwner.TryGetValue(owner, out var id) && leasesById.TryGetValue(id,out lease) && lease.Active; } }
    internal bool TryEndLease(object owner, string id, out Lease ended)
    {
        lock (stateLock)
        {
            ended=default; if (!leasesById.TryGetValue(id, out var lease) || !ReferenceEquals(lease.Owner, owner)) return false;
            if (!lease.Active) { ended=lease; return true; }
            ended = new Lease(lease.Id, ++leaseRevision, false, lease.Owner, lease.SessionId, lease.LocationId, lease.OwnerCharacterId, lease.TargetCharacterId);
            leasesById[id]=ended; leaseByOwner.Remove(owner); return true;
        }
    }
    internal void ResetReplicaSession(string sessionId) { lock(stateLock) { replicaSessionId=sessionId; replicas.Clear(); } }
    internal bool ApplyLeaseState(NetworkLocationConversationLeaseState state)
    {
        lock(stateLock)
        {
            if (replicaSessionId != state.SessionId) return false;
            if (replicas.TryGetValue(state.LeaseId,out var current) && current.Revision >= state.Revision)
                return current.Revision == state.Revision && current.Active == state.IsActive && current.LocationId == state.LocationId && current.OwnerCharacterId == state.OwnerCharacterId && current.TargetCharacterId == state.TargetCharacterId;
            replicas[state.LeaseId]=new Lease(state.LeaseId,state.Revision,state.IsActive,null,state.SessionId,state.LocationId,state.OwnerCharacterId,state.TargetCharacterId); return true;
        }
    }
    internal bool IsReplicaLease(string sessionId, string id, long revision, bool active, string locationId, string ownerCharacterId, string targetCharacterId)
    { lock(stateLock) return replicaSessionId==sessionId && replicas.TryGetValue(id,out var x) && x.Revision==revision && x.Active==active &&
        (locationId == null || x.LocationId==locationId) && (ownerCharacterId == null || x.OwnerCharacterId==ownerCharacterId) &&
        (targetCharacterId == null || x.TargetCharacterId==targetCharacterId); }

    /// <summary>
    /// Begins (or refreshes) <paramref name="engagerKey"/>'s engagement of the given NPC. Both the
    /// initiating player's character and the target NPC are reserved so neither participant can enter a
    /// second location conversation until this one ends.
    /// </summary>
    public bool TryBeginEngagement(object engagerKey, string engagerNpcKey, string targetNpcKey)
    {
        if (engagerKey == null || engagerNpcKey == null || targetNpcKey == null) return false;

        lock (stateLock)
        {
            if (engagementByEngager.TryGetValue(engagerKey, out var currentEngagement))
            {
                return currentEngagement.EngagerNpcKey == engagerNpcKey &&
                       currentEngagement.TargetNpcKey == targetNpcKey;
            }

            if (engagerByNpcKey.TryGetValue(engagerNpcKey, out var existingEngager) &&
                !Equals(existingEngager, engagerKey))
                return false;

            if (engagerByNpcKey.TryGetValue(targetNpcKey, out var existingTarget) &&
                !Equals(existingTarget, engagerKey))
                return false;

            engagerByNpcKey[engagerNpcKey] = engagerKey;
            engagerByNpcKey[targetNpcKey] = engagerKey;
            engagementByEngager[engagerKey] = new Engagement(engagerNpcKey, targetNpcKey);
            isEmpty = false;
            return true;
        }
    }

    /// <summary>Ends <paramref name="engagerKey"/>'s engagement, returning the NPC it held.</summary>
    public bool TryEndEngagement(object engagerKey, out string npcKey)
    {
        npcKey = null;

        if (engagerKey == null) return false;

        lock (stateLock)
        {
            if (!engagementByEngager.TryGetValue(engagerKey, out var engagement))
                return false;

            npcKey = engagement.TargetNpcKey;
            engagementByEngager.Remove(engagerKey);
            engagerByNpcKey.Remove(engagement.EngagerNpcKey);
            engagerByNpcKey.Remove(engagement.TargetNpcKey);

            isEmpty = engagementByEngager.Count == 0;
            return true;
        }
    }

    public bool TryGetEngagement(object engagerKey, out string npcKey)
    {
        npcKey = null;
        if (engagerKey == null) return false;

        lock (stateLock)
        {
            if (!engagementByEngager.TryGetValue(engagerKey, out var engagement))
                return false;

            npcKey = engagement.TargetNpcKey;
            return true;
        }
    }

    /// <summary>True when the NPC is engaged by a player other than <paramref name="engagerKey"/>.</summary>
    public bool IsEngagedByOther(string npcKey, object engagerKey)
    {
        if (npcKey == null) return false;

        lock (stateLock)
        {
            return engagerByNpcKey.TryGetValue(npcKey, out var engager) && !Equals(engager, engagerKey);
        }
    }
}
