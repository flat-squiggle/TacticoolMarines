# Changelog

## 1.43.0

- Everything is in English now: chat replies, `/tmhelp`, console output and
  the log. `/tmremove all` replaces `alle`, and `/tmprofil` is now
  `/tmprofile`.

## 1.42.0

- All commands renamed from `ls...` to `tm...` to match the new name: chat
  commands `/tmhelp`, `/tminfo`, `/tmspawn`, `/tmfollow`, `/tmremove` ... and the
  console commands `tmcombat`, `tminfo`, `tmrange`, `tmlook`. The old `ls`
  names no longer work (the `/lspawn` typo alias is gone too).

## 1.41.1 - first release as Tacticool Marines

Based on Living Soldiers. Everything below is new or reworked compared to it.

- **Combat:** soldiers fight hostile cannibals and mutants with the game's rifle,
  using the player's rifle animations. They have reaction time and awareness,
  fire in bursts, reload, take cover, back off, chase and support each other.
  Bullet impacts cause real hit reactions, blood and body-part damage, like the
  player's rifle. They don't cower when guns go off.
- **Health:** soldiers take damage with hit reactions. By default they cannot
  die: they go down with the game's own injured animation and get up after a
  while. Works with ImmortalCompanions.
- **Following:** they resume following or their work order after a fight. A
  watchdog restarts following when the game silently drops it.
- **Caves:** soldiers follow you into caves (with the entrance and exit
  animations), along your trail inside and back out. They climb onto or vault
  over knee-high obstacles, also outside caves.
- **Looks:** each soldier gets his own colour scheme and gear, kept across saves.
  The model fit is improved: hands, face, the vest no longer clipping through the
  jacket, the axe shown while chopping, and the rifle slung on the back.
- **Rifle flashlight** (optional).
- **Fixes:** extra soldiers no longer pile up as plain Kelvins on every load.
  Left-over duplicates are removed automatically.
- **Settings:** the in-game settings are grouped, with pickers and sliders.
- **Renamed** to Tacticool Marines. Settings move from `CharacterReplacement.cfg`
  to `TacticoolMarines.cfg` automatically, and existing saves keep their
  soldiers.
