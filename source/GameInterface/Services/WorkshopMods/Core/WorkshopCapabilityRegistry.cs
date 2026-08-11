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

public interface IWorkshopCapabilityRegistry : IGameAbstraction
{
    bool IsEnabled(string moduleId, string operation);
    WorkshopCapabilityApplyResult Apply(WorkshopCapabilitySnapshot snapshot);
    void Reset();
}

internal sealed class WorkshopCapabilityRegistry : IWorkshopCapabilityRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
    private WorkshopCapabilitySnapshot current;

    public bool IsEnabled(string moduleId, string operation)
    {
        string key = Key(moduleId, operation);
        lock (gate) return key != null && enabled.TryGetValue(key, out bool value) && value;
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
            return WorkshopCapabilityApplyResult.Applied;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            enabled.Clear();
            current = null;
        }
    }

    private static string Key(string moduleId, string operation)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(operation)) return null;
        return moduleId + "\0" + operation;
    }
}
