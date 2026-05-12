using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Hex.Configs;
using Hex.GameHub.Lobby.UI;
using Hex.GameHub.UICommon;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.SceneManagement;
using Il2CppCollections = Il2CppSystem.Collections.Generic;

namespace System.Runtime.CompilerServices
{
    internal sealed class NullableAttribute : Attribute { public NullableAttribute(byte _) { } public NullableAttribute(byte[] _) { } }
    internal sealed class NullableContextAttribute : Attribute { public NullableContextAttribute(byte _) { } }
}

namespace TowerLegacy
{

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    internal static Harmony Harmony;
    internal static bool DbInjected;

    internal static readonly HashSet<IntPtr> TowerSlotPtrs = new HashSet<IntPtr>();

    public static readonly HashSet<string> SkipFields = new HashSet<string>(StringComparer.Ordinal)
        { "pooledPtr", "isWrapped" };

    // Methods known to crash when patched via HarmonyX on IL2CPP trampolines.
    // Add any obfuscated name that shows up in a DMD crash to this list.
    static readonly HashSet<string> SkipMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "IsInvoking", "CompareTag",
        // gla crashes with NullReferenceException inside its IL2CPP-to-managed
        // trampoline (DMDdaglada) — the object is not initialised when it fires.
        "gla"
    };

    // Type name substrings that are strong signals for the fraction-availability
    // check. We patch ONLY types whose name contains one of these, which reduces
    // the patch surface from 50 random methods down to a handful.
    static readonly string[] HubTypeHints =
    {
        "GameHub", "GameMode", "Lobby", "Fraction", "Hub", "Matchmak"
    };

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        PatchEarlyDbTrigger();
        PatchGameHubAvailability();

        DumpFractionLobbyAssetFields();
        DumpLocMethods();
        DumpHubAvailabilityMethods(); // probe: logs candidates so we can identify the exact type next run

        Log.LogInfo("TowerLegacy loaded.");
    }

    // ── Early DB injection ──────────────────────────────────────────────────────
    static void PatchEarlyDbTrigger()
    {
        try
        {
            SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;
            Log.LogInfo("[EarlyInject] Registered SceneManager.sceneLoaded — will inject tower on scene load.");
        }
        catch (Exception ex) { Log.LogWarning($"[EarlyInject] {ex.Message}"); }
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            if (DbInjected) return;
            Log.LogInfo($"[EarlyInject] Scene '{scene.name}' loaded — attempting DB injection.");
            TowerDbInjector.TryInject();
        }
        catch (Exception ex) { Log.LogError($"[EarlyInject] OnSceneLoaded failed: {ex}"); }
    }

    // ── GameHub availability patch ──────────────────────────────────────────────
    // Strategy: patch ONLY bool(string) methods on types whose name hints at
    // GameHub / lobby / fraction logic. This avoids patching deep-engine methods
    // like gla (on some unrelated type) whose IL2CPP trampolines crash on invoke.
    static void PatchGameHubAvailability()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Log.LogWarning("[HubPatch] Hex assembly not found."); return; }

            int patched = 0;
            var postfix = new HarmonyMethod(typeof(Plugin), nameof(FractionAvailablePostfix));

            foreach (var type in hexAsm.GetTypes())
            {
                var ns = type.Namespace ?? "";
                // Skip Unity namespaces entirely.
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    ns.StartsWith("Unity.",       StringComparison.Ordinal) ||
                    ns.StartsWith("TMPro",        StringComparison.Ordinal) ||
                    ns.StartsWith("UnityExplorer", StringComparison.Ordinal))
                    continue;

                // Only consider types that look like hub/lobby/fraction logic.
                var typeName = type.Name ?? "";
                bool isHubType = HubTypeHints.Any(h =>
                    typeName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isHubType) continue;

                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        if (m.ReturnType != typeof(bool)) continue;
                        if (SkipMethodNames.Contains(m.Name)) continue;

                        var declNs = m.DeclaringType?.Namespace ?? "";
                        if (declNs.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                            declNs.StartsWith("Unity.",      StringComparison.Ordinal))
                            continue;

                        var parms = m.GetParameters();
                        if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;

                        Harmony.Patch(m, postfix: postfix);
                        patched++;
                        Log.LogInfo($"[HubPatch] Patched {type.Name}.{m.Name}");
                    }
                    catch { }
                }
            }

            Log.LogInfo($"[HubPatch] Patched {patched} hub/lobby bool(string) methods.");
        }
        catch (Exception ex) { Log.LogWarning($"[HubPatch] {ex.Message}"); }
    }

    public static void FractionAvailablePostfix(string __0, ref bool __result)
    {
        try
        {
            if (!__result && __0 == "tower")
            {
                __result = true;
                Plugin.Log.LogInfo("[HubPatch] Overrode availability check: tower -> true");
            }
        }
        catch { }
    }

    // ── Probe: dump all bool(string) candidates so we can identify the exact one ─
    static void DumpHubAvailabilityMethods()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) return;

            Log.LogInfo("[HubDump] All Hex bool(string) methods:");
            foreach (var type in hexAsm.GetTypes())
            {
                var ns = type.Namespace ?? "";
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    ns.StartsWith("Unity.",       StringComparison.Ordinal)) continue;

                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        if (m.ReturnType != typeof(bool)) continue;
                        var parms = m.GetParameters();
                        if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;
                        Log.LogInfo($"[HubDump] {type.Name}.{m.Name}  static={m.IsStatic}  decl={m.DeclaringType?.Name}");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Log.LogWarning($"[HubDump] {ex.Message}"); }
    }

    static void DumpFractionLobbyAssetFields()
    {
        try
        {
            Log.LogInfo("[Dump] FractionLobbyAsset fields:");
            foreach (var f in typeof(FractionLobbyAsset).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Log.LogInfo($"[Dump]   field  {f.FieldType.Name} {f.Name}");
            Log.LogInfo("[Dump] FractionLobbyAsset properties:");
            foreach (var p in typeof(FractionLobbyAsset).GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Log.LogInfo($"[Dump]   prop   {p.PropertyType.Name} {p.Name}  get={p.CanRead} set={p.CanWrite}");
        }
        catch (Exception ex) { Log.LogWarning($"[Dump] {ex.Message}"); }
    }

    static void DumpLocMethods()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Log.LogWarning("[LocDump] Hex assembly not found."); return; }

            int count = 0;
            foreach (var type in hexAsm.GetTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        if (m.IsAbstract) continue;
                        if (m.ReturnType != typeof(string)) continue;
                        var parms = m.GetParameters();
                        if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;
                        if (m.Name.StartsWith("set_") || m.Name.StartsWith("get_")) continue;
                        Log.LogInfo($"[LocDump] {type.Name}.{m.Name}(string) : string  static={m.IsStatic}");
                        count++;
                    }
                    catch { }
                }
            }
            Log.LogInfo($"[LocDump] Total (string)->string methods: {count}");
        }
        catch (Exception ex) { Log.LogWarning($"[LocDump] {ex.Message}"); }
    }

    internal static readonly Dictionary<string, string> LocOverrides = new Dictionary<string, string>
    {
        { "tower_name",   "Tower City" },
        { "tower_desc",   "A city built around a great tower." },
        { "tower_select", "Select Tower City" },
    };

    static readonly string[] LocTypeNames = {
        "io", "lh", "zr", "bbj", "bbp", "bbw", "ccu", "cjt", "enz",
        "dk", "qc", "di"
    };

    public static void LocPostfix(string __0, ref string __result)
    {
        try
        {
            if (__0 != null && LocOverrides.TryGetValue(__0, out var val))
            {
                Log.LogInfo($"[LocPatch] Override '{__0}' -> '{val}'");
                __result = val;
            }
        }
        catch { }
    }
}

