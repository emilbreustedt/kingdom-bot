# KingdomBot

A BepInEx mod that plays **Kingdom: Classic** (Steam, 2015) on its own — built to hunt the game's
day-based achievements. It reads the game state directly (no screen scraping), replaces the
controller input with its own, and runs a rule-based strategy at 16× game speed by default (32× is possible, but movement gets imprecise).

> Single-player only. Use at your own discretion. Not affiliated with Raw Fury or Noio.
> This repository contains **no game code or assets** — the build derives a compile-time
> reference from your own installation.

## Features

- **Input takeover** via a Harmony patch on `Player.ReceiveInput` (move, gallop, pay, drop coin)
- **State export** every second to `BepInEx/KingdomBot/state.json` (day, coins, units, walls, portals, enemies, payables, achievement counters/flags)
- **Command queue** for remote control/debugging (`BepInEx/KingdomBot/cmd.txt`)
- **Strategy ("Brain")**: campfire → recruit beggars → bows/hammers → walls on both sides → castle upgrades, wall upgrades, towers; retreats at night, evades enemies, interrupts plans when it gets dark
- **Run modes + `auto` campaign**: reads which achievements are still missing on Steam (read-only — the bot never unlocks anything itself) and picks the next run type; short special runs restart automatically once their goal is reached or out of reach
- **Portal assault** (master mode): mason shrine → stone, castle up to shield shops, knights, border-banner charges
- **Achievement routines**
  - *On the Second Day I Got a Gift* — castle level 2 before the achievement day counter reaches 2 (needs a free wall foundation within 12 units)
  - *On the Third Day I Lit Up a Fire* — `mode fire3`: survive two nights without a camp, light the fire on day 3
  - *On the Ninth Day I First Ran* — no gallop before day counter 8
  - *Survive past day X* — long runs
- **Game speed** 1×–32× (default 16×) (only while playing normally; menus and the crown slow-mo stay untouched)
- Auto-skips the intro, auto-restarts after game over, runs windowed/minimized

## Requirements

- Windows, Kingdom: Classic (Steam), Steam running
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) **x86** (Kingdom is a 32-bit Unity 5.3 game)
- Nothing else: the build uses the C# compiler that ships with Windows (.NET Framework 4)

## Installation

1. Extract `BepInEx_win_x86_5.4.x.zip` into the Kingdom game folder.
2. **Important for Unity 5.3:** in `BepInEx/config/BepInEx.cfg` set
   ```ini
   [Preloader.Entrypoint]
   Type = Camera
   ```
   (start the game once so the config exists). With the default `Application` entrypoint the game
   hangs on the loading screen ("referenced script is missing").
3. Build and install the plugin:
   ```powershell
   .\build.ps1
   ```
   The game folder is found automatically (all Steam libraries) or via `$env:KINGDOM_PATH`.
   On first build `tools/publicize.ps1` creates `lib/Assembly-CSharp.publicized.dll` from your installation.
4. Optional settings in `BepInEx/config/emil.kingdombot.cfg` (created on first start):
   ```ini
   [General]
   Mode = auto          # auto | main | early | challenge
   Speed = 16
   AutoStart = true
   AutoRestart = true
   ```
5. Start the game:
   ```powershell
   .\start.ps1             # windowed, moved to the widest screen
   .\start.ps1 -Minimize   # keeps running minimized
   ```

To update the bot **without losing the current run** use `.\deploy.ps1` (save & quit → build → start; the game reloads its `AUTO` save).

## Run types

You normally never pick anything: with `Mode = auto` the bot reads which achievements are still missing on Steam and
plays the right kind of run, restarting automatically until everything of that kind is unlocked (or after 5 failed tries).

| Run type | Phases (switch by day) | Achievements |
|---|---|---|
| `challenge` | days 1–2 no camp (dodge) → light the fire on day 3 → no kills / no coin on the ground until day 8 (walls only, coins handed straight to beggars) | Third Day Fire, Five Days, Eighth Day |
| `early` | day 1: 8 archers (map reroll if no chest is near) → days 1–4: forest wall beyond the deer, 10th deer on day 4 | First Day Army, Fourth Day Feast, Cleared an Acre |
| `main` | record defence + farms → stone, castle level 5, knights, border charges | Second Day Gift, Ninth Day, Day V … C, Safe in 25–40, Tenth Day, win |

Old mode names still work (`master`/`survive` → main, `fire3`/`pacifist` → challenge, `army1`/`feast`/`acre` → early).

## Controls

| Key | Action |
|-----|--------|
| F8  | bot on/off (takes over the controls) |
| F9  | automatic strategy on/off |
| F10 | game speed 1× → 2× → 4× → 8× → 16× → 32× |

### Commands (`BepInEx/KingdomBot/cmd.txt`, polled twice per second)

```
bot on | bot off | auto on | auto off | clear | achievements | savequit
speed 16
mode auto | mode main | mode early | mode challenge
newgame
goto X [tol] [timeout]     walkto X [tol] [timeout]
payid <instanceId> [x]     pay      tap N      wait S      shot
```

## How it works

| File | Purpose |
|------|---------|
| `src/Plugin.cs` | BepInEx plugin, persistent runner object, input hook, command queue, speed control |
| `src/Brain.cs` | strategy and achievement logic |
| `src/StateDump.cs` | JSON world snapshot |
| `src/Acc.cs` | reflection helpers — Unity 5.3's Mono enforces member visibility at runtime |

