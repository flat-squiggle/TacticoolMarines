# Tacticool Marines

A companion mod for **Sons of the Forest**. Revive the injured tactical soldiers
scattered around the island. They then work like Kelvin, follow you into caves
and fight with rifles.

Works in single player, as a co-op host and on dedicated servers. **Other players
do not need the mod.**

## Features

- **Work:** revived soldiers take Kelvin's orders (build, maintain, clear, fetch,
  follow, stay) and keep working without breaks. Optional auto jobs collect logs,
  sticks and rocks into nearby holders.
- **Combat:** soldiers fight cannibals and mutants with rifles. They notice
  enemies, fire in bursts, reload, take cover and help each other. Their damage
  is like the player's rifle.
- **Health:** soldiers take damage. By default they go down instead of dying and
  get up again, or sooner if you help them up.
- **Caves:** followers enter caves with you, follow your trail and climb over
  obstacles.
- **Variety:** each soldier gets his own uniform colours and gear, kept across
  saves. An optional rifle flashlight lights up at night.

## Installing

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader).

1. Download a zip from [Releases](../../releases):
   - `TacticoolMarines_<version>.zip` for single player and co-op hosts.
   - `TacticoolMarines_<version>_Server.zip` for dedicated servers.
2. Copy its `Mods` folder into the game or server folder.

**Updating from LivingSoldiers:** delete `Mods/LivingSoldiers.dll` and the
`Mods/LivingSoldiers/` folder. Your settings and soldiers carry over.

**Linux server under Wine:** set `WINEDLLOVERRIDES=winhttp=n,b`, otherwise
RedLoader does not start.

## Settings and commands

Settings are in RedLoader's **Mod Settings** menu under **TacticoolMarines**, or
in `UserData/TacticoolMarines.cfg`.

Server owners type commands in chat. The main ones:
- `/tmhelp`: the command overview
- `/tmfollow`, `/tmstay`, `/tmbuild`: orders for everyone
- `/tmremove`: removing soldiers and leftover duplicates
- `/tmperf`: server performance

In single player, `adddebugsoldier` in the F1 console spawns a soldier. The full
list is in [docs/COMMANDS.md](docs/COMMANDS.md).

## Building

```
dotnet build src/TacticoolMarines.csproj -c Release -p:GRoot="<game folder>\_Redloader"
```

## Credits

- **Living Soldiers** by **Toni Macaroni**, who also wrote
  [RedLoader](https://github.com/ToniMacaroni/RedLoader).
- **[LivingSoldiers-rebooted](https://github.com/Gaiser147/LivingSoldiers-rebooted)**:
  the rebuilt source, dedicated server support and server administration.
- **Tacticool Marines** by **flat_squiggle**, developed together with
  **Claude** (Anthropic's AI). It adds combat, health, cave following, soldier
  variety and the in-game settings.

Unofficial; not affiliated with the original authors or Endnight Games.
