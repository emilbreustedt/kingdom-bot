# Kingdom: New Lands — reverse-engineering notes

Status: analysis only (no plugin yet). Planned after Kingdom: Classic is complete.

## Engine

| | Classic | New Lands |
|---|---|---|
| Unity | 5.3.4 | 2017.1.2 |
| Architecture | x86 | x86 |
| Scripting | Mono | Mono (no IL2CPP) |
| Exe | `Kingdom.exe` | `Kingdom.exe` (same name! scripts must match by path) |

→ BepInEx 5 x86, Harmony and the same hook points should work. Many fields are public in New Lands.

## Input

`Player.IControllable.ReceiveInput(Rewired.Player)` reads the axis/buttons and calls
`Player.UpdateActionState(direction, startSprint, stopSprint)` and `Player.UpdatePayState(payKey, payKeyDown)` —
identical to Classic, so the input hook and the command queue can be shared.

## Achievements (34)

Data-driven: `AchievementManager.achievements[]` → `Achievement.requirements` (list of `Stat` + `StatComparison` + value),
checked in `HandleOnStatChanged`. Thresholds must be read at runtime from the assets.

`Stat` enum: Day, Archers, Beggars, Farmers, Knights, Peasants, Trolls, Workers, HuntedDeers, KilledEnemies,
DestroyedPortals, ClearedLand, FreeWallBuilt, LitFire, Galloped, CoinDroppedOnGround, CoinFellOut, Won,
NumberOfGamesStarted, GameProgress, Reign, Land, BiggestStash, CoinsSpent, FastestEscapeLandOne..Six, IntroDone,
BowsBought, HammersBought, WallsBuilt, Bakers, Bankers, Bosses, Dogs, Hermits, Merchants, Mules, …

| Steam ID | Description |
|---|---|
| ON_THE_FIRST_DAY … THE_TENTH_DAY | same ten "day" achievements as Classic (Fourth Day: 20 deer by day 4; Tenth Day: destroy **a** portal by day 10) |
| SURVIVE_DAY_5 … SURVIVE_DAY_100 | 5,10,…,50,60,70,80,90,100 |
| SPEEDRUN_LAND_ONE … FIVE | build the boat and escape land N before day 10/15/20/25/30 |
| STREAK_1 / 3 / 5 | Birth of a Reign (win a game), Interior Decoration (antlers on the coat of arms), Heraldic Achievement (reign over five lands) |
| GAME_FINISHED | Crowned — escape the final land |

## New mechanics relevant for the bot

- **Boat**: `Wharf` (payable) → workers build it from parts (`BoatPart`, `workPerPart`); `Boat.State` Pushing / WaitingForPlayer / Sailing; carries archers, workers, knights (`maxKnights`). Escaping a land = boarding the boat.
- Lands `One`..`Six`, `Map`, `UnlockManager` (unlocks persist between games), hermits, baker, banker, dog, ballista, seasons, horde/boss.
