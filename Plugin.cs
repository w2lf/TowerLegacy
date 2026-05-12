using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Hex.Configs;
using Hex.GameHub.Lobby.UI;
using Hex.GameHub.UICommon;
using Il2CppInterop.Runtime;
using Il2CppCollections = Il2CppSystem.Collections.Generic;

namespace TowerLegacy;

// ── JSON POCO ────────────────────────────────────────────────────────────────
public class TowerFractionJson
{
    public List<TowerFractionData> array { get; set; }
}

public class TowerFractionData
{
    public string id            { get; set; }
    public string name          { get; set; }
    public string desc          { get; set; }
    public string narrativeDesc { get; set; }
}

// ── PLUGIN ───────────────────────────────────────────────────────────────────
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    internal static Harmony Harmony;
    internal static bool DbInjected;

    internal const string TowerDisplayName = "Tower";

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        Log.LogInfo("TowerLegacy loaded.");
    }
}

// ── PATCHES ──────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(ScLobby2), nameof(ScLobby2.Init))]
public static class ScLobby2_Init_Patch
{
    public static void Postfix()
    {
        if (!Plugin.DbInjected)
            TowerDbInjector.TryInject();
    }
}

// Patch qwa (PREFIX) so our slot is in the list BEFORE qwa builds UI components.
// qwb just returns the list; qwa iterates it and spawns the actual slot GameObjects.
[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwa))]
public static class ScFractionSelect_qwa_Patch
{
    public static void Prefix(ScFractionSelect __instance)
    {
        try
        {
            // fractionsAssets is a SoFractions ScriptableObject that holds the
            // authoritative list of FractionLobbyAsset. We inject into that list
            // before qwa reads it, so the spawning loop includes our slot.
            var assets = __instance?.fractionsAssets;
            if (assets == null) { Plugin.Log.LogWarning("[TowerInject] fractionsAssets is null in qwa prefix."); return; }

            // Get the inner list from SoFractions via reflection (obfuscated field name).
            var listObj = TowerDbInjector.GetMemberPublic(assets, "fractions")
                       ?? TowerDbInjector.GetFirstListField<FractionLobbyAsset>(assets);

            if (listObj == null) { Plugin.Log.LogWarning("[TowerInject] Could not find fractions list on SoFractions."); return; }

            var list = listObj as Il2CppCollections.List<FractionLobbyAsset>
                       ?? new Il2CppCollections.List<FractionLobbyAsset>(
                           ((Il2CppSystem.Object)listObj).Pointer);

            // Already injected?
            for (int i = 0; i < list.Count; i++)
                if (list[i]?.sid == "tower") return;

            // Find human slot as sprite source.
            FractionLobbyAsset src = null;
            for (int i = 0; i < list.Count; i++)
                if (list[i]?.sid == "human") { src = list[i]; break; }

            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human UI slot not found in SoFractions."); return; }

            var slot = new FractionLobbyAsset
            {
                sid          = "tower",
                icon         = src.icon,
                slotFon      = src.slotFon,
                statisticFon = src.statisticFon,
                versusFon    = src.versusFon,
                bigIcon      = src.bigIcon,
                card         = src.card
            };

            list.Add(slot);
            Plugin.Log.LogInfo("[TowerInject] Injected tower slot into SoFractions before qwa.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwa prefix failed: {ex}"); }
    }
}

// Keep qwb patch as fallback in case fractionsAssets path fails.
[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwb))]
public static class ScFractionSelect_qwb_Patch
{
    public static void Postfix(ref Il2CppCollections.List<FractionLobbyAsset> __result)
    {
        try
        {
            if (__result == null || __result.Count == 0) return;

            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "tower") return;

            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "human") { src = __result[i]; break; }

            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human UI slot not found (qwb fallback)."); return; }

            var slot = new FractionLobbyAsset
            {
                sid          = "tower",
                icon         = src.icon,
                slotFon      = src.slotFon,
                statisticFon = src.statisticFon,
                versusFon    = src.versusFon,
                bigIcon      = src.bigIcon,
                card         = src.card
            };

            __result.Add(slot);
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot via qwb fallback.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwb fallback failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(FractionConfig), "get_Name")]
public static class FractionConfig_GetName_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (__instance?.id == "tower") __result = Plugin.TowerDisplayName; }
        catch { }
    }
}

[HarmonyPatch(typeof(FractionConfig), "get_name")]
public static class FractionConfig_get_name_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (__instance?.id == "tower") __result = Plugin.TowerDisplayName; }
        catch { }
    }
}

