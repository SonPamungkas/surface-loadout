<img width="872" height="491" alt="Untitled" src="https://github.com/user-attachments/assets/129a46c3-07a5-4ac9-831e-b5bf450c09ff" />

# Surface Loadout

Reconfigure the missile loadout of every surface unit in the game. Ships,  vehicles, and buildings that carry missile each get their own configuration section, where you can swap the missile type and set the ammo count per launcher. Changes apply immediately, to units already in the world as well as to everything spawned afterwards.

# Feature
- **Every surface unit is covered.** Units are discovered from the game's `Encyclopedia` at startup, so ships, vehicles, and structures are all picked up automatically. Aircraft and missiles are excluded. Units added by other mods are supported without any extra configuration.
- **Missile Type.** A dropdown listing every projectile registered in the game, including modded ones. Selecting a missile also carries across its `WeaponInfo`, so range, guidance, and proximity fuse behaviour follow the missile you picked.
- **Max Ammo.** Set the ammo count for a launcher group. Launchers that report no ammo are measured from their launch transforms, or from their cell grid, so tube and cell launchers get a sensible default.
- **Symmetrical launchers grouped.** Launchers sharing the same missile and ammo count are bound to a single pair of settings, so a ship with matched port and starboard mounts is configured once rather than mount by mount.
- **Live application.** Editing a setting rewrites the prefab and every matching unit currently in the world, then resynchronises the weapon station so ammo counts, kill rewards, supply demand, and the map rearm overlay all reflect the new loadout.
- **Settings backup.** Every setting is mirrored to `SurfaceLoadout/<section>/<key>.json` next to the plugin and restored on startup, so a wiped or regenerated config file does not lose your loadouts.
- **Blueprinter 2.0 compatibility & awareness.** Initialization waits for Blueprinter to finish registering its bundle content before binding settings, and rescans from the loadout menu for anything that arrives later. Blueprinter is a soft dependency — the mod runs normally without it.