Notes from reverse engineering (Kingdom: Classic 1.x):

- Achievement *flags* are only checked the first time they are set in a run (`AchievementManager.SetFlag`).
- The achievement day counter switches at `dayStart` (dawn), not at midnight.
- Castle upgrades have a cooldown (~230 game seconds after level 1). Level 2 builds free walls
  (closest foundation per side within 12), level 4 upgrades walls/towers, level 5 adds shield shops.
- Taps longer than one frame count as "hold" at high time scales.
- Beggars only pick up coins dropped by the player; coins knocked out of the purse by a hit count as dropped too.
- Knights: castle level 5 → shield shops → an archer takes the shield. Paying the border banner sends every ready knight
  (with archers) to charge the closest portal on that side. Destroying the last inner portal spawns the final wave;
  defeating it wins the game.

## Status

**Kingdom: Classic** — 31/34 achievements on the author's account, almost all earned by the bot, including
Day C (day 100), Safe in 35/40, The Tenth Day and the Fourth Day Feast. First Day Army earned 2026-11-07 (32/34). Open: Safe in 25, Safe in 30.

What finally worked (all verified in the game, see the git history for details):

- **Early army (`boot` phase, days 0-2):** loot chests, recruit whole camps, buy bows - ~20 archers by day 10.
- **Portal assault:** castle 5 → shields → knights; charges only on a side with a payable banner *and* a ready
  knight; knights get re-targeted when their target portal is already dead and switch sides once their side is clear.
  Before the last portal the bot rebuilds the army (16 archers) and keeps a small bank for the final wave.
- **Pitfalls found:** destroyed portals only crumble after their spawn queue drains (the last one of a side gets the
  nightly waves; ~4-7 days); big enemies throw catapult boulders back (catapults are off); never save & quit during
  the final wave (the reload starts a new game - `deploy.ps1` and the plugin refuse it).
- **Fourth Day Feast:** the 10th deer must fall *on* day 4. Deer only flee 2.5 units and always return to their
  habitat, so herding them is hopeless - instead a wall is built just beyond the nearest deer (clearing trees for it if
  needed); the archers then hunt them inside the kingdom.
- **First Day Army:** ~10 ground coins + one chest per side (8 coins each; one usually 40-90 out, the other 100-215);
  8 archers need ~25 (8 recruits, 8 bows at 2, the fire). A coin ledger (`COIN LEDGER` in the log) showed where coins
  went. Bugs found that way:
  - at 32x speed `goto` stopped up to 2.5 units short, so chests never opened and coins/beggars right next to the
    king were never touched. Now the game slows down for the last few units.
  - the game pools coins (LeanPool), so permanent per-coin cooldowns made the bot ignore reused coins forever.
  - the map-reroll cap was a lifetime counter, so maps without a near chest were played.
  Day 0 is only ~15 h of daylight at ~25 units/h. The far chest costs the whole day. The day counter stays 0 until
  dawn, so evening archer coins are spent on a night trip to a camp. The merchant (always there on day 0, 4 coins)
  refills a *random* shop: bows = 4 free bows, hammers = builders. The bot rerolls the map in the hammer case.
  Template objects (`Chest` at x0 without parent, inactive pool coins) must be ignored.
  **Earned** after: night recruiting up to 75 out, collecting archer coins at night, pre-buying bows at night
  (dawn results then 4-8 archers; the 8 came on the 10th try).

- **Safe in 25/30 (open) - main-run economy and assault fixes (2026-11-07):** daily coin ledger showed ~5 coins/day.
  Fixed: castle chest (5-7 coins/day) was emptied once per run; the king now collects from archers hunting outside
  the walls and sweeps tax coins next to him; horse grazes (2 s on grass = 48 s gallop without stamina); 3-4 builders
  instead of archers/3; scythes for built farms; max 22 archers until castle 5; the purse is kept below ~30 (the bag
  spills at ~39 and half of the spilled coins fall into the water); payments abort when more than price+3 flow out;
  goto timeouts 20-40 s (90 s = 9 game hours stuck); charges may start at dawn (knights march ~13 units/h); the main
  map check runs again (boot phase skipped it) and requires every inner portal within 185.
  Best runs: castle 5 on day 11-14, all portals down on day 25, final wave lasted until day 31.

- **Safe in 25/30, evening of 2026-11-07:** typical run now: castle 5 on day 11-15, 1-2 portals by day 24 (pace end).
  Added after a user-provided guide and live observations: early farm (day 5) with upgrade, wall beyond it and 6
  farmers; farmer/archer tax trips; recruiting whole camps and in passing; night shopping; crown guard checked before
  every purchase; knights armed to 5 coins, 2 per side, shields on the charge side; charges only when they end before
  dusk; forward walls only for portals > 130 beyond the wall; intruder avoidance first. Open problem: about 2/3 of all
  archers die (mostly charge escorts at the portals at dusk), and the late economy is thin (7-12 coins/day).

**Kingdom: New Lands** (`newlands/`) — the Classic strategy runs via a small compatibility layer (`Compat.cs`);
first achievements earned: Day V/X/XV, Five Days, Ninth Day, Second Day Gift. Boat escape (wreck → wharf → beach,
land transition via the map) is implemented but not completed yet. See `docs/new-lands.md`.

## License

MIT
