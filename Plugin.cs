using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Hex.GameHub.Lobby.UI;
using Hex.GameHub.UICommon;
using Il2CppSystem.Collections.Generic;

namespace TowerLegacy;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    internal static Harmony Harmony;
    internal static bool UiInjected;
    internal static bool DbInjected;

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        Log.LogInfo("TowerLegacy loaded.");
    }
}

// Hook ScLobby2.Init — config DB is fully loaded by this point
[HarmonyPatch(typeof(ScLobby2), nameof(ScLobby2.Init))]
public static class ScLobby2_Init_Patch
{
    public static void Postfix()
    {
        if (!Plugin.DbInjected)
            TowerDbInjector.TryInject();
    }
}

// Hook the faction picker list — runs every time the window opens
[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwb))]
public static class ScFractionSelect_qwb_Patch
{
    public static void Postfix(ref List<FractionLobbyAsset> __result)
    {
        try
        {
            if (__result == null || __result.Count == 0)
            {
                Plugin.Log.LogWarning("[TowerInject] qwb returned no factions.");
                return;
            }

            // Already injected into this particular list build — skip
            for (int i = 0; i < __result.Count; i++)
            {
                var existing = __result[i];
                if (existing != null && existing.sid == "tower")
                {
                    Plugin.Log.LogInfo("[TowerInject] UI already contains tower.");
                    return;
                }
            }

            // Use Human as visual template (icons/backgrounds only, not a clone)
            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
            {
                var f = __result[i];
                if (f != null && f.sid == "human")
                {
                    src = f;
                    break;
                }
            }

            if (src == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Could not find human UI slot.");
                return;
            }

            // New UI entry — only share visual asset refs, not config data
            var slot = new FractionLobbyAsset();
            slot.sid      = "tower";
            slot.icon      = src.icon;
            slot.slotFon   = src.slotFon;
            slot.statisticFon = src.statisticFon;
            slot.versusFon = src.versusFon;
            slot.bigIcon   = src.bigIcon;
            slot.card      = src.card;

            __result.Add(slot);
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot: tower");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TowerInject] UI injection failed: {ex}");
        }
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
            if (hexAsm == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Hex assembly not found.");
                return;
            }

            var cjvType = hexAsm.GetType("cjv");
            if (cjvType == null)
            {
                Plugin.Log.LogWarning("[TowerInject] cjv type not found.");
                return;
            }

            var repo = GetStaticProperty(cjvType, "bxjy");
            if (repo == null)
            {
                Plugin.Log.LogWarning("[TowerInject] cjv.bxjy not found.");
                return;
            }

            var container = GetMember(repo, "bxni");
            if (container == null)
            {
                Plugin.Log.LogWarning("[TowerInject] repo.bxni not found.");
                return;
            }

            var listObj = GetMember(container, "bxjw");
            var dictObj = GetMember(container, "bxjx");

            if (listObj == null || dictObj == null)
            {
                Plugin.Log.LogWarning("[TowerInject] bxjw or bxjx missing.");
                return;
            }

            Plugin.Log.LogInfo($"[TowerInject] bxjw type = {listObj.GetType().FullName}");
            Plugin.Log.LogInfo($"[TowerInject] bxjx type = {dictObj.GetType().FullName}");

            // Already linked into dict — nothing to do
            if (FindByIdInDict(dictObj, "tower") != null)
            {
                Plugin.DbInjected = true;
                Plugin.Log.LogInfo("[TowerInject] tower already exists in bxjx.");
                return;
            }

            // Find the real tower config already loaded from DB/fractions/tower.json
            var tower = FindByIdInList(listObj, "tower");
            if (tower == null)
            {
                Plugin.Log.LogWarning("[TowerInject] tower not found in loaded DB list. Check DB/fractions/tower.json exists.");
                DumpListIds(listObj);
                return;
            }

            // Link it into the dictionary so lookup by ID succeeds — no cloning
            AddToDict(dictObj, "tower", tower);
            Plugin.DbInjected = true;
            Plugin.Log.LogInfo("[TowerInject] Linked real tower config into bxjx.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TowerInject] Direct DB injection failed: {ex}");
        }
    }

    // Helper: static property like cjv.bxjy
    static object GetStaticProperty(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null && p.CanRead)
            return p.GetValue(null);

        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m != null)
            return m.Invoke(null, null);

        return null;
    }

    // Helper: instance field/property/getter like repo.bxni
    static object GetMember(object obj, string name)
    {
        var t = obj.GetType();

        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanRead)
            return p.GetValue(obj);

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null)
            return f.GetValue(obj);

        var m = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (m != null)
            return m.Invoke(obj, null);

        return null;
    }

    // Walk IL2CPP list and find entry whose id/sid field matches
    static object FindByIdInList(object listObj, string wantedId)
    {
        var count    = (int)listObj.GetType().GetProperty("Count").GetValue(listObj);
        var itemProp = listObj.GetType().GetProperty("Item");

        for (int i = 0; i < count; i++)
        {
            var item = itemProp.GetValue(listObj, new object[] { i });
            if (item == null) continue;

            if (GetId(item) == wantedId)
                return item;
        }

        return null;
    }

    // Look up a key in the IL2CPP dictionary
    static object FindByIdInDict(object dictObj, string wantedId)
    {
        var containsKey = dictObj.GetType().GetMethod("ContainsKey");
        var itemProp    = dictObj.GetType().GetProperty("Item");

        if (containsKey != null && (bool)containsKey.Invoke(dictObj, new object[] { wantedId }))
            return itemProp.GetValue(dictObj, new object[] { wantedId });

        return null;
    }

    // Add entry to IL2CPP dictionary
    static void AddToDict(object dictObj, string key, object value)
    {
        dictObj.GetType().GetMethod("Add").Invoke(dictObj, new object[] { key, value });
        Plugin.Log.LogInfo($"[TowerInject] Added {key} to bxjx dictionary.");
    }

    // Read the id/sid field from any config object
    static string GetId(object cfg)
    {
        foreach (var t in GetTypeChain(cfg.GetType()))
        {
            foreach (var name in new[] { "id", "Id", "sid", "_id" })
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(string))
                    return f.GetValue(cfg) as string;

                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanRead && p.PropertyType == typeof(string))
                    return p.GetValue(cfg) as string;
            }
        }

        return null;
    }

    // Walk up inheritance chain
    static System.Collections.Generic.IEnumerable<Type> GetTypeChain(Type t)
    {
        while (t != null)
        {
            yield return t;
            t = t.BaseType;
        }
    }

    // Debug: dump first 30 IDs from bxjw when tower is missing
    static void DumpListIds(object listObj)
    {
        try
        {
            var count    = (int)listObj.GetType().GetProperty("Count").GetValue(listObj);
            var itemProp = listObj.GetType().GetProperty("Item");

            Plugin.Log.LogInfo($"[TowerInject] bxjw count = {count}");

            for (int i = 0; i < count && i < 30; i++)
            {
                var item = itemProp.GetValue(listObj, new object[] { i });
                if (item == null) continue;
                Plugin.Log.LogInfo($"[TowerInject] bxjw[{i}] id = {GetId(item)}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[TowerInject] DumpListIds failed: {ex.Message}");
        }
    }
}
