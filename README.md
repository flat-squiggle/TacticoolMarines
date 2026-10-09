# Tacticool Marines

Revive the injured tactical soldiers scattered around the island in
**Sons of the Forest**. Once recovered they work like Kelvin: they fetch logs and
materials, finish buildings, maintain the base and clear areas. They also follow
you into caves and fight with rifles.

One build covers every situation: the mod detects by itself whether it runs in
single player, on a co-op host or on a dedicated server. **Other players do not
need it.** The game replicates the soldiers like any other character.

---

## Features

**Work**
- Revived soldiers take the same orders as Kelvin: build, maintain, clear,
  fetch, follow, stay.
- With no breaks they keep working until there is nothing left to do.
- Optional auto jobs let idle soldiers collect logs, sticks and rocks into
  nearby holders.
- The search ranges for materials and structures are adjustable.

**Combat**
- Soldiers fight hostile cannibals and mutants with the game's rifle, using the
  player's rifle animations.
- They notice enemies over time, fire in bursts, reload, take cover, back off,
  chase and help each other.
- Damage works like the player's rifle, including real bullet impacts, hit
  reactions and blood.
- They stay near you while following, hold fire when a friend is in the way,
  and go back to following or their work order after a fight.

**Health**
- Soldiers take damage and react to hits.
- By default they cannot die. At no health they go down with the game's own
  injured animation and get up after a while, or sooner if you help them up.

**Caves**
- Soldiers following you enter caves with the same entrance animation, follow
  your trail inside and come out with you.
- They climb onto or vault over knee-high obstacles, outside caves too.

**Looks**
- Every soldier gets his own uniform colours (standard, woodland, desert, black,
  urban) and gear (helmet, body armour, sunglasses). His look stays the same
  across saves.
- An optional flashlight on the rifle lights up in the dark while the rifle is
  in his hands.

**Servers**
- Owners get chat commands for orders, cleanup and performance measuring.
- Duplicate soldiers left over from older versions are removed automatically.

---

## Packages

| Package | Size | For |
| --- | --- | --- |
| `TacticoolMarines_<version>.zip` | ~60 MB | Single player and co-op host |
| `TacticoolMarines_<version>_Server.zip` | ~150 KB | Dedicated server |

Both packages contain the same DLL. The only difference is the `character` asset
bundle, which holds the soldiers' look. Only a game that draws the soldiers needs
it, so the server package leaves it out.

## Installing

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader):
- `version.dll` (on a server: `winhttp.dll`) and the `_Redloader` folder must be
  in the game or server folder.
- `doorstop_config.ini` must say `enabled = true`.

1. Copy the `Mods` folder from the package into the game or server folder,
   overwriting existing files.
2. Start the game. Restart a server from its panel, because a loaded mod cannot
   be swapped while it is running.

**Updating from LivingSoldiers:** delete `Mods/LivingSoldiers.dll` and the
`Mods/LivingSoldiers/` folder, otherwise both mods load. Your settings are taken
over from `CharacterReplacement.cfg` on the first start, and your soldiers stay
in your saves.

**Linux server under Wine:** set this before launching:

```
WINEDLLOVERRIDES=winhttp=n,b
```

Without it, Wine uses its own `winhttp.dll`, RedLoader never starts, and the mod
is silently missing with no error message.

## Settings

Open RedLoader's **Mod Settings** menu and choose **TacticoolMarines**. The
settings are grouped under Soldiers, Health, Combat, Work and Debug.
Everything is also in `UserData/TacticoolMarines.cfg`.

## Commands

Typed in chat by a server owner (your Steam ID must be in `ownerswhitelist.txt`).
`/tmhelp` shows the same overview in game. The full reference is in
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

## Why the server administration exists

The game does not keep characters that are far from every player as real objects.
It keeps them only as entries in a world simulation. Earlier versions of Living
Soldiers looked for their soldiers among the real objects alone. They found none
at server start, because nobody was near them yet, so they spawned a fresh set
every time while the old ones stayed. After a few restarts a server carried over
a hundred Kelvin-type AI actors. That dropped it to roughly 13 frames per second
and cost connected players more than half their frame rate.

The mod now checks both lists and spawns no duplicates. It picks an existing
soldier back up the moment the game turns him into a real character.
`/tmremove dupes` clears the leftovers from an affected save. Leftovers are also
removed automatically a minute after loading.

## Compatibility

- **ImmortalCompanions:** works alongside it. That mod then protects only the
  real Kelvin and Virginia, and the soldiers follow this mod's health settings.

## Performance

Each soldier near you costs about as much as another Kelvin. Soldiers far away
cost almost nothing. A full squad following you is playable on a reasonable PC,
with dips during big fights. The rifle flashlight adds a spotlight per soldier
at night, so it is off by default.

## Building

You need the .NET 8 SDK. The project references the assemblies that RedLoader
generates in your own installation, so point `GRoot` at your `_Redloader` folder:

```
dotnet build src/TacticoolMarines.csproj -c Release -p:GRoot="<game folder>\_Redloader"
```

The output targets `net8.0`, but it is loaded by RedLoader's .NET 6 runtime. The
assembly declares the matching target framework attribute itself.

---

## Credits

- **Living Soldiers**, the original mod, was created by **Toni Macaroni**, who
  also wrote [RedLoader](https://github.com/ToniMacaroni/RedLoader), the mod
  loader this depends on.
- **[LivingSoldiers-rebooted](https://github.com/Gaiser147/LivingSoldiers-rebooted)**
  rebuilt the source from the released DLL. It added the duplicate-spawn fix,
  dedicated server support and the server administration that this project
  builds on.
- **Tacticool Marines** by flat_squiggle adds combat, health, cave following,
  soldier variety and the in-game settings.
- The soldier models are the game's own.

This is an unofficial project. It is not affiliated with or endorsed by the
original authors or by Endnight Games.
