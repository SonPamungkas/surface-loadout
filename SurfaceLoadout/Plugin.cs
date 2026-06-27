using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SurfaceLoadout
{
    [BepInDependency("com.offiry.qol", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SurfaceLoadoutPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "surface.loadout";
        public const string PluginName = "Surface Loadout";
        public const string PluginVersion = "2.2.0"; // permanent

        public static SurfaceLoadoutPlugin Instance;
        private List<MissileDefinition> allMissiles;
        private List<string> missileNames;
        public static bool isConfigsInitialized = false;

        public class SurfaceLoadoutPreset
        {
            public string section;
            public string key;
            public string value;
        }

        public void LoadFromJson()
        {
            try
            {
                if (Config == null) return;
                string baseDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "SurfaceLoadout");
                if (!System.IO.Directory.Exists(baseDir)) return;

                int count = 0;
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var orphaned = prop?.GetValue(Config) as Dictionary<ConfigDefinition, string>;

                if (orphaned != null)
                {
                    foreach (string file in System.IO.Directory.GetFiles(baseDir, "*.json", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            string json = System.IO.File.ReadAllText(file);
                            var preset = UnityEngine.JsonUtility.FromJson<SurfaceLoadoutPreset>(json);
                            if (preset != null && !string.IsNullOrEmpty(preset.section) && !string.IsNullOrEmpty(preset.key))
                            {
                                var def = new ConfigDefinition(preset.section, preset.key);
                                if (!orphaned.ContainsKey(def))
                                {
                                    orphaned[def] = preset.value;
                                    count++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Failed to parse {file}: {ex.Message}");
                        }
                    }
                    Logger.LogInfo($"SurfaceLoadout: Successfully loaded {count} config backups from SurfaceLoadout folder!");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to load SurfaceLoadout json: " + ex);
            }
        }

        private static string SafeFileName(string name) => string.Join("_", (name ?? "").Split(System.IO.Path.GetInvalidFileNameChars()));

        public void SaveToJson()
        {
            if (Instance == null || Instance.Config == null || !isConfigsInitialized) return;
            try
            {
                string baseDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "SurfaceLoadout");
                System.IO.Directory.CreateDirectory(baseDir);

                var entries = new Dictionary<ConfigDefinition, string>();
                foreach (var def in Instance.Config.Keys)
                {
                    entries[def] = Instance.Config[def].GetSerializedValue();
                }

                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null)
                {
                    var orphaned = prop.GetValue(Instance.Config) as Dictionary<ConfigDefinition, string>;
                    if (orphaned != null)
                    {
                        foreach (var def in orphaned.Keys)
                        {
                            entries[def] = orphaned[def];
                        }
                    }
                }

                foreach (var kvp in entries)
                {
                    var def = kvp.Key;
                    string val = kvp.Value;

                    string sectionName = def.Section;
                    string folderName = SafeFileName(sectionName);
                    string fileName = SafeFileName(def.Key) + ".json";

                    string targetDir = System.IO.Path.Combine(baseDir, folderName);
                    System.IO.Directory.CreateDirectory(targetDir);

                    string filePath = System.IO.Path.Combine(targetDir, fileName);
                    var preset = new SurfaceLoadoutPreset { section = def.Section, key = def.Key, value = val };
                    string json = UnityEngine.JsonUtility.ToJson(preset, true);
                    System.IO.File.WriteAllText(filePath, json);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to save to SurfaceLoadout folder: " + ex);
            }
        }

        public class LauncherConfig
        {
            public ConfigEntry<string> MissileName;
            public ConfigEntry<int> MaxAmmo;
            public MissileDefinition GetMissileDef() => Instance.allMissiles.FirstOrDefault(m => Instance.GetMissileDisplayName(m) == MissileName.Value);
        }

        public string GetMissileDisplayName(MissileDefinition m)
        {
            if (m == null) return "None";
            string key = string.IsNullOrEmpty(m.jsonKey) ? (m.unitPrefab != null ? m.unitPrefab.name : m.name) : m.jsonKey;
            string rawName = string.IsNullOrEmpty(m.unitName) ? key : $"{m.unitName} ({key})";
            if (rawName.Length > 40) return rawName.Substring(0, 37) + "...";
            return rawName;
        }

        public Dictionary<string, Dictionary<string, LauncherConfig>> UnitConfigs = new Dictionary<string, Dictionary<string, LauncherConfig>>();

        private void Awake()
        {
            Instance = this;

            LoadFromJson();

            Config.SettingChanged += (sender, args) => { SaveToJson(); };

            // Config Migration to new GUID
            string newCfgPath = Path.Combine(BepInEx.Paths.ConfigPath, "surface.loadout.cfg");
            try
            {
                var oldConfigs = Directory.GetFiles(BepInEx.Paths.ConfigPath, "*SurfaceLoadout.cfg");
                foreach (var oldCfgPath in oldConfigs)
                {
                    if (!oldCfgPath.Equals(newCfgPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(newCfgPath))
                        {
                            File.Copy(oldCfgPath, newCfgPath);
                        }
                        File.Delete(oldCfgPath);
                        Logger.LogInfo($"Successfully migrated and deleted old configuration file: {Path.GetFileName(oldCfgPath)}");
                    }
                }
                // Reload config in case it was migrated
                Config.Reload();
            }
            catch (Exception e)
            {
                Logger.LogError($"Error migrating old config: {e.Message}");
            }

            StartCoroutine(InitLoadouts());
        }

        private string GetRelativePath(Transform t, Transform root)
        {
            string path = t.name + "_" + t.GetSiblingIndex();
            while (t.parent != null && t.parent != root)
            {
                t = t.parent;
                path = t.name + "_" + t.GetSiblingIndex() + "/" + path;
            }
            return path;
        }

        private IEnumerator InitLoadouts()
        {
            UnitDefinition[] units = null;
            while (true)
            {
                units = Resources.FindObjectsOfTypeAll<UnitDefinition>();
                if (units != null && units.Length > 0)
                    break;
                yield return new WaitForSeconds(2f);
            }

            allMissiles = Resources.FindObjectsOfTypeAll<MissileDefinition>().ToList();
            missileNames = allMissiles.Select(m => GetMissileDisplayName(m)).Distinct().ToList();
            missileNames.Sort();

            int moddedCount = 0;

            // Filter out unsupported units and sort alphabetically to ensure alphabetical config categories
            var sortedUnits = units.Where(u => !(u is AircraftDefinition || u is MissileDefinition) && u.unitPrefab != null)
                                   .OrderBy(u => {
                                       string key = string.IsNullOrEmpty(u.jsonKey) ? (u.unitPrefab != null ? u.unitPrefab.name : u.name) : u.jsonKey;
                                       return string.IsNullOrEmpty(u.unitName) ? key : $"{u.unitName} ({key})";
                                   }, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            foreach (var unitDef in sortedUnits)
            {
                var prefabLaunchers = unitDef.unitPrefab.GetComponentsInChildren<MissileLauncher>(true);
                
                if (prefabLaunchers.Length == 0) continue;

                string key = string.IsNullOrEmpty(unitDef.jsonKey) ? (unitDef.unitPrefab != null ? unitDef.unitPrefab.name : unitDef.name) : unitDef.jsonKey;
                string configCategory = string.IsNullOrEmpty(unitDef.unitName) ? key : $"{unitDef.unitName} ({key})";

                UnitConfigs[unitDef.name] = new Dictionary<string, LauncherConfig>();

                List<LauncherInfo> launcherInfos = new List<LauncherInfo>();
                
                // Process MissileLaunchers
                for (int i = 0; i < prefabLaunchers.Length; i++)
                {
                    string mName = prefabLaunchers[i].missile != null ? GetMissileDisplayName(prefabLaunchers[i].missile) : "None";
                    
                    var t = Traverse.Create(prefabLaunchers[i]);
                    int calculatedAmmo = t.Field<int>("maxAmmo").Value;
                    if (calculatedAmmo == 0)
                    {
                        var transforms = t.Field<Transform[]>("launchTransforms").Value;
                        if (transforms != null && transforms.Length > 0)
                            calculatedAmmo = transforms.Length;
                        else
                        {
                            int cols = t.Field<int>("cellColumns").Value;
                            int rows = t.Field<int>("cellRows").Value;
                            if (cols > 0 && rows > 0)
                                calculatedAmmo = cols * rows;
                        }
                    }

                    launcherInfos.Add(new LauncherInfo
                    {
                        Path = GetRelativePath(prefabLaunchers[i].transform, unitDef.unitPrefab.transform),
                        OriginalMissileName = mName,
                        OriginalMaxAmmo = calculatedAmmo
                    });
                    
                    if (!missileNames.Contains(mName))
                        missileNames.Add(mName);
                }

                var grouped = launcherInfos.GroupBy(l => l.OriginalMissileName + "_" + l.OriginalMaxAmmo);

                int groupIndex = 1;
                foreach (var group in grouped)
                {
                    var first = group.First();
                    string defaultMissile = first.OriginalMissileName;
                    int defaultAmmo = first.OriginalMaxAmmo;
                    int launcherCount = group.Count();

                    string symmetryText = launcherCount > 1 ? $" ({launcherCount}x Symmetrical)" : "";
                    string groupTitle = $"Group {groupIndex} ({defaultMissile} x{defaultAmmo}){symmetryText}";

                    var configMissile = Config.Bind(
                        configCategory, 
                        $"{groupTitle} - Missile Type", 
                        defaultMissile, 
                        new ConfigDescription($"Missile type for this launcher group", 
                            new AcceptableValueList<string>(missileNames.ToArray())));

                    var configAmmo = Config.Bind(
                        configCategory, 
                        $"{groupTitle} - Max Ammo", 
                        defaultAmmo, 
                        $"Amount of ammo for this launcher group");

                    configMissile.SettingChanged += (sender, args) => ApplyToUnit(unitDef);
                    configAmmo.SettingChanged += (sender, args) => ApplyToUnit(unitDef);

                    foreach (var info in group)
                    {
                        UnitConfigs[unitDef.name][info.Path] = new LauncherConfig
                        {
                            MissileName = configMissile,
                            MaxAmmo = configAmmo
                        };
                    }
                    groupIndex++;
                }

                ApplyToUnit(unitDef);
                moddedCount++;
            }

            Logger.LogInfo($"Surface Loadout initialized. Modded {moddedCount} surface units. Found {allMissiles.Count} projectiles.");
            
            isConfigsInitialized = true;
            SaveToJson();
        }

        public void ApplyToUnit(UnitDefinition unitDef)
        {
            if (unitDef == null) return;
            string uName = unitDef.name;

            if (UnitConfigs.TryGetValue(uName, out var launcherConfigs))
            {
                // 1. Update Prefab
                if (unitDef.unitPrefab != null)
                {
                    var prefabLaunchers = unitDef.unitPrefab.GetComponentsInChildren<MissileLauncher>(true);
                    for (int i = 0; i < prefabLaunchers.Length; i++)
                    {
                        string path = GetRelativePath(prefabLaunchers[i].transform, unitDef.unitPrefab.transform);
                        if (launcherConfigs.TryGetValue(path, out var config))
                        {
                            MissileDefinition mDef = config.GetMissileDef();
                            if (mDef != null)
                            {
                                prefabLaunchers[i].missile = mDef;
                                if (mDef.unitPrefab != null)
                                {
                                    var mScript = mDef.unitPrefab.GetComponent<Missile>();
                                    if (mScript != null)
                                    {
                                        var mInfo = Traverse.Create(mScript).Field("info").GetValue<WeaponInfo>();
                                        if (mInfo != null)
                                        {
                                            Traverse.Create(prefabLaunchers[i]).Field("info").SetValue(mInfo);
                                        }
                                    }
                                }
                            }
                            Traverse.Create(prefabLaunchers[i]).Field("maxAmmo").SetValue(config.MaxAmmo.Value);
                            Traverse.Create(prefabLaunchers[i]).Field("ammo").SetValue(config.MaxAmmo.Value);
                        }
                    }
                }

                // 2. Update Active Units
                foreach (var activeUnit in FindObjectsOfType<Unit>())
                {
                    if (activeUnit.definition != null && activeUnit.definition.name == uName)
                    {
                        var activeLaunchers = activeUnit.gameObject.GetComponentsInChildren<MissileLauncher>(true);
                        for (int i = 0; i < activeLaunchers.Length; i++)
                        {
                            string path = GetRelativePath(activeLaunchers[i].transform, activeUnit.transform);
                            if (launcherConfigs.TryGetValue(path, out var config))
                            {
                                var launcher = activeLaunchers[i];
                                MissileDefinition mDef = config.GetMissileDef();
                                if (mDef != null)
                                {
                                    launcher.missile = mDef;
                                    if (mDef.unitPrefab != null)
                                    {
                                        var mScript = mDef.unitPrefab.GetComponent<Missile>();
                                        if (mScript != null)
                                        {
                                            var mInfo = Traverse.Create(mScript).Field("info").GetValue<WeaponInfo>();
                                            if (mInfo != null)
                                            {
                                                Traverse.Create(launcher).Field("info").SetValue(mInfo);
                                            }
                                        }
                                    }
                                }
                                
                                Traverse.Create(launcher).Field("maxAmmo").SetValue(config.MaxAmmo.Value);
                                Traverse.Create(launcher).Field("ammo").SetValue(config.MaxAmmo.Value);
                                
                                try {
                                    Traverse.Create(launcher).Method("Rearm").GetValue();
                                } catch { }
                            }
                        }
                    }
                }
            }
        }

        private class LauncherInfo
        {
            public string Path;
            public string OriginalMissileName;
            public int OriginalMaxAmmo;
        }
    }
}
