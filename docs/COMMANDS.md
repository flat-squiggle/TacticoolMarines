# Command reference

Typed in chat by a server owner — your Steam ID has to be listed in
`ownerswhitelist.txt`. `/tmhelp` prints the same overview in game.

## Soldiers

| Command | What it does |
| --- | --- |
| `/tminfo` | Status: how many soldiers exist, how many are revived |
| `/tmspawn` | Spawn a soldier directly in front of you |
| `/tmlook` | Re-roll the appearance of all soldiers |

## Work

| Command | What it does |
| --- | --- |
| `/tmjobs` | Auto jobs on/off and current settings |
| `/tmbuild` | Tell everyone to finish buildings |
| `/tmmaintain` | Tell everyone to maintain the base |
| `/tmclear` | Tell everyone to clear the area |
| `/tmget` | Tell everyone to fetch materials |
| `/tmfollow` | Everyone follows you |
| `/tmstay` | Everyone stays where they are |
| `/tmbreaks on\|off` | No breaks — they keep working until there is nothing left |

`/tmfollow` and `/tmstay` put the auto jobs on hold. They resume once you give a
work command again.

## Search ranges

| Command | What it does |
| --- | --- |
| `/tmrange <number>` | Radius for trees and materials lying around |
| `/tmstruct <number>` | Radius for build sites, holders and repairs |

`1` is the game's own value, `2` doubles it. Both take effect immediately, no
restart needed. Large values cost server performance, because every search scans
a bigger area every time.

## Cleanup

| Command | What it does |
| --- | --- |
| `/tmclean on\|off\|now` | Remove the bodies of killed soldiers |
| `/tmclean <seconds>` | How long a body stays before it is removed |
| `/tmremove` | Overview: total, managed by the mod, surplus |
| `/tmremove dupes` | Remove the surplus ones only |
| `/tmremove <number>` | Remove that many, farthest from any player first |
| `/tmremove <name>` | Remove one specific soldier |
| `/tmremove all` | Remove every soldier |

Removal is permanent and survives a restart. The story Kelvin is never removed —
he is identified by the lowest actor id, since he exists from the moment the
world is created.

Run `/tmremove dupes` at least a minute after the server started. Before that the
mod has not read its own soldiers yet, and every soldier would count as surplus.
It refuses in that case and says so; `/tmremove dupes force` overrides it.

## Measuring

| Command | What it does |
| --- | --- |
| `/tmperf` | Server FPS, CPU, RAM, network, number of AI actors |
| `/tmprofile` | Breakdown of one server frame in milliseconds |

Both measure in one-minute windows, so right after a restart they have nothing to
report yet.

`/tmprofile` covers the game's own AI world simulation stage by stage, the network
layer's internal timings (reading, sending, visibility, entity simulation,
events), how many physics steps run per frame, and how many build pieces the base
consists of.

## Combat (console, F1)

Typed in the F1 console without a slash (or in chat as `/tmcombat ...` on a
server). Most settings are easier to change in the game's mod settings.

| Command | What it does |
| --- | --- |
| `tmcombat status` | Combat settings and every soldier currently fighting |
| `tmcombat on` / `off` | Combat on or off |
| `tmcombat debug [on\|off]` | Detailed combat log (spotting, moves, cover, reloads, each shot) in `_Redloader/Latest.log` |
| `tmcombat set <key> <value>` | Change a value live, saved to the settings file |

Keys for `tmcombat set`:

| Key | Setting | Meaning |
| --- | --- | --- |
| `range` | `combat_range` | Engagement range (m) |
| `rate` | `combat_fire_interval` | Seconds between shots; bursts and pauses scale with it |
| `accuracy` | `combat_accuracy` | Hit chance (0-1) |
| `headshot` | `combat_headshot_chance` | Share of hits aimed at the head (0-1) |
| `burst` | `combat_burst` | Most shots per burst |
| `mag` | `combat_magazine` | Shots per magazine, 0 = no reloading |
| `reload` | `combat_reload_time` | Seconds per reload |
| `reaction` | `combat_reaction` | Seconds from spotting to the first shot |
| `move` | `combat_move` | Reposition during a fight (on/off) |
| `cover` | `combat_cover` | Prefer positions behind trees, rocks, walls (on/off) |
| `mindist` | `combat_min_distance` | Back off from enemies closer than this (m) |
| `retreat` | `combat_retreat_health` | Fall back towards the player below this share of health |
| `immortal` | `soldier_immortal` | Soldiers are downed instead of killed (on/off) |
| `soldierdamage` | `soldier_damage` | Soldiers take damage (on/off) |
| `downtime` | `soldier_downed_time` | Seconds a downed soldier lies before getting up |

`combat_targets` (settings file only) is `auto` by default: every type the game
classes as cannibal or creepy (mutant), minus Kelvin, Virginia and Timmy. Ids or
names can be added or removed: `auto,-18` or `auto,Moose`.
