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
using UnityEngine.UI;
using TMPro;
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
        DumpAndPatchSlotSetters();
        Log.LogInfo("TowerLegacy loaded.");
    }

    // Find every method in ScFractionSlot (the individual card) that takes a
    // FractionLobbyAsset and patch it so we can see which one writes the name.
    static bool _slotDumped = false;
    internal static void DumpAndPatchSlotSetters()
    {
        try
        {
            var hexAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Hex");
            if (hexAsm == null) return;

            // Dump all types that touch FractionLobbyAsset in their methods.
            foreach (var type in hexAsm.GetTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        var parms = m.GetParameters();
                        bool takesFla = parms.Any(p => p.ParameterType == typeof(FractionLobbyAsset));
                        if (!takesFla) continue;

                        Log.LogInfo($"[SlotDump] {type.Name}.{m.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name + " " + p.Name))})");

                        // Patch it with a postfix that logs the sid of the asset passed.
                        var postfix = new HarmonyMethod(typeof(Plugin), nameof(SlotSetterPostfix));
                        try { Harmony.Patch(m, postfix: postfix); }
                        catch { /* skip unpatchable */ }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Log.LogWarning($"[SlotDump] Failed: {ex.Message}"); }
    }

    // Generic postfix: logs which method was called and the sid of the asset.
    // __instance may be null for static methods.
    public static void SlotSetterPostfix(object __instance, FractionLobbyAsset __0)
    {
        try
        {
            var typeName   = __instance?.GetType().Name ?? "(static)";
            var sid        = __0?.sid ?? "null";
            var methodName = new System.Diagnostics.StackFrame(1).GetMethod()?.Name ?? "?";
            Log.LogInfo($"[SlotCall] {typeName}.{methodName} sid={sid}");

            // Also dump all TMP_Text children so we can see what text is set.
            if (__instance != null)
            {
                var go = (__instance as UnityEngine.MonoBehaviour)?.gameObject;
                if (go != null)
                {
                    foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>())
                        Log.LogInfo($"[SlotCall]   TMP '{tmp.name}' = \"{tmp.text}\"");
                }
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

            var dictFieldPtr = IL2CPP.GetIl2CppField(soClassPtr, "dict_");
            var dictObjPtr   = IL2CPP.il2cpp_field_get_value_object(dictFieldPtr, objPtr);
            if (dictObjPtr != IntPtr.Zero)
            {
                var dict = new Il2CppSystem.Collections.Generic.Dictionary<string, FractionLobbyAsset>(dictObjPtr);
                Plugin.Log.LogInfo($"[TowerInject] dict_ has {dict.Count} entries, skipping tower key insert.");
            }

            _injectedQwa = true;
            Plugin.Log.LogInfo("[TowerInject] Injected tower slot (sid=human) into SoFractions.");
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
        slot.sid = "human";
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
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot via qwb (sid=human).");
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

            // Hard-copy cityNames
            var cities = new Il2CppCollections.List<string>();
            if (humanCfg.cityNames != null)
                for (int i = 0; i < humanCfg.cityNames.Count; i++)
                    cities.Add(humanCfg.cityNames[i]);
            if (cities.Count == 0) cities.Add("Tower City");
            tower.cityNames = cities;

            // Hard-copy heroes
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
