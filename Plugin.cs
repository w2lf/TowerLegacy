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

    // Pointers of FractionLobbyAsset instances we created, so get_sid can
    // return "tower" for them specifically.
    internal static readonly HashSet<IntPtr> TowerSlotPtrs = new HashSet<IntPtr>();

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();

        // Patch FractionLobbyAsset.get_sid so our injected slot reports "tower"
        PatchFlaGetSid();

        // Patch the localization system so "tower_name" etc. resolve correctly
        PatchLocalization();

        Log.LogInfo("TowerLegacy loaded.");
    }

    // ── FractionLobbyAsset.get_sid patch ─────────────────────────────────────
    static void PatchFlaGetSid()
    {
        try
        {
            var getSid = typeof(FractionLobbyAsset).GetProperty("sid",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
            if (getSid == null)
            {
                getSid = typeof(FractionLobbyAsset).GetMethod("get_sid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (getSid == null) { Log.LogWarning("[SidPatch] FractionLobbyAsset.get_sid not found."); return; }

            Harmony.Patch(getSid, postfix: new HarmonyMethod(typeof(Plugin), nameof(GetSidPostfix)));
            Log.LogInfo("[SidPatch] Patched FractionLobbyAsset.get_sid.");
        }
        catch (Exception ex) { Log.LogWarning($"[SidPatch] {ex.Message}"); }
    }

    public static void GetSidPostfix(object __instance, ref string __result)
    {
        try
        {
            if (__instance == null) return;
            var ptr = IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppSystem.Object)__instance);
            if (TowerSlotPtrs.Contains(ptr))
                __result = "tower";
        }
        catch { }
    }

    // ── Localization patch ────────────────────────────────────────────────────
    internal static readonly Dictionary<string, string> LocOverrides = new Dictionary<string, string>
    {
        { "tower_name",   "Tower City" },
        { "tower_desc",   "A city built around a great tower." },
        { "tower_select", "Select Tower City" },
    };

    // Known method names used by localization systems in obfuscated Unity IL2CPP games.
    // We ONLY patch methods whose name matches one of these — never blind-scan all (string)->string.
    static readonly HashSet<string> LocMethodNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "GetString", "GetText", "Translate", "Localize",
        "GetValue", "Lookup", "GetLocalized", "GetLocalizedString",
        "GetKey", "GetEntry",
    };

    static void PatchLocalization()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Log.LogWarning("[LocPatch] Hex assembly not found."); return; }

            int patched = 0;
            foreach (var type in hexAsm.GetTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        // Only target methods with a name that looks like a localization getter
                        if (!LocMethodNames.Contains(m.Name)) continue;
                        if (m.ReturnType != typeof(string)) continue;
                        var parms = m.GetParameters();
                        if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;

                        Harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(LocPrefix)));
                        Log.LogInfo($"[LocPatch] Patched {type.FullName}.{m.Name}");
                        patched++;
                    }
                    catch { }
                }
            }

            if (patched == 0)
            {
                Log.LogWarning("[LocPatch] No named loc methods found. Falling back to obfuscated scan (2-char names only).");
                // Fallback: only patch short obfuscated-looking names (2-3 chars) — far fewer than 640
                foreach (var type in hexAsm.GetTypes())
                {
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.Instance | BindingFlags.Static))
                    {
                        try
                        {
                            if (m.Name.Length > 4) continue; // obfuscated names are short
                            if (m.ReturnType != typeof(string)) return;
                            var parms = m.GetParameters();
                            if (parms.Length != 1 || parms[0].ParameterType != typeof(string)) continue;

                            Harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(LocPrefix)));
                            Log.LogInfo($"[LocPatch] Patched (fallback) {type.FullName}.{m.Name}");
                            patched++;
                        }
                        catch { }
                    }
                }
            }

            Log.LogInfo($"[LocPatch] Patched {patched} string->string method(s) in Hex.");
        }
        catch (Exception ex) { Log.LogWarning($"[LocPatch] {ex.Message}"); }
    }

    /// <summary>
    /// PREFIX — only fires when the key is one of our overrides.
    /// Returns false to skip the original entirely (no Il2Cpp marshal on return path).
    /// Returns true for everything else — original runs normally, zero overhead.
    /// </summary>
    public static bool LocPrefix(string __0, ref string __result)
    {
        try
        {
            if (__0 != null && LocOverrides.TryGetValue(__0, out var val))
            {
                Log.LogInfo($"[LocPatch] Intercepted key '{__0}' → '{val}'");
                __result = val;
                return false; // skip original — avoids marshal of native return ptr
            }
        }
        catch { }
        return true; // let original run normally
    }
}

// ── PATCHES ───────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(ScLobby2), nameof(ScLobby2.Init))]
public static class ScLobby2_Init_Patch
{
    public static void Postfix()
    {
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
        foreach (var f in typeof(FractionLobbyAsset).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            try { f.SetValue(slot, f.GetValue(src)); } catch { }
        foreach (var p in typeof(FractionLobbyAsset).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            try { if (p.CanRead && p.CanWrite) p.SetValue(slot, p.GetValue(src)); } catch { }

        var slotPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(slot);
        Plugin.TowerSlotPtrs.Add(slotPtr);
        Plugin.Log.LogInfo($"[TowerInject] Tower slot ptr registered: 0x{slotPtr:X}");

        return slot;
    }
}

[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwb))]
public static class ScFractionSelect_qwb_Patch
{
    static readonly HashSet<IntPtr> _injected = new HashSet<IntPtr>();

    public static void Postfix(ref Il2CppSystem.Collections.Generic.List<FractionLobbyAsset> __result)
    {
        try
        {
            if (__result == null || __result.Count == 0) return;
            var listPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(__result);
            if (_injected.Contains(listPtr)) return;
            _injected.Add(listPtr);

            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "human") { src = __result[i]; break; }
            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human not found (qwb)."); return; }

            __result.Add(ScFractionSelect_qwa_Patch.BuildSlot(src));
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot via qwb (sid=tower via ptr patch).");
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