// ── DB INJECTOR ───────────────────────────────────────────────────────────────
internal static class TowerDbInjector
{
    static string JsonPath =>
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "DB", "fractions", "tower.json");

    public static void TryInject()
    {
        try
        {
            Plugin.Log.LogInfo("[TowerInject] Direct DB injection starting...");

            if (!File.Exists(JsonPath))
            {
                Plugin.Log.LogWarning($"[TowerInject] JSON not found: {JsonPath}");
                return;
            }

            var jsonText = File.ReadAllText(JsonPath);
            var options  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var jsonData = JsonSerializer.Deserialize<TowerFractionJson>(jsonText, options);
            var data     = jsonData?.array?.FirstOrDefault(x => x.id == "tower");
            if (data == null) { Plugin.Log.LogWarning("[TowerInject] tower entry missing from JSON."); return; }

            Plugin.Log.LogInfo($"[TowerInject] JSON loaded: id={data.id}");

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

            if (FindByIdInDict(dictObj, "tower") != null || FindByIdInList(listObj, "tower") != null)
            { Plugin.DbInjected = true; Plugin.Log.LogInfo("[TowerInject] tower already in DB."); return; }

            var humanRaw = FindByIdInList(listObj, "human");
            if (humanRaw == null) { Plugin.Log.LogWarning("[TowerInject] human config not found."); return; }

            var human = humanRaw as FractionConfig
                        ?? new FractionConfig(((Il2CppSystem.Object)humanRaw).Pointer);

            Plugin.Log.LogInfo($"[TowerInject] human ok, id={human.id}");

            var tower = new FractionConfig(IL2CPP.il2cpp_object_new(
                Il2CppClassPointerStore<FractionConfig>.NativeClassPtr));

            CopyAllFields(human, tower);

            tower.id            = data.id;
            tower.name          = "";
            tower.desc          = data.desc;
            tower.narrativeDesc = data.narrativeDesc;

            Plugin.Log.LogInfo($"[TowerInject] tower created, id={tower.id}");

            var typedList = listObj as Il2CppCollections.List<FractionConfig>
                            ?? new Il2CppCollections.List<FractionConfig>(
                                ((Il2CppSystem.Object)listObj).Pointer);
            var typedDict = dictObj as Il2CppCollections.Dictionary<string, FractionConfig>
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(
                                ((Il2CppSystem.Object)dictObj).Pointer);

            typedList.Add(tower);
            typedDict.Add("tower", tower);

            Plugin.DbInjected = true;
            Plugin.Log.LogInfo("[TowerInject] tower injected into bxjw and bxjx.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TowerInject] Injection failed: {ex}");
        }
    }

    static void CopyAllFields(FractionConfig src, FractionConfig dst)
    {
        var type = typeof(FractionConfig);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            try { field.SetValue(dst, field.GetValue(src)); } catch { }
        }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            try { if (prop.CanRead && prop.CanWrite) prop.SetValue(dst, prop.GetValue(src)); } catch { }
        }
        Plugin.Log.LogInfo("[TowerInject] CopyAllFields complete.");
    }

    // Used by qwa prefix to find the fractions list on SoFractions by common name.
    public static object GetMemberPublic(object obj, string name)
    {
        try { return GetMember(obj, name); } catch { return null; }
    }

    // Fallback: find the first List<FractionLobbyAsset> field on the object.
    public static object GetFirstListField<T>(object obj) where T : Il2CppSystem.Object
    {
        try
        {
            var t = obj.GetType();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var val = f.GetValue(obj);
                if (val == null) continue;
                try
                {
                    var list = val as Il2CppCollections.List<T>
                               ?? new Il2CppCollections.List<T>(((Il2CppSystem.Object)val).Pointer);
                    if (list.Count > 0) return list;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public static string GetIdSafe(object cfg)
    {
        try { return (cfg as FractionConfig)?.id; }
        catch { return null; }
    }

    static object GetStaticProperty(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null && p.CanRead) return p.GetValue(null);
        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m != null) return m.Invoke(null, null);
        return null;
    }

    public static object GetMember(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanRead) return p.GetValue(obj);
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) return f.GetValue(obj);
        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (m != null) return m.Invoke(obj, null);
        return null;
    }

    static FractionConfig FindByIdInList(object listObj, string wantedId)
    {
        try
        {
            var typedList = listObj as Il2CppCollections.List<FractionConfig>
                            ?? new Il2CppCollections.List<FractionConfig>(
                                ((Il2CppSystem.Object)listObj).Pointer);
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
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(
                                ((Il2CppSystem.Object)dictObj).Pointer);
            if (typedDict.ContainsKey(wantedId)) return typedDict[wantedId];
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TowerInject] FindByIdInDict: {ex.Message}"); }
        return null;
    }

    static IEnumerable<Type> TrySafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch { return Enumerable.Empty<Type>(); }
    }
}
