using System;
using System.Collections;
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

// Patch get_Name / get_name on FractionConfig — whichever the game uses
[HarmonyPatch(typeof(FractionConfig), "get_Name")]
public static class FractionConfig_GetName_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (TowerDbInjector.GetIdSafe(__instance) == "tower") __result = "Tower"; }
        catch { }
    }
}

[HarmonyPatch(typeof(FractionConfig), "get_name")]
public static class FractionConfig_get_name_Patch
{
    public static void Postfix(FractionConfig __instance, ref string __result)
    {
        try { if (TowerDbInjector.GetIdSafe(__instance) == "tower") __result = "Tower"; }
        catch { }
    }
}

// Safety net: patch the localization Translate method to intercept the tower
// faction name if get_Name/get_name patches don't fire.
[HarmonyPatch]
public static class LocalizationTranslate_Patch
{
    static MethodBase TargetMethod()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var t in TrySafeGetTypes(asm))
            {
                if (!t.Name.ToLower().Contains("local") && !t.Name.ToLower().Contains("loc"))
                    continue;

                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.ReturnType != typeof(string)) continue;
                    var prms = m.GetParameters();
                    if (prms.Length == 1 && prms[0].ParameterType == typeof(string))
                    {
                        Plugin.Log.LogInfo($"[TowerInject] Loc patch targeting: {t.FullName}.{m.Name}");
                        return m;
                    }
                }
            }
        }
        Plugin.Log.LogWarning("[TowerInject] Could not find localization Translate method.");
        return null;
    }

    static IEnumerable<Type> TrySafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch { return Enumerable.Empty<Type>(); }
    }

    public static void Postfix(string __0, ref string __result)
    {
        // Safety net only — TryNullNameField + get_Name patches handle naming.
        // Nothing to do here unless those fail.
        _ = __0;
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

            var fractionType = hexAsm.GetType("Hex.Configs.FractionConfig");
            var cjvType      = hexAsm.GetType("cjv");

            if (fractionType == null || cjvType == null)
            { Plugin.Log.LogWarning("[TowerInject] Required types not found."); return; }

            var repo = GetStaticProperty(cjvType, "bxjy");
            if (repo == null) { Plugin.Log.LogWarning("[TowerInject] cjv.bxjy not found."); return; }

            var container = GetMember(repo, "bxni");
            if (container == null) { Plugin.Log.LogWarning("[TowerInject] repo.bxni not found."); return; }

            var listObj = GetMember(container, "bxjw");
            var dictObj = GetMember(container, "bxjx");
            if (listObj == null || dictObj == null)
            { Plugin.Log.LogWarning("[TowerInject] bxjw or bxjx missing."); return; }

            Plugin.Log.LogInfo($"[TowerInject] bxjw type = {listObj.GetType().FullName}");
            Plugin.Log.LogInfo($"[TowerInject] bxjx type = {dictObj.GetType().FullName}");

            var existing = FindByIdInDict(dictObj, "tower") ?? FindByIdInList(listObj, "tower");
            if (existing != null)
            {
                Plugin.DbInjected = true;
                Plugin.Log.LogInfo("[TowerInject] tower already exists in DB.");
                return;
            }

            var human = FindByIdInDict(dictObj, "human") ?? FindByIdInList(listObj, "human");
            if (human == null)
            {
                Plugin.Log.LogWarning("[TowerInject] human config not found.");
                DumpListIds(listObj);
                return;
            }

            Plugin.Log.LogInfo("[TowerInject] Cloning human into tower...");

            var tower = Activator.CreateInstance(fractionType);
            if (tower == null) { Plugin.Log.LogWarning("[TowerInject] Could not create FractionConfig instance."); return; }

            CopyAllFields(fractionType, human, tower);
            SetId(tower, "tower");
            TryNullNameField(tower, fractionType);

            AddToList(listObj, tower);
            AddToDict(dictObj, "tower", tower);

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
        try { return GetId(cfg); }
        catch { return null; }
    }

    static void CopyAllFields(Type rootType, object src, object dst)
    {
        var skipNames = new HashSet<string>
        {
            "name", "Name", "_name",
            "desc", "Desc", "_desc",
            "narrativeDesc", "NarrativeDesc", "_narrativeDesc"
        };

        foreach (var t in GetTypeChain(rootType))
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (skipNames.Contains(f.Name)) continue;

                var ft = f.FieldType;
                if (ft.IsArray) continue;
                if (typeof(ICollection).IsAssignableFrom(ft)) continue;
                if (ft.IsGenericType)
                {
                    var gtd = ft.GetGenericTypeDefinition();
                    if (gtd == typeof(List<>) || gtd == typeof(Dictionary<,>) ||
                        gtd == typeof(HashSet<>) || gtd == typeof(Queue<>))
                        continue;
                }
                var ftn = ft.FullName ?? "";
                if (ftn.Contains("Il2CppSystem.Collections") || ftn.Contains("Il2CppInterop"))
                    continue;

                try { f.SetValue(dst, f.GetValue(src)); }
                catch { }
            }
        }
    }

    static void TryNullNameField(object cfg, Type rootType)
    {
        foreach (var t in GetTypeChain(rootType))
        {
            foreach (var fname in new[] { "name", "Name", "_name" })
            {
                var f = t.GetField(fname, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;

                try
                {
                    if (f.FieldType == typeof(string))
                    {
                        f.SetValue(cfg, "Tower");
                        Plugin.Log.LogInfo($"[TowerInject] Set string name field '{fname}' = Tower");
                        return;
                    }
                    if (!f.FieldType.IsValueType)
                    {
                        f.SetValue(cfg, null);
                        Plugin.Log.LogInfo($"[TowerInject] Nulled ref name field '{fname}'");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[TowerInject] TryNullNameField '{fname}' failed: {ex.Message}");
                }
            }
        }
    }

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

    static object FindByIdInList(object listObj, string wantedId)
    {
        var count    = (int)listObj.GetType().GetProperty("Count").GetValue(listObj);
        var itemProp = listObj.GetType().GetProperty("Item");
        for (int i = 0; i < count; i++)
        {
            var item = itemProp.GetValue(listObj, new object[] { i });
            if (item == null) continue;
            if (GetId(item) == wantedId) return item;
        }
        return null;
    }

    static object FindByIdInDict(object dictObj, string wantedId)
    {
        var containsKey = dictObj.GetType().GetMethod("ContainsKey");
        var itemProp    = dictObj.GetType().GetProperty("Item");
        if (containsKey != null && (bool)containsKey.Invoke(dictObj, new object[] { wantedId }))
            return itemProp.GetValue(dictObj, new object[] { wantedId });
        return null;
    }

    static void AddToList(object listObj, object item)
    {
        listObj.GetType().GetMethod("Add").Invoke(listObj, new[] { item });
        Plugin.Log.LogInfo("[TowerInject] Added tower to bxjw list.");
    }

    static void AddToDict(object dictObj, string key, object value)
    {
        dictObj.GetType().GetMethod("Add").Invoke(dictObj, new object[] { key, value });
        Plugin.Log.LogInfo($"[TowerInject] Added {key} to bxjx dictionary.");
    }

    static IEnumerable<Type> GetTypeChain(Type t)
    {
        while (t != null) { yield return t; t = t.BaseType; }
    }

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