// ── PATCHES ───────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(ScLobby2), nameof(ScLobby2.Init))]
public static class ScLobby2_Init_Patch
{
    public static void Postfix()
    {
        ScFractionSelect_qwb_Patch._lobbyInjected = false;
        if (!Plugin.DbInjected)
            TowerDbInjector.TryInject();
    }
}

[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwa))]
public static class ScFractionSelect_qwa_Patch
{
    internal static bool _injectedQwa = false;

    public static void Prefix(ScFractionSelect __instance)
    {
        try
        {
            var assets = __instance?.fractionsAssets;
            if (assets == null) { Plugin.Log.LogWarning("[TowerInject] fractionsAssets null."); return; }

            var soClassPtr  = Il2CppClassPointerStore<SoFractions>.NativeClassPtr;
            var objPtr      = IL2CPP.Il2CppObjectBaseToPtrNotNull(assets);
            var arrFieldPtr = IL2CPP.GetIl2CppField(soClassPtr, "fractions");
            var arrObjPtr   = IL2CPP.il2cpp_field_get_value_object(arrFieldPtr, objPtr);
            if (arrObjPtr == IntPtr.Zero) { Plugin.Log.LogWarning("[TowerInject] fractions ptr zero."); return; }

            var arr = new Il2CppReferenceArray<FractionLobbyAsset>(arrObjPtr);
            if (_injectedQwa) return;

            FractionLobbyAsset src = null;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i]?.sid == "human") { src = arr[i]; break; }
            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human slot not found."); return; }

