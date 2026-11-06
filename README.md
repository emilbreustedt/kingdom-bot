# KingdomBot

A BepInEx mod that plays **Kingdom: Classic** (Steam, 2015) on its own — built to hunt the game's
day-based achievements. It reads the game state directly (no screen scraping), replaces the
controller input with its own, and runs a rule-based strategy at up to 32× game speed.

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
- **Game speed** 1×–16× (only while playing normally; menus and the crown slow-mo stay untouched)
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
   Speed = 32
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
| `early` | day 1: 8 archers (map reroll if too poor) → days 1–4 deer hunting → days 4–7 clearing | First Day Army, Fourth Day Feast, Cleared an Acre |
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

**Kingdom: Classic** — 24/34 achievements on the author's account, most of them earned by the bot:
Second Day Gift, Third Day Fire, Ninth Day, Cleared an Acre, Day V … Day XCV (best run: day 98).
Portal assault works end to end (stone tech → castle level 5 → knights → border charges; portals do get destroyed),
but the late-game economy is still too weak for *Safe in X* and *Day C*. Pacifist, First Day Army (needs ~25 coins on
day one — maps rarely have them) and Fourth Day Feast are open.

**Kingdom: New Lands** (`newlands/`) — the Classic strategy runs via a small compatibility layer (`Compat.cs`);
first achievements earned: Day V/X/XV, Five Days, Ninth Day, Second Day Gift. Boat escape (wreck → wharf → beach,
land transition via the map) is implemented but not completed yet. See `docs/new-lands.md`.

## License

MIT
