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

// Basic plugin metadata for BepInEx
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    // Shared logger so every class can print useful info into the BepInEx console/log
    internal static new ManualLogSource Log;
    // Harmony is what we use to patch game methods at runtime
    internal static Harmony Harmony;
    // Prevents us from injecting the UI slot more than once
    internal static bool UiInjected;
    // Prevents us from injecting the config DB more than once
    internal static bool DbInjected;

    public override void Load()
    {
        Log = base.Log;

         // Create and apply all Harmony patches in this assembly
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();

        Log.LogInfo("TowerLegacy loaded.");
    }
}

// We hook ScLobby2.Init because by this point the game's config DB is already loaded
[HarmonyPatch(typeof(ScLobby2), nameof(ScLobby2.Init))]
public static class ScLobby2_Init_Patch
{
    public static void Postfix()
    {
        if (!Plugin.DbInjected)
            TowerDbInjector.TryInject();
    }
}

// We hook the faction select popup method that returns the list of faction buttons/assets
[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwb))]
public static class ScFractionSelect_qwb_Patch
{
    public static void Postfix(ref List<FractionLobbyAsset> __result)
    {
        try
        {

            // Another safety check:
            // If the game returned no faction list, we have nothing to edit.
            if (__result == null || __result.Count == 0)
            {
                Plugin.Log.LogWarning("[TowerInject] qwb returned no factions.");
                return;
            }

            // Check whether "tower" is already in the list.
            // This prevents duplicate Tower entries in the faction picker.
            for (int i = 0; i < __result.Count; i++)
            {
                var existing = __result[i];
                if (existing != null && existing.sid == "tower")
                {
                    //Plugin.UiInjected = true;
                    Plugin.Log.LogInfo("[TowerInject] UI already contains tower.");
                    return;
                }
            }

            // We need a base faction to copy visual assets from.
            // For now we use the Human faction because we know it already works.
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

            // If we couldn't find Human, we can't safely clone from it.
            if (src == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Could not find human UI slot.");
                return;
            }

            // Create a brand new UI faction asset entry.
            var clone = new FractionLobbyAsset();

            // IMPORTANT:
            // This sid must match the ID we inject into the game's config database.
            // UI sid = "tower"
            // DB id = "tower"
            clone.sid = "tower";

            // Reuse existing artwork/backgrounds for now so the slot renders safely.
            // Later, once you find custom assets, you can replace these.
            clone.icon = src.icon;
            clone.slotFon = src.slotFon;
            clone.statisticFon = src.statisticFon;
            clone.versusFon = src.versusFon;
            clone.bigIcon = src.bigIcon;
            clone.card = src.card;

             // Add our new slot to the game's returned faction list.
            __result.Add(clone);

            // Mark as done so we don't inject multiple times.
            //Plugin.UiInjected = true;

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

            // Find the game's main Hex assembly.
            // This is where the game's real classes live.
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Hex assembly not found.");
                return;
            }

            // Grab the runtime type for the faction config model.
            // You already confirmed this class exists and matches DB/fractions.
            var fractionType = hexAsm.GetType("Hex.Configs.FractionConfig");
            
            // Grab the obfuscated repository type you discovered from logs.
            var cjvType = hexAsm.GetType("cjv");

            if (cjvType == null || fractionType == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Required types not found.");
                return;
            }

            // Get the singleton/static repository instance.
            // From your logs, this is cjv.bxjy.
            var repo = GetStaticProperty(cjvType, "bxjy");
            if (repo == null)
            {
                Plugin.Log.LogWarning("[TowerInject] cjv.bxjy not found.");
                return;
            }

            // Inside the repo, get the actual fraction container.
            // From your logs, this is repo.bxni.
            var container = GetMember(repo, "bxni");
            if (container == null)
            {
                Plugin.Log.LogWarning("[TowerInject] repo.bxni not found.");
                return;
            }

            // bxjw = List<FractionConfig>
            // bxjx = Dictionary<string, FractionConfig>
            // We want BOTH:
            // - list for iteration / UI / ordering
            // - dictionary for direct lookup by ID
            var listObj = GetMember(container, "bxjw");
            var dictObj = GetMember(container, "bxjx");

            if (listObj == null || dictObj == null)
            {
                Plugin.Log.LogWarning("[TowerInject] bxjw or bxjx missing.");
                return;
            }

            Plugin.Log.LogInfo($"[TowerInject] bxjw type = {listObj.GetType().FullName}");
            Plugin.Log.LogInfo($"[TowerInject] bxjx type = {dictObj.GetType().FullName}");

            // If Tower already exists, stop here.
            // This prevents duplicate DB injection.
            var existingTower = FindByIdInDict(dictObj, "tower") ?? FindByIdInList(listObj, "tower");
            if (existingTower != null)
            {
                Plugin.DbInjected = true;
                Plugin.Log.LogInfo("[TowerInject] tower already exists in DB.");
                return;
            }

            // Find the Human config.
            // This is our template for creating a new faction safely.
            var human = FindByIdInDict(dictObj, "human") ?? FindByIdInList(listObj, "human");
            if (human == null)
            {
                Plugin.Log.LogWarning("[TowerInject] human config not found in bxjw/bxjx.");
                DumpListIds(listObj);
                return;
            }

            Plugin.Log.LogInfo("[TowerInject] Found human config, cloning...");

            // Create a new empty FractionConfig object.
            var tower = Activator.CreateInstance(fractionType);
            if (tower == null)
            {
                Plugin.Log.LogWarning("[TowerInject] Could not create FractionConfig.");
                return;
            }

            // Copy all fields from Human into Tower.
            // This gives Tower a working starting point.
            CopyAllFields(fractionType, human, tower);
            // Change the internal config ID so the game sees this as a new faction.
            SetId(tower, "tower");
            // Optional: replace text keys now, even if localization isn't ready yet.
            // You can later add matching localization entries for these.
            //SetString(tower, "name", "tower_name");
            //SetString(tower, "desc", "tower_desc");
            //SetString(tower, "narrativeDesc", "tower_narrative_desc");

            // Add to the list so it appears in normal faction collections.
            AddToList(listObj, tower);
            // Add to the dictionary so lookup by ID ("tower") succeeds.
            AddToDict(dictObj, "tower", tower);

            Plugin.DbInjected = true;
            Plugin.Log.LogInfo("[TowerInject] tower injected into bxjw and bxjx.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TowerInject] Direct DB injection failed: {ex}");
        }
    }

