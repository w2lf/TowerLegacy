// Workaround: IL2CPP net6 BepInEx target is missing NullableAttribute compiler support.
namespace System.Runtime.CompilerServices
{
    internal sealed class NullableAttribute : Attribute { public NullableAttribute(byte _) { } public NullableAttribute(byte[] _) { } }
    internal sealed class NullableContextAttribute : Attribute { public NullableContextAttribute(byte _) { } }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    internal static readonly Dictionary<string, string> LocOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "tower_name", TowerDisplayName },
        { "tower_desc", "A mighty tower faction." },
    };

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony.PatchAll();
        PatchLocKit();
        Log.LogInfo("TowerLegacy loaded.");
    }

    void PatchLocKit()
    {
        try
        {
            var locKitType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "LocKit");

            if (locKitType == null) { Log.LogWarning("[TowerInject] LocKit type not found."); return; }

            var candidates = locKitType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.ReturnType == typeof(string)
                         && m.GetParameters().Length >= 1
                         && m.GetParameters()[0].ParameterType == typeof(string))
                .ToList();

            Log.LogInfo($"[TowerInject] LocKit candidates: {candidates.Count}");
            foreach (var c in candidates)
                Log.LogInfo($"[TowerInject]   LocKit method: {c.Name}({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))})");

            foreach (var method in candidates)
            {
                try
                {
                    Harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(LocKit_Get_Patch), nameof(LocKit_Get_Patch.Prefix)));
                    Log.LogInfo($"[TowerInject] Patched LocKit.{method.Name}");
                }
                catch (Exception ex) { Log.LogWarning($"[TowerInject] LocKit patch failed for {method.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.LogError($"[TowerInject] PatchLocKit failed: {ex}"); }
    }
}

public static class LocKit_Get_Patch
{
    public static bool Prefix(object[] __args, ref string __result)
    {
        try
        {
            if (__args == null || __args.Length == 0) return true;
            var key = __args[0] as string;
            if (key != null && Plugin.LocOverrides.TryGetValue(key, out var val))
            {
                __result = val;
                return false;
            }
        }
        catch { }
        return true;
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

[HarmonyPatch(typeof(ScFractionSelect), nameof(ScFractionSelect.qwa))]
public static class ScFractionSelect_qwa_Patch
{
    public static void Prefix(ScFractionSelect __instance)
    {
        try
        {
            var assets = __instance?.fractionsAssets;
            if (assets == null) { Plugin.Log.LogWarning("[TowerInject] fractionsAssets null."); return; }

            var soClassPtr = Il2CppClassPointerStore<SoFractions>.NativeClassPtr;
            var objPtr     = IL2CPP.Il2CppObjectBaseToPtrNotNull(assets);

            var arrFieldPtr = IL2CPP.GetIl2CppField(soClassPtr, "fractions");
            var arrObjPtr   = IL2CPP.il2cpp_field_get_value_object(arrFieldPtr, objPtr);
            if (arrObjPtr == IntPtr.Zero) { Plugin.Log.LogWarning("[TowerInject] fractions ptr zero."); return; }

            var arr = new Il2CppReferenceArray<FractionLobbyAsset>(arrObjPtr);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i]?.sid == "tower") return;

            FractionLobbyAsset src = null;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i]?.sid == "human") { src = arr[i]; break; }
            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human slot not found."); return; }

            var slot = BuildSlot(src);

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
                if (!dict.ContainsKey("tower")) { dict.Add("tower", slot); Plugin.Log.LogInfo("[TowerInject] dict_ updated."); }
            }
            else Plugin.Log.LogWarning("[TowerInject] dict_ is null, skipping.");

            Plugin.Log.LogInfo("[TowerInject] Injected tower into SoFractions before qwa.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwa prefix failed: {ex}"); }
    }

    internal static FractionLobbyAsset BuildSlot(FractionLobbyAsset src) => new FractionLobbyAsset
    {
        sid          = "tower",
        icon         = src.icon,
        slotFon      = src.slotFon,
        statisticFon = src.statisticFon,
        versusFon    = src.versusFon,
        bigIcon      = src.bigIcon,
        card         = src.card
    };
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

            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "tower") return;

            var listPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(__result);
            if (_injected.Contains(listPtr)) return;

            FractionLobbyAsset src = null;
            for (int i = 0; i < __result.Count; i++)
                if (__result[i]?.sid == "human") { src = __result[i]; break; }
            if (src == null) { Plugin.Log.LogWarning("[TowerInject] human not found (qwb)."); return; }

            __result.Add(ScFractionSelect_qwa_Patch.BuildSlot(src));
            _injected.Add(listPtr);
            Plugin.Log.LogInfo("[TowerInject] Injected UI slot via qwb.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[TowerInject] qwb failed: {ex}"); }
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
            { Plugin.Log.LogWarning($"[TowerInject] JSON not found: {JsonPath}"); return; }

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
            tower.name          = data.name ?? Plugin.TowerDisplayName;
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
            try { field.SetValue(dst, field.GetValue(src)); } catch { }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            try { if (prop.CanRead && prop.CanWrite) prop.SetValue(dst, prop.GetValue(src)); } catch { }
        Plugin.Log.LogInfo("[TowerInject] CopyAllFields complete.");
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
