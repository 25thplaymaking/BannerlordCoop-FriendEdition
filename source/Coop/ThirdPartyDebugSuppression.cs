using Common.Logging;
using Serilog;
using System;
using System.Linq;
using System.Reflection;

namespace Coop
{
    /// <summary>
    /// Turns off on-screen debug output that bundled third-party modules leave switched on.
    /// </summary>
    /// <remarks>
    /// These are not co-op faults and nothing here changes what a module DOES. Each entry flips a
    /// switch the module itself provides, by reflection and by name, so a loadout without that module
    /// is simply a no-op rather than a missing-assembly reference.
    /// </remarks>
    internal static class ThirdPartyDebugSuppression
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(ThirdPartyDebugSuppression));

        internal static void Apply()
        {
            SuppressRealmsForgottenBattleAi();
        }

        /// <summary>
        /// Silences Realms Forgotten's battle-AI commentary ("RF Battle AI: team Enemy-Defender -> ...").
        /// </summary>
        /// <remarks>
        /// Empires of Europe 1100 ships <c>RF_BattleAI.dll</c> inside <c>Modules/Europe1100/bin/</c>, and
        /// its <c>BattleAIDebug</c> static constructor sets <c>Enabled = true</c>, so every tactic change
        /// prints an <c>InformationMessage</c> over the battle HUD.
        /// <para>
        /// <c>BattleAIDebug.Show</c> already returns early when <c>Enabled</c> is false, and it is the only
        /// gate on the display call — so clearing the flag suppresses the whole class of message using the
        /// mod's own supported switch. The tactic selection itself, and its runtime tracer, are untouched:
        /// <c>NoteTacticState</c>/<c>NoteEvent</c> run before <c>Show</c> and still do.
        /// </para>
        /// <para>
        /// Europe1100 is deliberately uncatalogued, so no Workshop receipt hash covers it and nothing here
        /// can affect the join handshake.
        /// </para>
        /// </remarks>
        private static void SuppressRealmsForgottenBattleAi()
        {
            try
            {
                Type debugType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Where(assembly => string.Equals(
                        assembly.GetName().Name, "RF_BattleAI", StringComparison.OrdinalIgnoreCase))
                    .Select(assembly => assembly.GetType("RF_BattleAI.BattleAIDebug", throwOnError: false))
                    .FirstOrDefault(type => type != null);

                // Not an error: the module is absent from most loadouts, and from the dedicated host.
                if (debugType == null) return;

                PropertyInfo enabled = debugType.GetProperty(
                    "Enabled", BindingFlags.Public | BindingFlags.Static);
                if (enabled == null || !enabled.CanWrite)
                {
                    Logger.Debug("RF_BattleAI is loaded but exposes no writable Enabled switch; leaving it alone.");
                    return;
                }

                enabled.SetValue(null, false);
                Logger.Information("Silenced RF_BattleAI on-screen debug output; its battle AI is unchanged.");
            }
            catch (Exception error)
            {
                // Cosmetic by definition. Never let it affect startup.
                Logger.Warning(error, "Could not silence RF_BattleAI debug output; continuing.");
            }
        }
    }
}