            var slot   = BuildSlot(src);
            var newArr = new Il2CppReferenceArray<FractionLobbyAsset>(arr.Length + 1);
            for (int i = 0; i < arr.Length; i++) newArr[i] = arr[i];
            newArr[arr.Length] = slot;

            IL2CPP.il2cpp_gc_wbarrier_set_field(
                objPtr,
                objPtr + (int)IL2CPP.il2cpp_field_get_offset(arrFieldPtr),
                IL2CPP.Il2CppObjectBaseToPtrNotNull(newArr));
            Plugin.Log.LogInfo("[TowerInject] fractions array updated.");

            _injectedQwa = true;
            Plugin.Log.LogInfo($"[TowerInject] Injected tower slot (sid=tower) into SoFractions.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwa prefix failed: {ex}"); }
    }

    internal static FractionLobbyAsset BuildSlot(FractionLobbyAsset src)
    {
        var slot = new FractionLobbyAsset();

        foreach (var f in typeof(FractionLobbyAsset).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (Plugin.SkipFields.Contains(f.Name)) continue;
            try { f.SetValue(slot, f.GetValue(src)); } catch { }
        }

        foreach (var p in typeof(FractionLobbyAsset).GetProperties(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (Plugin.SkipFields.Contains(p.Name)) continue;
            if (p.Name is "ObjectClass" or "Pointer" or "WasCollected") continue;
            try { if (p.CanRead && p.CanWrite) p.SetValue(slot, p.GetValue(src)); } catch { }
        }

        bool sidSet = false;
        foreach (var f in typeof(FractionLobbyAsset).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (f.FieldType != typeof(string)) continue;
            try
            {
                var val = f.GetValue(slot) as string;
                if (val == "human")
                {
                    f.SetValue(slot, "tower");
                    Plugin.Log.LogInfo($"[TowerInject] Set field '{f.Name}' (was 'human') -> 'tower'");
                    sidSet = true;
                }
            }
            catch { }
        }

        if (!sidSet)
        {
            try { slot.sid = "tower"; sidSet = true; Plugin.Log.LogInfo("[TowerInject] Set sid property -> 'tower'"); }
            catch { }
        }

        if (!sidSet)
        {
            var slotPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(slot);
            Plugin.TowerSlotPtrs.Add(slotPtr);
            Plugin.Log.LogInfo($"[TowerInject] No 'human' string field found; ptr registered 0x{slotPtr:X}");
        }

        return slot;
    }
}

[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwb))]
public static class ScFractionSelect_qwb_Patch
{
    internal static bool _lobbyInjected = false;

    public static void Postfix(ref Il2CppSystem.Collections.Generic.List<FractionLobbyAsset> __result)
    {
        if (__result == null || __result.Count == 0)
        {
            Plugin.Log.LogWarning("[TowerInject] qwb: result null or empty — skipping premature call.");
            return;
        }

        if (!Plugin.DbInjected) return;

        if (_lobbyInjected)
        {
            bool found = false;
            try { for (int i = 0; i < __result.Count; i++) if (__result[i]?.sid == "tower") { found = true; break; } }
            catch { }
            if (found) { Plugin.Log.LogInfo("[TowerInject] qwb: already injected, skipping."); return; }
            Plugin.Log.LogInfo("[TowerInject] qwb: flag set but slot missing — re-injecting.");
            _lobbyInjected = false;
        }

        try
        {
            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "human") { src = __result[i]; break; }
            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human not found (qwb)."); return; }

            var newSlot = ScFractionSelect_qwa_Patch.BuildSlot(src);

            var freshList = new Il2CppSystem.Collections.Generic.List<FractionLobbyAsset>();
            for (int i = 0; i < __result.Count; i++)
                freshList.Add(__result[i]);
            freshList.Add(newSlot);
            __result = freshList;

            _lobbyInjected = true;
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot via qwb (fresh-list path).");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwb failed: {ex}"); }
    }
}

