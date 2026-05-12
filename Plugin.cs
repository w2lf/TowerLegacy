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

    // The display name used everywhere as a hard fallback
    internal const string TowerDisplayName = "Tower City";

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

            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human UI slot not found."); return; }

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
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot: tower");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] UI injection failed: {ex}"); }
    }
}

// Hard override: if the game resolves the name field through any property,
// intercept and return our display name directly.
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

            // 1. Load JSON
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

            Plugin.Log.LogInfo($"[TowerInject] JSON loaded: id={data.id} name={data.name}");

            // 2. Inject localization entry (best-effort, Harmony patch is the real fallback)
            TryInjectLocKey(data.name, Plugin.TowerDisplayName);

            // 3. Find DB collections
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

            Plugin.Log.LogInfo($"[TowerInject] bxjw type = {listObj.GetType().FullName}");
            Plugin.Log.LogInfo($"[TowerInject] bxjx type = {dictObj.GetType().FullName}");

            if (FindByIdInDict(dictObj, "tower") != null || FindByIdInList(listObj, "tower") != null)
            { Plugin.DbInjected = true; Plugin.Log.LogInfo("[TowerInject] tower already in DB."); return; }

            // 4. Get human config as base
            var humanRaw = FindByIdInList(listObj, "human");
            if (humanRaw == null) { Plugin.Log.LogWarning("[TowerInject] human config not found."); return; }

            var human = humanRaw as FractionConfig
                        ?? new FractionConfig(((Il2CppSystem.Object)humanRaw).Pointer);

            Plugin.Log.LogInfo($"[TowerInject] human ok, id={human.id}");

            var tower = new FractionConfig(IL2CPP.il2cpp_object_new(
                Il2CppClassPointerStore<FractionConfig>.NativeClassPtr));

            // Store the display name directly — no loc key needed since
            // the Harmony get_Name patch will always override to TowerDisplayName.
            tower.id            = data.id;
            tower.name          = Plugin.TowerDisplayName;  // "Tower City" stored directly
            tower.desc          = data.desc;
            tower.narrativeDesc = data.narrativeDesc;

            Plugin.Log.LogInfo($"[TowerInject] tower.id={tower.id} tower.name={tower.name}");

            // 5. Add to collections
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

    static void TryInjectLocKey(string key, string value)
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) return;

            foreach (var type in TrySafeGetTypes(hexAsm))
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!typeof(System.Collections.IDictionary).IsAssignableFrom(field.FieldType)) continue;
                    var dict = field.GetValue(null) as System.Collections.IDictionary;
                    if (dict == null || !dict.Contains("human_name")) continue;

                    if (!dict.Contains(key))
                    {
                        dict[key] = value;
                        Plugin.Log.LogInfo($"[TowerInject] Injected loc key '{key}' = '{value}' into {type.FullName}.{field.Name}");
                    }
                    else
                        Plugin.Log.LogInfo($"[TowerInject] Loc key '{key}' already present.");
                    return;
                }
            }

            Plugin.Log.LogWarning($"[TowerInject] Could not find loc table for '{key}' (Harmony patch handles display name).");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TowerInject] TryInjectLocKey failed: {ex.Message}"); }
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

    static object GetMember(object obj, string name)
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
