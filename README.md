# KingdomBot

A BepInEx mod that plays **Kingdom: Classic** (Steam, 2015) on its own — built to hunt the game's
day-based achievements. It reads the game state directly (no screen scraping), replaces the
controller input with its own, and runs a rule-based strategy at up to 16× game speed.

> Single-player only. Use at your own discretion. Not affiliated with Raw Fury or Noio.
> This repository contains **no game code or assets** — the build derives a compile-time
> reference from your own installation.

## Features

- **Input takeover** via a Harmony patch on `Player.ReceiveInput` (move, gallop, pay, drop coin)
- **State export** every second to `BepInEx/KingdomBot/state.json` (day, coins, units, walls, portals, enemies, payables, achievement counters/flags)
- **Command queue** for remote control/debugging (`BepInEx/KingdomBot/cmd.txt`)
- **Strategy ("Brain")**: campfire → recruit beggars → bows/hammers → walls on both sides → castle upgrades, wall upgrades, towers; retreats at night, evades enemies, interrupts plans when it gets dark
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
4. Start the game:
   ```powershell
   .\start.ps1             # windowed, moved to the widest screen
   .\start.ps1 -Minimize   # keeps running minimized
   ```

## Controls

| Key | Action |
|-----|--------|
| F8  | bot on/off (takes over the controls) |
| F9  | automatic strategy on/off |
| F10 | game speed 1× → 2× → 4× → 8× → 16× |

### Commands (`BepInEx/KingdomBot/cmd.txt`, polled twice per second)

```
bot on | bot off | auto on | auto off | clear
speed 16
mode main | mode fire3
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

## Status

Survives ~15–20 days reliably. Reaching day 35–60 and the *Safe in X* achievements (destroying all
portals) needs a stronger mid-game (farms, multiple wall rings, knights) — contributions welcome.

## License

MIT
