# Tacticool Marines

A companion mod for **Sons of the Forest**. Revive the injured tactical soldiers
scattered around the island. They then work like Kelvin, follow you into caves
and fight with rifles.

Works in single player, as a co-op host and on dedicated servers. **Other players
do not need the mod**: the game replicates the soldiers like any other character.

## Features

- **Revive the soldiers** lying at the crash sites. Once revived they take the
  same orders as Kelvin: fetch logs and materials, finish buildings, maintain
  the base, clear areas, follow you or stay put.
- **They keep working** until there is nothing left to do, instead of taking
  breaks. Optional auto jobs let idle soldiers collect logs, sticks and rocks
  into nearby holders.
- **Combat:** soldiers fight hostile cannibals and mutants with rifles. They
  notice enemies, aim, fire in bursts, reload, take cover, back off, chase and
  help each other. Damage is like the player's rifle.
- **Health:** soldiers take damage and react to hits. By default they cannot
  die: at no health they go down and get up again after a while, or sooner if
  you help them up.
- **Caves:** soldiers following you walk into caves with you, follow you inside
  and come out with you. They climb onto or vault over knee-high obstacles.
- **Variety:** each soldier gets his own uniform colours (standard, woodland,
  desert, black, urban) and gear (helmet, body armour, sunglasses). His look
  stays the same across saves.
- **Optional rifle flashlight:** lights up in the dark while the rifle is in his
  hands.
- **Server tools:** chat commands for owners, cleanup of duplicate characters,
  and performance measuring.

## Requirements

[RedLoader](https://github.com/ToniMacaroni/RedLoader) must be installed:
- `version.dll` (on a server: `winhttp.dll`) and the `_Redloader` folder go in the
  game or server folder.
- `doorstop_config.ini` must say `enabled = true`.

## Installing

1. Copy the `Mods` folder from the zip into the game folder (on a server, into
   the server folder), overwriting existing files.
2. Start the game. A server has to be restarted, because a mod cannot be swapped
   while the server is running.

| Package | For |
| --- | --- |
| `TacticoolMarines_<version>.zip` | Single player and co-op host |
| `TacticoolMarines_<version>_Server.zip` | Dedicated server (no character models, nothing is drawn there) |

**Updating from LivingSoldiers:** delete `Mods/LivingSoldiers.dll` and the
`Mods/LivingSoldiers/` folder, otherwise both mods load. Your settings are taken
over from `CharacterReplacement.cfg`, and your soldiers stay in your saves.

**Linux server under Wine:** set this before launching, otherwise Wine uses its
own `winhttp.dll` and RedLoader never starts:

```
WINEDLLOVERRIDES=winhttp=n,b
```

## Settings

To change settings in game, open RedLoader's **Mod Settings** menu and choose
**TacticoolMarines**. They are grouped under Soldiers, Health, Combat, Work and
Debug. Everything is also in `UserData/TacticoolMarines.cfg`.

## Commands

Typed in chat by a server owner (your Steam ID must be in `ownerswhitelist.txt`).
`/tmhelp` shows an overview in game. The full list is in
[docs/COMMANDS.md](docs/COMMANDS.md).

| Command | What it does |
| --- | --- |
| `/tmhelp [topic]` | Command overview, or the details of one area |
| `/tminfo` | How many soldiers exist and how many are revived |
| `/tmspawn` | Spawn a soldier in front of you |
| `/tmjobs` | Auto jobs on/off and their settings |
| `/tmbuild` `/tmmaintain` `/tmclear` `/tmget` | Order everyone to work |
| `/tmfollow` `/tmstay` | Follow you, or stay put |
| `/tmbreaks on\|off` | No breaks: they keep working |
| `/tmrange` `/tmstruct` | Search radius for materials, or for structures |
| `/tmclean` | Remove the bodies of killed soldiers |
| `/tmremove` | Soldier overview and removal, including leftover duplicates |
| `/tmperf` `/tmprofile` | Server performance and a per-frame breakdown |

In single player, use the F1 console:
- `adddebugsoldier` spawns a soldier in front of you.
- `tmcombat status|on|off` shows or switches combat.

## Compatibility

- **ImmortalCompanions:** works alongside it. That mod then protects only the
  real Kelvin and Virginia, and the soldiers follow this mod's own health
  settings.

## Building from source

Requires the .NET 8 SDK. The project references the assemblies that RedLoader
generates in your own installation, so point `GRoot` at your `_Redloader` folder:

```
dotnet build src/TacticoolMarines.csproj -c Release -p:GRoot="<game folder>\_Redloader"
```

The output targets `net8.0`, but it is loaded by RedLoader's .NET 6 runtime. The
assembly declares the matching target framework attribute itself.

## Credits

Based on the **Living Soldiers** mod by Toni Macaroni, who also wrote
[RedLoader](https://github.com/ToniMacaroni/RedLoader). The soldier models are
the game's own.
