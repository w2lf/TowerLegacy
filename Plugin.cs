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
using Il2CppCollections = Il2CppSystem.Collections.Generic;

namespace TowerLegacy;

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
            if (__result == null || __result.Count == 0)
            {
                Plugin.Log.LogWarning("[TowerInject] qwb returned no factions.");
                return;
            }

            for (int i = 0; i < __result.Count; i++)
            {
                var existing = __result[i];
                if (existing != null && existing.sid == "tower")
                {
                    Plugin.Log.LogInfo("[TowerInject] UI already contains tower.");
                    return;
                }
            }

            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
            {
                var f = __result[i];
                if (f != null && f.sid == "human") { src = f; break; }
            }

            if (src == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Could not find human UI slot.");
                return;
            }

            var slot = new FractionLobbyAsset();
            slot.sid          = "tower";
            slot.icon         = src.icon;
            slot.slotFon      = src.slotFon;
            slot.statisticFon = src.statisticFon;
            slot.versusFon    = src.versusFon;
            slot.bigIcon      = src.bigIcon;
            slot.card         = src.card;

            __result.Add(slot);
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot: tower");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TowerInject] UI injection failed: {ex}");
        }
    }
}

// Safety net patches — cover any get_Name / get_name variant the game may use
[HarmonyPatch(typeof(FractionConfig), "get_Name")]
public static class FractionConfig_GetName_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (__instance?.id == "tower") __result = "Tower"; }
        catch { }
    }
}

[HarmonyPatch(typeof(FractionConfig), "get_name")]
public static class FractionConfig_get_name_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (__instance?.id == "tower") __result = "Tower"; }
        catch { }
    }
}

internal static class TowerDbInjector
{
    public static void TryInject()
    {
        try
        {
            Plugin.Log.LogInfo("[TowerInject] Direct DB injection starting...");

            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) { Plugin.Log.LogWarning("[TowerInject] Hex assembly not found."); return; }

            var cjvType = hexAsm.GetType("cjv");
            if (cjvType == null) { Plugin.Log.LogWarning("[TowerInject] cjv type not found."); return; }

            var repo = GetStaticProperty(cjvType, "bxjy");
            if (repo == null) { Plugin.Log.LogWarning("[TowerInject] cjv.bxjy not found."); return; }

            var container = GetMember(repo, "bxni");
            if (container == null) { Plugin.Log.LogWarning("[TowerInject] repo.bxni not found."); return; }

            var listObj = GetMember(container, "bxni") ?? GetMember(container, "bxjw");
            var dictObj = GetMember(container, "bxjx");
            if (listObj == null || dictObj == null)
            { Plugin.Log.LogWarning("[TowerInject] bxjw or bxjx missing."); return; }

            Plugin.Log.LogInfo($"[TowerInject] bxjw type = {listObj.GetType().FullName}");
            Plugin.Log.LogInfo($"[TowerInject] bxjx type = {dictObj.GetType().FullName}");

            // Already injected?
            if (FindByIdInDict(dictObj, "tower") != null || FindByIdInList(listObj, "tower") != null)
            {
                Plugin.DbInjected = true;
                Plugin.Log.LogInfo("[TowerInject] tower already exists in DB.");
                return;
            }

            // Get typed human config
            var humanRaw = FindByIdInList(listObj, "human");
            if (humanRaw == null) { Plugin.Log.LogWarning("[TowerInject] human config not found."); return; }

            var human = humanRaw as FractionConfig
                        ?? new FractionConfig(((Il2CppSystem.Object)humanRaw).Pointer);

            Plugin.Log.LogInfo($"[TowerInject] human typed cast ok, id={human.id} name={human.name}");

            // Create new native FractionConfig and set only known-good properties
            var tower = new FractionConfig(IL2CPP.il2cpp_object_new(
                Il2CppClassPointerStore<FractionConfig>.NativeClassPtr));

            tower.id           = "tower";
            tower.name         = "Tower";
            tower.desc         = human.desc;
            tower.narrativeDesc = human.narrativeDesc;

            Plugin.Log.LogInfo($"[TowerInject] tower.id={tower.id} tower.name={tower.name}");

            // Add to typed collections
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
            Plugin.Log.LogError($"[TowerInject] Direct DB injection failed: {ex}");
        }
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
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[TowerInject] FindByIdInList failed: {ex.Message}");
        }
        return null;
    }

    static FractionConfig FindByIdInDict(object dictObj, string wantedId)
    {
        try
        {
            var typedDict = dictObj as Il2CppCollections.Dictionary<string, FractionConfig>
                            ?? new Il2CppCollections.Dictionary<string, FractionConfig>(
                                ((Il2CppSystem.Object)dictObj).Pointer);
            if (typedDict.ContainsKey(wantedId))
                return typedDict[wantedId];
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[TowerInject] FindByIdInDict failed: {ex.Message}");
        }
        return null;
    }
}
