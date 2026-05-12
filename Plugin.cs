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

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        Log.LogInfo("TowerLegacy loaded.");
    }
}

// ── FACTION DEFINITION ────────────────────────────────────────────────────────
internal static class TowerFaction
{
    public const string Id           = "tower";
    public const string DisplayName  = "Tower City";
    public const string Desc         = "Masters of stone and sorcery, the Tower City mages command arcane constructs and disciplined soldiers.";
    public const string NarrativeDesc= "From their obsidian spires, the Tower City sages watch the horizon for enemies who dare challenge their dominion.";
    public const string IconKey      = "fraction_human";
    public const string Biome        = "Snow";
    public const string CityKey      = "human_city";
    public const string ResourceName = "crystals";
    public static readonly string[] Heroes    = { };
    public static readonly string[] CityNames =
    {
        "Arcantum", "Spire's Edge", "Vorath Keep", "Crystalhold",
        "Ebonveil", "The Bastion", "Coldforge", "Mirethian",
    };

    // Localization keys the game will request for this faction.
    // We intercept them in LocalizationPatch and return the plain strings above.
    public const string KeyName          = "tower_name";
    public const string KeyDesc          = "tower_desc";
    public const string KeyNarrativeDesc = "tower_narrative_desc";
    public const string KeyCity          = "tower_city";

    public static bool TryGetLocalized(string key, out string value)
    {
        switch (key)
        {
            case KeyName:          value = DisplayName;   return true;
            case KeyDesc:          value = Desc;          return true;
            case KeyNarrativeDesc: value = NarrativeDesc; return true;
            case KeyCity:          value = "Tower City";  return true;
            default:               value = null;          return false;
        }
    }
}

// ── LOCALIZATION INTERCEPT ─────────────────────────────────────────────────────
// Patch every possible localization method signature the game might call.
// The game resolves "{sid}_name" etc. through its loc system.
// We intercept before the return and substitute our strings.

[HarmonyPatch]
public static class LocalizationPatch
{
    // Find all methods named "Get", "GetString", "Translate" etc. across all types
    // in the Hex assembly and patch the ones that take a single string key.
    static IEnumerable<MethodBase> TargetMethods()
    {
        var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Hex");
        if (hexAsm == null) yield break;

        var candidates = new[] { "Get", "GetString", "Translate", "GetText", "Localize" };
        foreach (var type in hexAsm.GetTypes())
        {
            foreach (var name in candidates)
            {
                var m = type.GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);
                if (m != null && m.ReturnType == typeof(string))
                    yield return m;
            }
        }
    }

    static void Postfix(string __0, ref string __result)
    {
        try
        {
            if (__0 != null && TowerFaction.TryGetLocalized(__0, out var v))
                __result = v;
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

            // Also inject into dict_ so lookups by sid="tower" succeed.
            var dictFieldPtr = IL2CPP.GetIl2CppField(soClassPtr, "dict_");
            var dictObjPtr   = IL2CPP.il2cpp_field_get_value_object(dictFieldPtr, objPtr);
            if (dictObjPtr != IntPtr.Zero)
            {
                var dict = new Il2CppSystem.Collections.Generic.Dictionary<string, FractionLobbyAsset>(dictObjPtr);
                if (!dict.ContainsKey(TowerFaction.Id))
                {
                    dict.Add(TowerFaction.Id, slot);
                    Plugin.Log.LogInfo($"[TowerInject] Added tower to dict_ (now {dict.Count} entries).");
                }
                else
                    Plugin.Log.LogInfo($"[TowerInject] dict_ already has tower ({dict.Count} entries).");
            }
            else Plugin.Log.LogWarning("[TowerInject] dict_ ptr is zero.");

            _injectedQwa = true;
            Plugin.Log.LogInfo($"[TowerInject] Injected tower slot (sid={TowerFaction.Id}) into SoFractions.");
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
        // Give it the tower sid so the UI resolves "tower_name" from the loc system.
        slot.sid = TowerFaction.Id;
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
            Plugin.Log.LogInfo($"[TowerInject] Injected UI slot via qwb (sid={TowerFaction.Id}).");
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

            if (FindByIdInDict(dictObj, TowerFaction.Id) != null || FindByIdInList(listObj, TowerFaction.Id) != null)
            { Plugin.DbInjected = true; Plugin.Log.LogInfo("[TowerInject] tower already in DB."); return; }

            var tower = new FractionConfig(IL2CPP.il2cpp_object_new(
                Il2CppClassPointerStore<FractionConfig>.NativeClassPtr));
            foreach (var f in typeof(FractionConfig).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                try { f.SetValue(tower, f.GetValue(humanCfg)); } catch { }
            foreach (var p in typeof(FractionConfig).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                try { if (p.CanRead && p.CanWrite) p.SetValue(tower, p.GetValue(humanCfg)); } catch { }

            // ── Overrides ─────────────────────────────────────────────
            tower.id           = TowerFaction.Id;
            // Use loc keys so the localization system (and our patch) resolves them.
            tower.name          = TowerFaction.KeyName;
            tower.desc          = TowerFaction.KeyDesc;
            tower.narrativeDesc = TowerFaction.KeyNarrativeDesc;
            tower.icon          = TowerFaction.IconKey;
            tower.biome         = TowerFaction.Biome;
            tower.city          = TowerFaction.CityKey;
            tower.resourceName  = TowerFaction.ResourceName;

            var cities = new Il2CppCollections.List<string>();
            foreach (var cn in TowerFaction.CityNames) cities.Add(cn);
            tower.cityNames = cities;

            if (TowerFaction.Heroes.Length > 0)
            {
                var heroes = new Il2CppCollections.List<string>();
                foreach (var h in TowerFaction.Heroes) heroes.Add(h);
                tower.heroes = heroes;
            }

            Plugin.Log.LogInfo($"[TowerInject] tower cloned, id={tower.id}");

            var typedList = listObj as Il2CppCollections.List<FractionConfig>
                            ?? new Il2CppCollections.List<FractionConfig>(((Il2CppSystem.Object)listObj).Pointer);
            var typedDict = dictObj as Il2CppCollections.Dictionary<string, FractionConfig>
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(((Il2CppSystem.Object)dictObj).Pointer);

            typedList.Add(tower);
            typedDict.Add(TowerFaction.Id, tower);

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
