using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal readonly struct DiplomacyValueBackupTarget
{
    internal Func<object> Read { get; }
    internal Action<object> Write { get; }

    internal DiplomacyValueBackupTarget(Func<object> read, Action<object> write)
    {
        Read = read ?? throw new ArgumentNullException(nameof(read));
        Write = write ?? throw new ArgumentNullException(nameof(write));
    }
}

/// <summary>
/// Rollback journal for the exact scalar settings, manager singletons and dictionaries touched
/// by snapshot application. Dictionary values are retained by reference: the adapter replaces or
/// clears containers but never mutates the saved record/list objects inside the prior containers.
/// </summary>
internal sealed class DiplomacyMutationTransaction
{
    private readonly List<(Func<object> Read, Action<object> Write, object Value)> values = new();
    private readonly List<(IDictionary Dictionary, List<DictionaryEntry> Entries)> dictionaries = new();

    internal DiplomacyMutationTransaction(
        IEnumerable<DiplomacyValueBackupTarget> valueTargets,
        IEnumerable<IDictionary> dictionaryTargets)
    {
        foreach (var target in valueTargets ?? Enumerable.Empty<DiplomacyValueBackupTarget>())
            values.Add((target.Read, target.Write, target.Read()));

        foreach (var dictionary in dictionaryTargets ?? Enumerable.Empty<IDictionary>())
        {
            if (dictionary == null || dictionaries.Any(item => ReferenceEquals(item.Dictionary, dictionary)))
                continue;

            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in dictionary) entries.Add(entry);
            dictionaries.Add((dictionary, entries));
        }
    }

    internal bool TryRestore(out string failure)
    {
        var failures = new List<string>();

        // Restore scalar settings and singleton references first. A setter may have side effects
        // on manager containers; restoring dictionaries afterward makes their contents the final
        // write rather than allowing a late setter to invalidate an already-verified restore.
        for (int index = values.Count - 1; index >= 0; index--)
        {
            try
            {
                values[index].Write(values[index].Value);
            }
            catch (Exception ex)
            {
                failures.Add($"value restore failed: {ex.Message}");
            }
        }

        foreach (var backup in dictionaries)
        {
            try
            {
                backup.Dictionary.Clear();
                foreach (var entry in backup.Entries)
                    backup.Dictionary[entry.Key] = entry.Value;
            }
            catch (Exception ex)
            {
                failures.Add($"dictionary restore failed: {ex.Message}");
            }
        }

        // Verify only after every restore write has completed. Immediate per-target verification
        // misses later cross-target side effects and can otherwise report a partial rollback as
        // successful.
        foreach (var backup in values)
        {
            try
            {
                if (!Equals(backup.Read(), backup.Value))
                    failures.Add("value restore verification failed");
            }
            catch (Exception ex)
            {
                failures.Add($"value restore verification failed: {ex.Message}");
            }
        }

        foreach (var backup in dictionaries)
        {
            try
            {
                if (backup.Dictionary.Count != backup.Entries.Count ||
                    backup.Entries.Any(entry =>
                        !backup.Dictionary.Contains(entry.Key) ||
                        !Equals(backup.Dictionary[entry.Key], entry.Value)))
                {
                    failures.Add("dictionary restore verification failed");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"dictionary restore verification failed: {ex.Message}");
            }
        }

        failure = failures.Count == 0 ? null : string.Join("; ", failures);
        return failures.Count == 0;
    }
}