// ── DB INJECTOR ───────────────────────────────────────────────────────────────
internal static class TowerDbInjector
{
    public static void TryInject()
    {
        try
        {
            Plugin.Log.LogInfo("[TowerInject] DB injection starting...");

            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Plugin.Log.LogWarning("[TowerInject] Hex assembly not found."); return; }

            var cjvType = hexAsm.GetType("cjv");
            if (cjvType == null) { Plugin.Log.LogWarning("[TowerInject] cjv type not found."); return; }

            var repo      = GetStaticProperty(cjvType, "bxjy");
            var container = GetMember(repo, "bxni");
            var listObj   = GetMember(container, "bxjw");
            var dictObj   = GetMember(container, "bxjx");

            if (listObj == null || dictObj == null)
            { Plugin.Log.LogWarning("[TowerInject] bxjw or bxjx missing."); return; }

            var humanCfg = FindByIdInList(listObj, "human");
            if (humanCfg == null) { Plugin.Log.LogWarning("[TowerInject] human config not found."); return; }
            Plugin.Log.LogInfo($"[TowerInject] human ok, id={humanCfg.id}");

            if (FindByIdInDict(dictObj, "tower") != null || FindByIdInList(listObj, "tower") != null)
            { Plugin.DbInjected = true; Plugin.Log.LogInfo("[TowerInject] tower already in DB."); return; }

            var tower = new FractionConfig(IL2CPP.il2cpp_object_new(
                Il2CppClassPointerStore<FractionConfig>.NativeClassPtr));
            foreach (var f in typeof(FractionConfig).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                try { f.SetValue(tower, f.GetValue(humanCfg)); } catch { }
            foreach (var p in typeof(FractionConfig).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                try { if (p.CanRead && p.CanWrite) p.SetValue(tower, p.GetValue(humanCfg)); } catch { }

            tower.id           = "tower";
            tower.icon         = "fraction_human";
            tower.biome        = "Snow";
            tower.resourceName = "crystals";

            var cities = new Il2CppCollections.List<string>();
            if (humanCfg.cityNames != null)
                for (int i = 0; i < humanCfg.cityNames.Count; i++)
                    cities.Add(humanCfg.cityNames[i]);
            if (cities.Count == 0) cities.Add("Tower City");
            tower.cityNames = cities;

            var heroes = new Il2CppCollections.List<string>();
            if (humanCfg.heroes != null)
                for (int i = 0; i < humanCfg.heroes.Count; i++)
                    heroes.Add(humanCfg.heroes[i]);
            tower.heroes = heroes;

            Plugin.Log.LogInfo($"[TowerInject] tower cloned, id={tower.id}");

            var typedList = listObj as Il2CppCollections.List<FractionConfig>
                            ?? new Il2CppCollections.List<FractionConfig>(((Il2CppSystem.Object)listObj).Pointer);
            var typedDict = dictObj as Il2CppCollections.Dictionary<string, FractionConfig>
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(((Il2CppSystem.Object)dictObj).Pointer);

            typedList.Add(tower);
            typedDict.Add("tower", tower);

            Plugin.DbInjected = true;
            Plugin.Log.LogInfo("[TowerInject] tower injected into DB.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] Injection failed: {ex}"); }
    }

    public static object GetMember(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanRead) return p.GetValue(obj);
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) return f.GetValue(obj);
        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (m != null) return m.Invoke(obj, null);
        return null;
    }

    static object GetStaticProperty(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null && p.CanRead) return p.GetValue(null);
        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m != null) return m.Invoke(null, null);
        return null;
    }

    static FractionConfig FindByIdInList(object listObj, string wantedId)
    {
        try
        {
            var typedList = listObj as Il2CppCollections.List<FractionConfig>
                            ?? new Il2CppCollections.List<FractionConfig>(((Il2CppSystem.Object)listObj).Pointer);
            for (int i = 0; i < typedList.Count; i++)
            {
                var item = typedList[i];
                if (item != null && item.id == wantedId) return item;
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TowerInject] FindByIdInList: {ex.Message}"); }
        return null;
    }

    static FractionConfig FindByIdInDict(object dictObj, string wantedId)
    {
        try
        {
            var typedDict = dictObj as Il2CppCollections.Dictionary<string, FractionConfig>
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(((Il2CppSystem.Object)dictObj).Pointer);
            if (typedDict.ContainsKey(wantedId)) return typedDict[wantedId];
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TowerInject] FindByIdInDict: {ex.Message}"); }
        return null;
    }
}

} // namespace TowerLegacy
