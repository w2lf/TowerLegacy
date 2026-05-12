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

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        PatchEarlyDbTrigger();
        PatchGameHubAvailability();

        DumpFractionLobbyAssetFields();
        DumpLocMethods();

        Log.LogInfo("TowerLegacy loaded.");
    }

    // ── Early DB injection: hook the method that fires when Config db is loaded ──
    // We scan the Hex assembly at runtime for the obfuscated method that logs
    // "Config db is loaded" so we can inject tower before GameHub runs its
    // availability check.
    static void PatchEarlyDbTrigger()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Log.LogWarning("[EarlyInject] Hex assembly not found."); return; }

            // Scan for any void/bool method whose IL contains the literal string
            // "Config db is loaded" — used as the trigger point.
            MethodBase target = null;
            foreach (var type in hexAsm.GetTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        var body = m.GetMethodBody();
                        if (body == null) continue;
                        // Check string tokens in the method body via IL inspection
                        // (cheap: look for the type's string fields referencing our marker)
                        // Fallback: patch cjv.bxjy setter — the property that exposes the DB repo.
                    }
                    catch { }
                }
            }

            // Reliable fallback: patch the static property setter on cjv that assigns bxjy.
            // This fires exactly once when the game finishes loading the fraction DB.
            var cjvType = hexAsm.GetType("cjv");
            if (cjvType == null) { Log.LogWarning("[EarlyInject] cjv type not found."); return; }

            var setter = cjvType.GetMethod("set_bxjy",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (setter == null)
            {
                // Try property setter via PropertyInfo
                var prop = cjvType.GetProperty("bxjy",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop != null) setter = prop.GetSetMethod(true);
            }

            if (setter != null)
            {
                Harmony.Patch(setter,
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(OnDbRepoSet)));
                Log.LogInfo("[EarlyInject] Patched cjv.set_bxjy — will inject tower when DB repo is assigned.");
            }
            else
            {
                Log.LogWarning("[EarlyInject] cjv.set_bxjy not found — falling back to ScLobby2.Init trigger.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[EarlyInject] {ex.Message}"); }
    }

    public static void OnDbRepoSet()
    {
        try
        {
            if (DbInjected) return;
            Log.LogInfo("[EarlyInject] cjv.bxjy assigned — triggering early DB injection.");
            TowerDbInjector.TryInject();
        }
        catch (Exception ex) { Log.LogError($"[EarlyInject] OnDbRepoSet failed: {ex}"); }
    }

    // ── GameHub availability patch ──────────────────────────────────────────────
    // The game checks a set/list of available fraction IDs before the lobby opens.
    // We scan for the method that logs "fraction {sid} not available" and postfix
    // it to return true when sid == "tower".
    static void PatchGameHubAvailability()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Log.LogWarning("[HubPatch] Hex assembly not found."); return; }

            int patched = 0;
            var postfix = new HarmonyMethod(typeof(Plugin), nameof(FractionAvailablePostfix));

            // Target: any method returning bool with a single string parameter whose
            // name suggests fraction availability (IsFractionAvailable, isFractionEnabled, etc.)
            // Since the assembly is obfuscated, we match on signature: bool(string) in GameHub types.
            foreach (var type in hexAsm.GetTypes())
            {
                // Only look in types whose namespace or name hints at GameHub/Lobby/Fraction.
                // Since names are obfuscated, we cast the net to ALL types but limit to
                // bool(string) methods to keep it tight.
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        if (m.ReturnType != typeof(bool)) continue;
                        var parms = m.GetParameters();
                        if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;
                        // Patch every bool(string) method — the postfix is a no-op unless
                        // __result is false and the key is "tower", so false positives are safe.
                        Harmony.Patch(m, postfix: postfix);
                        patched++;
                    }
                    catch { }
                }
            }

            Log.LogInfo($"[HubPatch] Patched {patched} bool(string) methods for tower availability.");
        }
        catch (Exception ex) { Log.LogWarning($"[HubPatch] {ex.Message}"); }
    }

    // Postfix for all bool(string) methods in Hex.
    // If the method returned false and the argument is "tower", flip it to true.
    // This is intentionally broad: the postfix is a guard-first no-op for the
    // overwhelming majority of calls that don't involve "tower".
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
        // Fallback: if early injection via cjv.set_bxjy didn't fire, try now.
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

            // Fresh-list path: assign a brand-new list via ref to avoid IL2Cpp
            // backing-array mutation errors.
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
