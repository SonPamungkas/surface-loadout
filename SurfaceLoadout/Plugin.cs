using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SurfaceLoadout
{
    [BepInPlugin("com.RaksaPutra.SurfaceLoadout", "Surface Loadout", "1.0.0")]
    public class SurfaceLoadoutPlugin : BaseUnityPlugin
    {
        public static SurfaceLoadoutPlugin Instance;
        private List<MissileDefinition> allMissiles;
        private List<string> missileNames;

        public class LauncherConfig
        {
            public ConfigEntry<string> MissileName;
            public ConfigEntry<int> MaxAmmo;
            public MissileDefinition GetMissileDef() => Instance.allMissiles.FirstOrDefault(m => Instance.GetMissileDisplayName(m) == MissileName.Value);
        }

        public string GetMissileDisplayName(MissileDefinition m)
        {
            if (m == null) return "None";
            string pName = m.unitPrefab != null ? m.unitPrefab.name : m.name;
            return string.IsNullOrEmpty(m.unitName) ? pName : $"{m.unitName} ({pName})";
        }

        public Dictionary<string, Dictionary<string, LauncherConfig>> UnitConfigs = new Dictionary<string, Dictionary<string, LauncherConfig>>();

        private void Awake()
        {
            Instance = this;
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

            foreach (var unitDef in units)
            {
                if (unitDef is AircraftDefinition || unitDef is MissileDefinition) continue;
                if (unitDef.unitPrefab == null) continue;
                
                var prefabLaunchers = unitDef.unitPrefab.GetComponentsInChildren<MissileLauncher>(true);
                if (prefabLaunchers.Length == 0) continue;

                string pName = unitDef.unitPrefab != null ? unitDef.unitPrefab.name : unitDef.name;
                string configCategory = string.IsNullOrEmpty(unitDef.unitName) ? pName : $"{unitDef.unitName} ({pName})";

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
