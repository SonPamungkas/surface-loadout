using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NavalLoadout
{
    [BepInPlugin("com.RaksaPutra.NavalLoadout", "Naval Loadout", "1.0.0")]
    public class NavalLoadoutPlugin : BaseUnityPlugin
    {
        public static NavalLoadoutPlugin Instance;
        private List<MissileDefinition> allMissiles;
        private List<string> missileNames;

        // Maps ShipDefinition.unitName -> Launcher Index -> Configs
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

        public Dictionary<string, Dictionary<string, LauncherConfig>> ShipConfigs = new Dictionary<string, Dictionary<string, LauncherConfig>>();

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
            ShipDefinition[] ships = null;
            while (true)
            {
                ships = Resources.FindObjectsOfTypeAll<ShipDefinition>();
                if (ships.Length > 0)
                    break;
                yield return new WaitForSeconds(2f);
            }

            allMissiles = Resources.FindObjectsOfTypeAll<MissileDefinition>().ToList();
            missileNames = allMissiles.Select(m => GetMissileDisplayName(m)).Distinct().ToList();
            missileNames.Sort();

            int moddedCount = 0;

            Dictionary<string, int> shipOrder = new Dictionary<string, int> {
                { "Corvette1", 1 },
                { "Frigate1", 2 },
                { "Destroyer1", 3 },
                { "SmallCarrier1", 4 },
                { "AssaultCarrier1", 5 },
                { "FleetCarrier1", 6 },
                { "LandingCraft1", 7 }
            };

            var sortedShips = ships.OrderBy(s => shipOrder.TryGetValue(s.name, out int o) ? o : 99).ToList();

            foreach (var ship in sortedShips)
            {
                if (ship.unitPrefab == null) continue;
                
                var prefabLaunchers = ship.unitPrefab.GetComponentsInChildren<MissileLauncher>(true);
                if (prefabLaunchers.Length == 0) continue;

                int order = shipOrder.TryGetValue(ship.name, out int o) ? o : 99;
                // Only add a prefix if it's one of the main ordered ships, otherwise let it group as "Other"
                string prefix = order <= 7 ? $"{order}. " : "";
                string pName = ship.unitPrefab != null ? ship.unitPrefab.name : ship.name;
                string configCategory = prefix + (string.IsNullOrEmpty(ship.unitName) ? pName : $"{ship.unitName} ({pName})");

                ShipConfigs[ship.name] = new Dictionary<string, LauncherConfig>();

                List<LauncherInfo> launcherInfos = new List<LauncherInfo>();
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
                        Path = GetRelativePath(prefabLaunchers[i].transform, ship.unitPrefab.transform),
                        OriginalMissileName = mName,
                        OriginalMaxAmmo = calculatedAmmo
                    });
                    
                    if (!missileNames.Contains(mName))
                    {
                        missileNames.Add(mName);
                    }
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

                    configMissile.SettingChanged += (sender, args) => ApplyToShip(ship);
                    configAmmo.SettingChanged += (sender, args) => ApplyToShip(ship);

                    // Map all paths in this group to the same Configs
                    foreach (var info in group)
                    {
                        ShipConfigs[ship.name][info.Path] = new LauncherConfig
                        {
                            MissileName = configMissile,
                            MaxAmmo = configAmmo
                        };
                    }
                    groupIndex++;
                }

                // Run Apply once to set up the default prefabs with any saved configs!
                ApplyToShip(ship);

                moddedCount++;
            }

            Logger.LogInfo($"Naval Loadout initialized. Found {ships.Length} ships, modded {moddedCount}. Found {allMissiles.Count} missiles.");
        }

        public void ApplyToShip(ShipDefinition shipDef)
        {
            if (shipDef == null) return;
            string sName = shipDef.name;

            if (ShipConfigs.TryGetValue(sName, out var launcherConfigs))
            {
                // 1. Update Prefab (for Encyclopedia and newly spawned ships)
                if (shipDef.unitPrefab != null)
                {
                    var prefabLaunchers = shipDef.unitPrefab.GetComponentsInChildren<MissileLauncher>(true);
                    for (int i = 0; i < prefabLaunchers.Length; i++)
                    {
                        string path = GetRelativePath(prefabLaunchers[i].transform, shipDef.unitPrefab.transform);
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
                            // Also need to set the Weapon's internal ammo because OnEnable sets maxAmmo = ammo
                            Traverse.Create(prefabLaunchers[i]).Field("ammo").SetValue(config.MaxAmmo.Value);
                        }
                    }
                }

                // 2. Update Active Ships in scene
                foreach (var activeShip in FindObjectsOfType<Ship>())
                {
                    if (activeShip.definition != null && activeShip.definition.name == sName)
                    {
                        var activeLaunchers = activeShip.gameObject.GetComponentsInChildren<MissileLauncher>(true);
                        for (int i = 0; i < activeLaunchers.Length; i++)
                        {
                            string path = GetRelativePath(activeLaunchers[i].transform, activeShip.transform);
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
                                
                                // Call Rearm to update visuals and ammo
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