//Helper methods--------------------------------------------
    // Gets a static property like cjv.bxjy
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

    // Gets an instance field/property/getter from an object.
    // Example: repo.bxni or container.bxjw
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

    // Search the IL2CPP list for a FractionConfig with a given ID.
    static object FindByIdInList(object listObj, string wantedId)
    {
        var count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj);
        var itemProp = listObj.GetType().GetProperty("Item");

        for (int i = 0; i < count; i++)
        {
            var item = itemProp.GetValue(listObj, new object[] { i });
            if (item == null) continue;

            var id = GetId(item);
            if (id == wantedId)
                return item;
        }

        return null;
    }

    // Search the IL2CPP dictionary by key directly.
    static object FindByIdInDict(object dictObj, string wantedId)
    {
        var containsKey = dictObj.GetType().GetMethod("ContainsKey");
        var itemProp = dictObj.GetType().GetProperty("Item");

        if (containsKey != null && (bool)containsKey.Invoke(dictObj, new object[] { wantedId }))
            return itemProp.GetValue(dictObj, new object[] { wantedId });

        return null;
    }

    // Adds the new config to the list.
    static void AddToList(object listObj, object item)
    {
        var add = listObj.GetType().GetMethod("Add");
        add.Invoke(listObj, new[] { item });
        Plugin.Log.LogInfo("[TowerInject] Added tower to bxjw list.");
    }

    // Adds the new config to the dictionary under the key "tower".
    static void AddToDict(object dictObj, string key, object value)
    {
        var add = dictObj.GetType().GetMethod("Add");
        add.Invoke(dictObj, new object[] { key, value });
        Plugin.Log.LogInfo("[TowerInject] Added tower to bxjx dictionary.");
    }

    // Reads the ID field/property from a FractionConfig.
    // We check several common names because the base class may store it there.
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

    // Writes the new ID into the cloned FractionConfig.
    static void SetId(object cfg, string newId)
    {
        foreach (var t in GetTypeChain(cfg.GetType()))
        {
            foreach (var name in new[] { "id", "Id", "sid", "_id" })
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(cfg, newId);
                    Plugin.Log.LogInfo($"[TowerInject] Set {t.FullName}.{name} = {newId}");
                    return;
                }

                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                {
                    p.SetValue(cfg, newId);
                    Plugin.Log.LogInfo($"[TowerInject] Set {t.FullName}.{name} = {newId}");
                    return;
                }
            }
        }

        Plugin.Log.LogWarning("[TowerInject] Could not find id field/property to set.");
    }

    // Convenience helper for string fields like name/desc/narrativeDesc.
    static void SetString(object obj, string name, string value)
    {
        var t = obj.GetType();

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null && f.FieldType == typeof(string))
        {
            f.SetValue(obj, value);
            return;
        }

        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanWrite && p.PropertyType == typeof(string))
            p.SetValue(obj, value);
    }

    // Copies all instance fields from Human into Tower.
    // This is the "clone" step.
    static void CopyAllFields(Type rootType, object src, object dst)
    {
        foreach (var t in GetTypeChain(rootType))
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    var val = f.GetValue(src);
                    f.SetValue(dst, val);
                }
                catch
                {  
                    // Ignore fields we can't copy cleanly
                }
            }
        }
    }

    // Walks up the inheritance chain.
    // Needed because the ID may be stored in a base class, not directly in FractionConfig.
    static System.Collections.Generic.IEnumerable<Type> GetTypeChain(Type t)
    {
        while (t != null)
        {
            yield return t;
            t = t.BaseType;
        }
    }

    // Debug helper: prints the first few IDs from the list if Human wasn't found.
    static void DumpListIds(object listObj)
    {
        try
        {
            var count = (int)listObj.GetType().GetProperty("Count").GetValue(listObj);
            var itemProp = listObj.GetType().GetProperty("Item");

            Plugin.Log.LogInfo($"[TowerInject] bxjw count = {count}");

            for (int i = 0; i < count && i < 20; i++)
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