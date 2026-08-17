using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.SaveSystem;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

internal static class RebellionsAndDemographicsHarmonyIsolation
{
    internal const string UpstreamOwner = "com.rebellions.and.demographics";
    internal const string AdapterOwner = "Bannerlord.Coop.RebellionsAndDemographics.1.4.8";

    private static readonly FieldInfo DefinitionContextField = typeof(SaveableTypeDefiner).GetField(
        "_definitionContext", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ConstructContainerDefinition = typeof(SaveableTypeDefiner).GetMethod(
        "ConstructContainerDefinition", BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null, types: new[] { typeof(Type) }, modifiers: null);
    private static readonly MethodInfo AddClassDefinition = typeof(SaveableTypeDefiner)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(method => method.Name == "AddClassDefinition" &&
            method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(Type));

    internal static void InstallSaveDefinitionCompatibility(Harmony adapter)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        if (DefinitionContextField == null || ConstructContainerDefinition == null || AddClassDefinition == null)
            throw new MissingMemberException("The v1.4.8 SaveableTypeDefiner contract is unavailable.");

        MethodInfo containerPrefix = AccessTools.Method(
            typeof(RebellionsAndDemographicsHarmonyIsolation), nameof(SkipDuplicateContainerDefinitionPrefix));
        MethodInfo classPrefix = AccessTools.Method(
            typeof(RebellionsAndDemographicsHarmonyIsolation), nameof(SkipDuplicateClassDefinitionPrefix));
        PatchOnce(adapter, ConstructContainerDefinition, containerPrefix);
        PatchOnce(adapter, AddClassDefinition, classPrefix);
    }

    private static void PatchOnce(Harmony adapter, MethodInfo original, MethodInfo prefix)
    {
        var patches = Harmony.GetPatchInfo(original)?.Prefixes;
        if (patches != null && patches.Any(patch => patch.owner == adapter.Id && patch.PatchMethod == prefix)) return;
        adapter.Patch(original, prefix: new HarmonyMethod(prefix));
    }

    // R&D v3.0.1 defines several native containers more than once and defines
    // PendingAllianceData from two definers. v1.4.8 turns the former into a fatal assert and the
    // latter into a duplicate dictionary insertion. Keep the first canonical definition exactly
    // as older versions did and make only subsequent registration attempts idempotent.
    private static bool SkipDuplicateContainerDefinitionPrefix(object __instance, Type __0) =>
        ShouldRunDefinitionOriginal(__instance, __0);

    private static bool SkipDuplicateClassDefinitionPrefix(object __instance, Type __0)
    {
        // The package duplicates its own PendingAllianceData class. Do not weaken collision
        // detection for any class owned by the game or a different module.
        if (!string.Equals(__0?.Assembly.GetName().Name,
                RebellionsAndDemographicsModule.AssemblyName, StringComparison.Ordinal)) return true;
        return ShouldRunDefinitionOriginal(__instance, __0);
    }

    private static bool ShouldRunDefinitionOriginal(object instance, Type type)
    {
        if (instance == null || type == null) return true;
        try
        {
            object context = DefinitionContextField.GetValue(instance);
            if (context == null) return true;
            MethodInfo hasDefinition = context.GetType().GetMethod(
                "HasDefinition", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Type) }, modifiers: null);
            if (hasDefinition == null) return true;
            return ShouldRunSaveDefinitionOriginal((bool)hasDefinition.Invoke(context, new object[] { type }));
        }
        catch
        {
            // Preserve the engine's fail-closed behavior if the pinned reflection contract changes.
            return true;
        }
    }

    internal static bool ShouldRunSaveDefinitionOriginal(bool alreadyDefined) => !alreadyDefined;

    internal static void Purge(Assembly assembly, Harmony adapter)
    {
        if (assembly == null || adapter == null) throw new ArgumentNullException(assembly == null ? nameof(assembly) : nameof(adapter));
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var patch in Enumerate(info).ToArray())
            {
                if (string.Equals(patch.owner, UpstreamOwner, StringComparison.Ordinal) ||
                    Equals(patch.PatchMethod?.DeclaringType?.Assembly, assembly))
                    adapter.Unpatch(original, patch.PatchMethod);
            }
        }

        var remaining = Harmony.GetAllPatchedMethods()
            .SelectMany(original =>
            {
                var info = Harmony.GetPatchInfo(original);
                return info == null ? Enumerable.Empty<Patch>() : Enumerate(info);
            })
            .Where(patch => string.Equals(patch.owner, UpstreamOwner, StringComparison.Ordinal) ||
                            Equals(patch.PatchMethod?.DeclaringType?.Assembly, assembly))
            .ToArray();
        if (remaining.Length != 0)
            throw new InvalidOperationException("R&D Harmony isolation failed: upstream patches remain after purge.");
    }

    private static IEnumerable<Patch> Enumerate(Patches patches)
    {
        if (patches.Prefixes != null) foreach (var patch in patches.Prefixes) yield return patch;
        if (patches.Postfixes != null) foreach (var patch in patches.Postfixes) yield return patch;
        if (patches.Transpilers != null) foreach (var patch in patches.Transpilers) yield return patch;
        if (patches.Finalizers != null) foreach (var patch in patches.Finalizers) yield return patch;
    }
}
