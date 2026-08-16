using GameInterface.Services;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Core;

public interface IWorkshopCapabilitySource
{
    IEnumerable<WorkshopCapability> CaptureCapabilities();
}

public enum WorkshopCapabilityApplyResult
{
    Applied,
    AlreadyCurrent,
    Malformed,
    Stale,
    Conflict,
}

public enum WorkshopCapabilityReadiness
{
    Unknown,
    Loading,
    Ready,
    Unavailable,
}

/// <summary>Readiness of one authoritative Workshop state bootstrap for the accepted session.</summary>
public enum WorkshopSnapshotReadiness
{
    Unknown,
    Loading,
    Ready,
    Unavailable,
}

public interface IWorkshopCapabilityRegistry : IGameAbstraction
{
    bool IsEnabled(string moduleId, string operation);
    WorkshopCapabilityReadiness Readiness { get; }
    bool IsReadyFor(string sessionId);
    WorkshopCapabilityApplyResult Apply(WorkshopCapabilitySnapshot snapshot);
    void MarkLoading(string sessionId);
    void MarkUnavailable();
    void Reset();
}

internal sealed class WorkshopCapabilityRegistry : IWorkshopCapabilityRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
    private WorkshopCapabilitySnapshot current;
    private WorkshopCapabilityReadiness readiness;

    public WorkshopCapabilityReadiness Readiness
    {
        get { lock (gate) return readiness; }
    }

    public bool IsEnabled(string moduleId, string operation)
    {
        string key = Key(moduleId, operation);
        lock (gate) return key != null && enabled.TryGetValue(key, out bool value) && value;
    }

    public bool IsReadyFor(string sessionId)
    {
        lock (gate) return readiness == WorkshopCapabilityReadiness.Ready && current != null &&
            string.Equals(current.SessionId, sessionId, StringComparison.Ordinal);
    }

    public WorkshopCapabilityApplyResult Apply(WorkshopCapabilitySnapshot snapshot)
    {
        if (!WorkshopCapabilityCodec.TryValidate(snapshot, out _))
            return WorkshopCapabilityApplyResult.Malformed;

        lock (gate)
        {
            if (current != null)
            {
                if (!string.Equals(current.SessionId, snapshot.SessionId, StringComparison.Ordinal))
                    return WorkshopCapabilityApplyResult.Conflict;
                if (snapshot.Revision < current.Revision)
                    return WorkshopCapabilityApplyResult.Stale;
                if (snapshot.Revision == current.Revision)
                {
                    return string.Equals(current.Sha256, snapshot.Sha256, StringComparison.Ordinal)
                        ? WorkshopCapabilityApplyResult.AlreadyCurrent
                        : WorkshopCapabilityApplyResult.Conflict;
                }
            }

            var candidate = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (WorkshopCapability capability in snapshot.Capabilities)
                candidate.Add(Key(capability.ModuleId, capability.Operation), capability.Enabled);

            enabled.Clear();
            foreach (KeyValuePair<string, bool> capability in candidate)
                enabled.Add(capability.Key, capability.Value);
            current = snapshot;
            readiness = WorkshopCapabilityReadiness.Ready;
            return WorkshopCapabilityApplyResult.Applied;
        }
    }

    public void MarkLoading(string sessionId)
    {
        lock (gate)
        {
            if (current != null && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
            {
                readiness = WorkshopCapabilityReadiness.Ready;
                return;
            }

            enabled.Clear();
            current = null;
            readiness = WorkshopCapabilityReadiness.Loading;
        }
    }

    public void MarkUnavailable()
    {
        lock (gate)
        {
            enabled.Clear();
            current = null;
            readiness = WorkshopCapabilityReadiness.Unavailable;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            enabled.Clear();
            current = null;
            readiness = WorkshopCapabilityReadiness.Unknown;
        }
    }

    private static string Key(string moduleId, string operation)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(operation)) return null;
        return moduleId + "\0" + operation;
    }
}
