using System;
using System.Collections.Generic;
using UnityEngine;

namespace KingdomBot
{
    /// <summary>
    /// Strategy layer (v1). Called whenever the CommandQueue is idle; pushes a short plan (1-3 commands)
    /// so that the situation is re-evaluated every few seconds.
    /// </summary>
    public class Brain
    {
        readonly Plugin _pl;
        public string Status = "idle";
        readonly Dictionary<int, float> _cooldown = new Dictionary<int, float>();   // payable/beggar id -> time until which we ignore it
        int _lastCastleUpgradeDay = -1;
        float _lastTap = -100f;
        int _coinsAtTap = -1, _tapFails; float _noRecruitUntil;
        float _gameOverAt = -1f;
        bool _failCounted;
        public bool AutoRestart = true;
        bool _safeMode;
        bool _armyFirst;
        LegacyBrain _legacy;          // current plan is a retreat/home plan -> do not interrupt it
        public int Runs = 0;
        string _lastLogged = "";

        // Tunables
        public float AcreTarget = 120f;      // BY_THE_SEVENTH_DAY: forest gap width by day counter 6
        public int GallopFromDay = 8;        // ON_THE_NINTH_DAY: first gallop must happen at day counter >= 8
        /// <summary>"main": no walls on day 0, castle upgrade on day 1 -> free walls (ON_THE_SECOND_DAY).
        /// "fire3": first campfire only on day counter 2 (ON_THE_THIRD_DAY), survive two nights by evading.</summary>
        public string Mode = "master";        // effective mode of the current run
        public string ConfigMode = "auto";   // auto | main | early | challenge (old names are aliases)
        public string RunType = "main";      // run type of the current run; Mode = phase derived from it every step
        public bool PacifistEnabled = true;
        float _newGameAt = -1f;
        public bool AcreRun = false;       // dedicated run: heavy forest clearing until day 6

        public Brain(Plugin pl) { _pl = pl; }

        void Push(string c) { _pl.Queue.Enqueue(c); }
        bool OnCooldown(int id) { float t; return _cooldown.TryGetValue(id, out t) && Time.time < t; }
        void Cool(int id, float s) { _cooldown[id] = Time.time + s; }

        public bool MaySprint
        {
            get
            {
                if (Mode == "pacifist" || Mode == "fire3" || Mode == "army1") return true;
                if (Campaign.Has(Campaign.NinthDay) == true) return true;   // already unlocked: no need to wait
                return GameState.Day >= GallopFromDay;
            }
        }

        static string ModeFile { get { return System.IO.Path.Combine(Plugin.Dir, "run_mode.txt"); } }

        /// <summary>Old mode names map to the three run types.</summary>
        public static string NormalizeRunType(string m)
        {
            switch ((m ?? "").Trim().ToLowerInvariant())
            {
                case "auto": return "auto";
                case "early": case "army1": case "feast": case "acre": return "early";
                case "challenge": case "fire3": case "pacifist": return "challenge";
                default: return "main";   // main, master, survive
            }
        }

        void ChooseModeForRun(bool continuing)
        {
            string cfg = NormalizeRunType(ConfigMode);
            string saved = null;
            try { if (continuing && System.IO.File.Exists(ModeFile)) saved = NormalizeRunType(System.IO.File.ReadAllText(ModeFile)); } catch { }
            if (cfg == "auto" && !string.IsNullOrEmpty(saved) && saved != "auto") RunType = saved;           // a loaded run keeps its type
            else RunType = cfg == "auto" ? Campaign.PickMode(PacifistEnabled) : cfg;
            try { System.IO.File.WriteAllText(ModeFile, RunType); } catch { }
            UpdatePhase();
            Plugin.Log((continuing ? "Continuing run" : "New run") + " - type: " + RunType + " (phase " + Mode + ") | " + Campaign.Summary());
        }

        /// <summary>
        /// Phase of the current step:
        ///  early     = army1 (day 1: 8 archers) -> feast (10th deer on day 4) -> acre (clearing until day 7) -> master
        ///  challenge = fire3 (no camp until day 3) -> pacifist (no kills until day 6, no coin on the ground until day 8) -> master
        ///  main      = master (record defence + economy + portal assault)
        /// </summary>
        void UpdatePhase()
        {
            var am = Managers.achievements;
            int dayC = am != null && am.counters != null ? am.counters[1] : GameState.Day;
            string phase = "master";
            if (RunType == "main" && Campaign.Missing("SURVIVED_UNTIL_DAY_100")) phase = "survive";   // pure record strategy for Day C
            if (RunType == "early")
            {
                if (dayC == 0 && Campaign.Missing(Campaign.FirstDay)) phase = "army1";
                else if (dayC <= 3 && Campaign.Missing(Campaign.FourthDay)) phase = "feast";
                else if (dayC <= 6 && Campaign.Missing(Campaign.Acre)) phase = "acre";
            }
            else if (RunType == "challenge")
            {
                bool fireLit = am != null && am.flags != null && am.flags[3];
                if (dayC < 2 && !fireLit && Campaign.Missing(Campaign.ThirdDay)) phase = "fire3";
                else if (dayC < 8 && (Campaign.Missing(Campaign.FifthDays) || Campaign.Missing(Campaign.EighthDay))) phase = "pacifist";
            }
            if (phase != Mode) { if (Mode != null) Plugin.Log("Phase: " + Mode + " -> " + phase); Mode = phase; }
        }

        /// <summary>Special runs end as soon as none of their achievements is reachable any more (only in auto).</summary>
        void CheckRunEnd()
        {
            if (NormalizeRunType(ConfigMode) != "auto" || _newGameAt > 0f) return;
            var am = Managers.achievements; if (am == null || am.flags == null) return;
            int dayC = am.counters[1];
            if (RunType == "main")
            {
                // assault phase: after day 40 no "Safe in X" is reachable any more (Day C is already done) -> next attempt
                if (Mode == "master" && dayC > 40 && Campaign.Missing("SAFE_IN_40") && !Campaign.Missing("SURVIVED_UNTIL_DAY_100"))
                {
                    Plugin.Log("Run finished (assault, day " + dayC + ", portals left) -> new game");
                    _newGameAt = Time.unscaledTime + 6f;
                }
                return;
            }
            bool possible;
            if (RunType == "early")
                possible = (dayC == 0 && Campaign.Missing(Campaign.FirstDay))
                        || (dayC <= 3 && am.counters[4] < 10 && Campaign.Missing(Campaign.FourthDay))
                        || (dayC <= 6 && Campaign.Missing(Campaign.Acre));
            else
                possible = (dayC <= 2 && Campaign.Missing(Campaign.ThirdDay) && !(am.flags[3] && dayC < 2))
                        || (dayC < 6 && am.counters[5] == 0 && Campaign.Missing(Campaign.FifthDays))
                        || (dayC < 8 && !am.flags[5] && Campaign.Missing(Campaign.EighthDay));
            if (possible) return;
            Plugin.Log("Run finished (" + RunType + ", day counter " + dayC + ") -> new game | " + Campaign.Summary());
            Campaign.Fail(RunType);      // counts attempts; auto moves on once everything of this type is unlocked or after too many tries
            _newGameAt = Time.unscaledTime + 6f;   // let unlock coroutines finish first
        }

        public void Think()
        {
            ThinkInner();
            if (Status != _lastLogged)
            {
                _lastLogged = Status;
                Plugin.Log(string.Format("[d{0} h{1:0.0} c{2} x{3:0}] {4}", GameState.Day, GameState.Hour, GameState.Player != null ? GameState.Coins : -1, GameState.Player != null ? GameState.PX : 0f, Status));
            }
        }

        int _lastDaySeen = -1;
        void ThinkInner()
        {
            _safeMode = false;
            // stuck guard: a recruitment tap must cost a coin; otherwise pause recruiting
            if (_coinsAtTap >= 0 && GameState.Player != null)
            {
                if (GameState.Coins >= _coinsAtTap) { _tapFails++; if (_tapFails >= 3) { _noRecruitUntil = Time.time + 120f; _tapFails = 0; Plugin.Log("Recruiting does not work - pausing"); } }
                else _tapFails = 0;
                _coinsAtTap = -1;
            }
            int dNow = GameState.Day;
            if (dNow < _lastDaySeen || _lastDaySeen < 0) { _lastCastleUpgradeDay = -1; _cooldown.Clear(); _mapChecked = false; _mainMapChecked = false; ChooseModeForRun(_lastDaySeen < 0 && dNow > 0); }
            if (dNow != _lastDaySeen && Managers.achievements != null && Managers.achievements.flags != null)
            {
                var am = Managers.achievements;
                int begs = UnityEngine.Object.FindObjectsOfType<Beggar>().Length, peas = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
                int arcs = UnityEngine.Object.FindObjectsOfType<Archer>().Length, wrks = UnityEngine.Object.FindObjectsOfType<Worker>().Length, kns = UnityEngine.Object.FindObjectsOfType<Knight>().Length;
                Plugin.Log(string.Format("== POP day {0}: beggars {1} peasants {2} archers {3} (made {4}) workers {5} knights {6} coins {7}", dNow, begs, peas, arcs, am.counters[3], wrks, kns, GameState.Player != null ? GameState.Coins : -1));
                Plugin.Log(string.Format("== Day {0} (mode {1}) Flags: FreeWall={2} LitFire={3} Galloped={4} CoinOnGround={5} PortalsDestroyed={6} | Archers={7} Deer={8} Kills={9} Land={10}",
                    dNow, Mode, am.flags[2], am.flags[3], am.flags[4], am.flags[5], am.flags[7], am.counters[3], am.counters[4], am.counters[5], am.counters[7]));
            }
            _lastDaySeen = dNow;
            if (_newGameAt > 0f && Time.unscaledTime >= _newGameAt && Managers.game != null)
            {
                _newGameAt = -1f; _lastDaySeen = -1;
                _pl.Queue.Clear();
                Acc.Call(Managers.game, "Restart");
                Status = "New game"; Push("wait 3"); return;
            }
            var game = Managers.game;
            var p = GameState.Player;
            if (game == null || p == null) { Status = "no game"; return; }
            if ((int)game.gameState == 3)
            {
                if (_gameOverAt < 0) _gameOverAt = Time.unscaledTime;
                if (AutoRestart && Time.unscaledTime - _gameOverAt > 10f && RunType != "main" && !_failCounted) { Campaign.Fail(RunType); _failCounted = true; }
                if (AutoRestart && Time.unscaledTime - _gameOverAt > 10f) { _failCounted = false; _gameOverAt = -1; Runs++; Status = "Restart (attempt " + (Runs + 1) + ")"; _pl.Queue.Clear(); Acc.Call(game, "Restart"); return; }
                Status = "Game Over"; Push("wait 1"); return;
            }
            _gameOverAt = -1;
            if ((int)game.gameState == 1)
            {
                // the intro waits for a real stick movement (read directly from Rewired) -> trigger it ourselves
                Acc.Call(game, "AbortIntroAndTutorial");
                Status = "Skip intro"; Push("wait 1"); return;
            }
            if ((int)game.gameState != 2) { Status = "waiting (" + game.gameState + ")"; Push("wait 1"); return; }
            if (!p.hasCrown) { Status = "Crown lost!"; Push("wait 1"); return; }

            var k = Managers.kingdom;
            int day = GameState.Day;
            UpdatePhase();
            CheckRunEnd();
            if (Mode == "pacifist") { ThinkPacifist(); return; }
            if (Mode == "army1") { ThinkArmy1(false); return; }
            if (Mode == "feast") { ThinkArmy1(true); return; }   // feast: maximum archers early - they hunt deer on their own in safe daytime
            if (RunType == "main" && GameState.Player != null && (int)Managers.game.gameState == 2 && GameState.Day == 0 && MainMapCheck()) return;
            if (Mode == "master" && GameState.Player != null && (int)Managers.game.gameState == 2)
            {
                // master = record-run defence (LegacyBrain) + portal assault steps on top
                var kk = Managers.kingdom;
                int d = GameState.Day; float hr = GameState.Hour, pxx = GameState.PX, hm = kk.campfirePosition;
                int bud = GameState.Coins - ((d >= 1 && hr >= kk.dayEnd - 8f) ? 4 : 0);
                int arch = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
                bool night = hr >= kk.dayEnd - 1.5f || !kk.isDaytime;
                var en = NearestEnemy(pxx);
                bool danger = en != null && Mathf.Abs(en.transform.position.x - pxx) < 12f && !WallBetween(pxx, en.transform.position.x);
                _armyFirst = false;
                // assault only once the defence is solid (the castle rush + shields cost ~40 coins)
                bool ready = (d >= 12 && arch >= 15) || d >= 20;
                if (ready && !night && !danger && AssaultStep(UnityEngine.Object.FindObjectsOfType<Payable>(), d, hr, bud, arch, hm, pxx)) return;
            }
            // (master now shares the survival basis below; economy/defence extras are no longer used)
            // survival: stone walls decide late survival (L3/L4 = 200/300 HP vs 50 HP). Follow a wall schedule and save for
            // the next step during the day if needed; otherwise the record strategy plays as usual.
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && AvoidIntruder()) return;
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && WallSchedule()) return;
            if (Mode == "survive" || Mode == "master" || Mode == "feast")
            {
                if (_legacy == null) _legacy = new LegacyBrain(_pl);
                _legacy.ThinkInner();
                Status = _legacy.Status; _safeMode = false;
                return;
            }
            float hour = GameState.Hour;
            float px = GameState.PX;
            int coins = GameState.Coins;
            float home = k.campfirePosition;
            float leftSafe = WallEdge(-1, home), rightSafe = WallEdge(1, home);

            // ---------- 1. safety ----------
            // night = not daytime; only exception: the very first morning (no enemies exist yet)
            bool evening = hour >= k.dayEnd - 1.5f || (!k.isDaytime && !(day == 0 && hour < 12f));
            bool dawn = false;
            Enemy nearest = NearestEnemy(px);
            if (nearest != null && Mathf.Abs(nearest.transform.position.x - px) < 18f && !Between(px, leftSafe, rightSafe))
            {
                Status = "Fleeing enemy"; _safeMode = true;
                Push("goto " + F(home)); return;
            }
            if (nearest != null && Mathf.Abs(nearest.transform.position.x - px) < 7f && !WallBetween(px, nearest.transform.position.x))
            {
                float ex = nearest.transform.position.x;
                float away = px + (px > ex ? 12f : -12f);
                Status = "Dodging"; _safeMode = true;
                Push("walkto " + F(away) + " 1"); return;
            }
            // ON_THE_SECOND_DAY: castle level 2 while the achievement day counter is still 1 (until dawn of day 2)
            if (GiftCastleDue(px, home, coins)) return;

            if (evening || (dawn && nearest != null && Mathf.Abs(nearest.transform.position.x - home) < 40f))
            {
                _safeMode = true;
                if (Mathf.Abs(px - home) > 3f) { Status = "Evening: going home"; Push("goto " + F(home + 1.5f) + " 1"); return; }
                // at night: grab coins that fall inside the walls, otherwise wait by the fire
                var nc = NearestCoin(px, leftSafe + 1f, rightSafe - 1f);
                if (nc != null && Mathf.Abs(nc.transform.position.x - px) < 8f) { Status = "Night: coin"; Push("walkto " + F(nc.transform.position.x) + " 0.3"); return; }
                Status = "Night: at the fire"; Push("wait 2"); return;
            }

            // ---------- 2. campfire first, then chest & coins ----------
            var payables0 = UnityEngine.Object.FindObjectsOfType<Payable>();
            var fire = Find(payables0, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && Mode == "fire3" && day < 2)
            {
                // no kingdom yet: collect coins near the camp site, evade enemies, otherwise wait
                var c0 = NearestCoin(px, fire.transform.position.x - 30f, fire.transform.position.x + 30f);
                if (c0 != null && k.isDaytime) { Status = "Fire run: collect coins"; Push("walkto " + F(c0.transform.position.x) + " 0.3 20"); return; }
                var en = NearestEnemy(px);
                if (en != null && Mathf.Abs(en.transform.position.x - px) < 14f)
                {
                    float ex = en.transform.position.x;
                    Status = "Fire run: dodge"; _safeMode = true;
                    Push("walkto " + F(px + (px > ex ? 15f : -15f)) + " 1 6"); return;
                }
                Status = "Fire run: wait for day 3"; Push("walkto " + F(fire.transform.position.x) + " 2 10"); return;
            }
            if (fire != null && coins >= fire.price) { Status = "Light campfire"; PayAt(fire); return; }
            bool fireLit = fire == null && Find(payables0, x => x.name.StartsWith("Castle0")) == null;
            foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>())
            {
                if (!fireLit) break;
                float cx = ch.transform.position.x;
                if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && cx > leftSafe - 70f && cx < rightSafe + 70f && hour < k.dayEnd - 4f) { Cool(ch.GetInstanceID(), 1e7f); /* one try per chest and run */ Status = "Chest"; Push("goto " + F(cx) + " 0.3"); Push("wait 2"); return; }
            }
            var coin = NearestCoin(px, leftSafe - 25f, rightSafe + 25f);
            if (coin != null && Mathf.Abs(coin.transform.position.x - px) < 30f)
            {
                Cool(coin.GetInstanceID(), 1e7f); /* one attempt per coin - unreachable coins are ignored afterwards */
                Status = "Pick up coin"; Push("goto " + F(coin.transform.position.x) + " 0.3 20"); return;
            }

            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            int workers = UnityEngine.Object.FindObjectsOfType<Worker>().Length, archers = UnityEngine.Object.FindObjectsOfType<Archer>().Length;

            // ---------- 2b. walls on both sides have top priority (incl. rebuilding wrecks) ----------
            if (workers > 0)
            {
                int lw = WallsOnSide(-1, home), rw = WallsOnSide(1, home);
                if (lw == 0 || rw == 0)
                {
                    int side = lw == 0 ? -1 : 1;
                    if (lw == 0 && rw == 0) side = px < home ? -1 : 1;
                    var w0 = WallSpotOnSide(payables, home, side);
                    if (w0 != null && coins >= w0.price) { Status = "First wall " + (side < 0 ? "left" : "right"); PayAt(w0); return; }
                }
            }

            // ---------- 3. budget ----------
            var castleAny = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && !x.name.StartsWith("Castle0"));
            int reserve = 0;
            // coins in the purse are the buffer against hits at night (coins get knocked out before the crown)
            if (day >= 1 && hour >= k.dayEnd - 8f) reserve = 4;
            if (GiftPending() && castleAny != null) reserve = Mathf.Max(reserve, castleAny.price);
            int budget = coins - reserve;
            bool acrePhase = (AcreRun || Mode == "acre") && day <= 6 && (k.rightForest - k.leftForest) < AcreTarget + 5f;
            int archerCap = Mathf.Min(40, 6 + 3 * day);
            // army first: archers are income (hunting) and defence; extras only when the army is big enough or money piles up
            _armyFirst = archers < Mathf.Min(archerCap, 30) * 0.75f && budget < 12;

            // shops
            int toolsOnDisplay = 0;
            PayableShop bowShop = null, hammerShop = null, scytheShop = null;
            foreach (var py in payables)
            {
                var sh = py as PayableShop;
                if (sh == null) continue;
                toolsOnDisplay += sh.GetItemCount();
                if (sh.name.Contains("Bow")) bowShop = sh; else if (sh.name.Contains("Hammer")) hammerShop = sh; else if (sh.name.Contains("Scythe")) scytheShop = sh;
            }
            int farmers = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;

            // ---------- 3a. acre run: clearing has priority right after the first tools ----------
            if (Mode == "acre" && acrePhase && workers > 0 && archers >= 2 && budget >= 1 && !(peasants > toolsOnDisplay && workers < 6))
            {
                var tree = EdgeTree(payables, k);
                if (tree != null) { Status = "Acre: chop tree (gap " + (k.rightForest - k.leftForest).ToString("0") + "/" + AcreTarget + ")"; PayAt(tree); return; }
            }

            // ---------- 3b. assault phase: stone tech, castle up to shields, knights, charges ----------
            if (AssaultStep(payables, day, hour, budget, archers, home, px)) return;

            // ---------- 4. tools for idle peasants (archers capped, workers for building/clearing) ----------
            if (peasants > toolsOnDisplay)
            {
                bool farmExists = false;
                foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>()) if (fh.level > 0) farmExists = true;
                PayableShop shop = null; string what = "";
                if (archers < 1 && bowShop != null) { shop = bowShop; what = "bow"; }
                else if (workers < 1 && hammerShop != null) { shop = hammerShop; what = "hammer"; }
                else if (archers < 2 && bowShop != null) { shop = bowShop; what = "bow"; }
                else if (workers < WorkerTarget(day, archers, acrePhase) && hammerShop != null) { shop = hammerShop; what = "hammer"; }
                else if (scytheShop != null && farmExists && farmers < 3) { shop = scytheShop; what = "scythe"; }
                else if (archers < archerCap && bowShop != null) { shop = bowShop; what = "bow"; }
                if (shop != null && SafeCanPay(shop) && budget >= shop.price)
                {
                    Status = "Buy tool: " + what + " (archers " + archers + ", workers " + workers + "/" + WorkerTarget(day, archers, acrePhase) + ", hammerShop " + (hammerShop != null) + ")";
                    PayAt(shop); return;
                }
            }

            // ---------- 5. castle (achievement timing handled above; later when affordable: lvl 4 = stone walls) ----------
            var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x));
            bool wallsOk = WallsOnSide(-1, home) > 0 && WallsOnSide(1, home) > 0;
            if (castle != null && !GiftPending() && day >= 2 && wallsOk && archers >= 8 && budget >= castle.price + 2)
            {
                Status = "Castle upgrade (" + castle.name.Replace("(Clone)", "") + ", " + castle.price + ")";
                PayAt(castle); return;
            }

            // ---------- 6. clear forest for the acre achievement (days 1-6) ----------
            float gap = k.rightForest - k.leftForest;
            if (acrePhase && workers > 0 && budget >= 1 && archers >= 2)
            {
                var tree = EdgeTree(payables, k);
                if (tree != null) { Status = "Chop tree (gap " + gap.ToString("0") + ")"; PayAt(tree); return; }
            }

            // ---------- 7. strengthen walls, then towers ----------
            if (archers >= 8 && budget >= 3 && !_armyFirst)
            {
                var up = Find(payables, x => x is PayableUpgrade && IsBuiltWall(x) && SafeCanPay(x) && x.price <= budget, home);
                if (up != null) { Status = "Upgrade wall (" + up.price + ")"; PayAt(up); return; }
                var tower = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Tower") && SafeCanPay(x) && x.price <= budget && Between(x.transform.position.x, leftSafe - 1f, rightSafe + 1f), home);
                if (tower != null) { Status = "Build tower (" + tower.price + ")"; PayAt(tower); return; }
            }

            // ---------- 8. more walls further out ----------
            var wall = InnerWallSpot(payables, home);
            if (wall != null && budget >= wall.price + 1 && archers >= 6)
            {
                Status = "Build wall";
                PayAt(wall); return;
            }

            // ---------- 8c. farms (income) before more recruits once there is a decent army ----------
            if (archers >= 10 && budget >= 6)
            {
                var farm = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Farmhouse") && SafeCanPay(x) && Between(x.transform.position.x, leftSafe - 2f, rightSafe + 2f), home);
                if (farm != null) { Status = "Farm (" + farm.price + ")"; PayAt(farm); return; }
            }

            // ---------- 9a. recruit beggars (only while the army is below the cap) ----------
            int army = archers + workers + farmers;
            if (Time.time >= _noRecruitUntil && budget >= 2 && peasants < 2 && hour < k.dayEnd - 4f && army < archerCap + WorkerTarget(day, archers, acrePhase) + 3)
            {
                // reach: 35 beyond the walls, but at least 60 around the fire while there is time left today
                float reach = hour < k.dayEnd - 10f ? 150f : (hour < k.dayEnd - 7f ? 100f : 35f);   /* far camps only in the morning */
                var b = NearestBeggar(px, Mathf.Min(leftSafe - 35f, home - reach), Mathf.Max(rightSafe + 35f, home + reach));
                if (b != null)
                {
                    Status = "Recruit beggar";
                    float bx = b.transform.position.x;
                    Cool(b.GetInstanceID(), 8f);
                    _lastTap = Time.time; _coinsAtTap = coins;
                    Push("walkto " + F(bx) + " 0.8 25"); Push("tap 1"); Push("wait 2.5"); return;
                }
            }

            // ---------- 9b. farms + trees for income ----------

            if (workers > 0 && budget >= 3 && !acrePhase && gap < 200f)
            {
                var tree = EdgeTree(payables, k);
                if (tree != null) { Status = "Chop tree (gap " + gap.ToString("0") + ")"; PayAt(tree); return; }
            }

            // ---------- 9. patrol to collect taxes ----------
            Status = "Patrol (collect taxes)";
            float target = px < home ? rightSafe - 1f : leftSafe + 1f;
            Push("walkto " + F(target) + " 1");
        }

        // ---------------- pacifist run: FOR_FIVE_DAYS (no kills until day 5) + ON_THE_EIGTH_DAY (no dropped coin until day 7) ----------------
        void ThinkPacifist()
        {
            // FOR_FIVE_DAYS: no kill until day counter 5 -> no bows before that (walls + workers only)
            // ON_THE_EIGTH_DAY: no player coin may touch the ground until day counter 7 -> recruit only by handing the
            //   coin straight to a beggar (stand right over him), never get hit (coins knocked out of the purse count too)
            var k = Managers.kingdom; var am = Managers.achievements;
            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour;
            int coins = GameState.Coins, dayC = am != null ? am.counters[1] : GameState.Day;
            bool noKillPhase = dayC < 5 && Campaign.Missing(Campaign.FifthDays);
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            bool walled = WallsOnSide(-1, home) > 0 && WallsOnSide(1, home) > 0;
            bool night = !k.isDaytime || hour >= k.dayEnd - 1.5f;
            _safeMode = true;

            // 1. danger: enemy without a wall in between -> move away (inside the walls if possible)
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 14f && !WallBetween(px, e.transform.position.x))
            {
                float ex = e.transform.position.x;
                float lo = walled ? l + 1f : PortalLimit(-1), hi = walled ? r - 1f : PortalLimit(1);
                float target = Mathf.Clamp(px + (px > ex ? 12f : -12f), lo, hi);
                Status = "Pacifist: dodge"; Push("goto " + F(target) + " 1 2"); return;
            }
            // 2. night: stay by the fire behind the walls
            if (night) { Status = "Pacifist: night at the fire"; if (Mathf.Abs(px - home) > 2f) Push("goto " + F(home) + " 1 6"); else Push("wait 1"); return; }

            // 3. fire, coins/chests (not dropped by us)
            var fire = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && coins >= fire.price) { Status = "Pacifist: light fire"; PayAt(fire); return; }
            foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>())
            {
                float cx = ch.transform.position.x;
                if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && Mathf.Abs(cx - home) < 40f) { Cool(ch.GetInstanceID(), 120f); Status = "Pacifist: chest"; Push("goto " + F(cx) + " 0.3 10"); Push("wait 1"); return; }
            }
            var coin = NearestCoin(px, home - 40f, home + 40f);
            if (coin != null && !coin.droppedByPlayer) { Cool(coin.GetInstanceID(), 1e7f); Status = "Pacifist: coin"; Push("walkto " + F(coin.transform.position.x) + " 0.3 10"); return; }

            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            int workers = UnityEngine.Object.FindObjectsOfType<Worker>().Length, archers = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
            int onDisplay = 0; PayableShop bow = null, hammer = null;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; else if (sh.name.Contains("Hammer")) hammer = sh; }

            // 4. tools: hammers (walls) - bows only after the no-kill phase
            if (peasants > onDisplay)
            {
                PayableShop shop = (workers < 3 || noKillPhase) ? hammer : bow;
                if (shop != null && SafeCanPay(shop) && coins >= shop.price) { Status = "Pacifist: buy " + (shop == hammer ? "hammer" : "bow"); PayAt(shop); return; }
            }
            // 5. castle upgrades (bigger daily chest = the only income without archers), walls x2 per side, upgrades
            var castleUp = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && !x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (castleUp != null && walled && coins >= castleUp.price + 1) { Status = "Pacifist: castle " + castleUp.name.Replace("(Clone)", ""); PayAt(castleUp); return; }
            if (workers > 0)
            {
                for (int need = 1; need <= 2; need++)
                for (int side = -1; side <= 1; side += 2)
                {
                    if (WallsOnSide(side, home) >= need) continue;
                    var w = WallSpotOnSide(payables, home, side);
                    if (w != null && coins >= w.price) { Status = "Pacifist: wall " + (side < 0 ? "left" : "right"); PayAt(w); return; }
                }
                var up = Find(payables, x => x is PayableUpgrade && IsBuiltWall(x) && SafeCanPay(x) && x.price <= coins - 2, home);
                if (up != null) { Status = "Pacifist: upgrade wall"; PayAt(up); return; }
            }
            // 6. recruit by hand-over (never let the coin touch the ground)
            if (coins >= 2 && peasants + workers + archers < (noKillPhase ? 3 : 12) && hour < k.dayEnd - 5f)
            {
                var b = NearestBeggar(px, home - 70f, home + 70f);
                if (b != null)
                {
                    Cool(b.GetInstanceID(), 10f); _lastTap = Time.time;
                    Status = "Pacifist: hand coin to beggar";
                    Push("walkto " + F(b.transform.position.x) + " 0.3 25"); Push("handtap " + b.GetInstanceID()); Push("wait 2"); return;
                }
            }
            Status = "Pacifist: waiting (day counter " + dayC + ", kills " + (am != null ? am.counters[5] : -1) + ", dropped " + (am != null && am.flags[5]) + ")";
            if (Mathf.Abs(px - home) > 3f) Push("walkto " + F(home) + " 1 6"); else Push("wait 1");
        }

        // ---------------- main: reroll maps with portals close to the camp (forum: "restart bad maps") ----------------
        int _mainRerolls; bool _mainMapChecked;
        public float MinPortalDistance = 100f;
        bool MainMapCheck()
        {
            if (_mainMapChecked || NormalizeRunType(ConfigMode) == "main" && false) return false;
            _mainMapChecked = true;
            var k = Managers.kingdom; float home = k.campfirePosition;
            float left = 1e6f, right = 1e6f; int camps = 0;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                if (po.outerSpawner) continue;
                float d = po.transform.position.x - home;
                if (d < 0) left = Mathf.Min(left, -d); else right = Mathf.Min(right, d);
            }
            foreach (var bc in UnityEngine.Object.FindObjectsOfType<BeggarCamp>()) if (Mathf.Abs(bc.transform.position.x - home) < 90f) camps++;
            bool good = left >= MinPortalDistance && right >= MinPortalDistance && camps >= 3;
            Plugin.Log(string.Format("main map check: nearest portals L {0:0} / R {1:0}, camps within 90: {2} -> {3} (reroll {4})", left, right, camps, good ? "keep" : "reroll", _mainRerolls));
            if (good || _mainRerolls >= 30) { _mainRerolls = 0; return false; }
            _mainRerolls++; _lastDaySeen = -1; _pl.Queue.Clear();
            Acc.Call(Managers.game, "Restart");
            Status = "main: reroll map"; Push("wait 2"); return true;
        }

        // ---------------- army1: ON_THE_FIRST_DAY (8 archers before the first dawn) ----------------
        int _rerolls; bool _mapChecked;
        void ThinkArmy1(bool feast)
        {
            var k = Managers.kingdom; var am = Managers.achievements;
            float px = GameState.PX, home = k.campfirePosition;
            int coins = GameState.Coins;
            _safeMode = true;
            // 1. map check: enough coins on the ground and in chests near the start? otherwise reroll the map
            if (!_mapChecked && !feast)
            {
                int avail = coins;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>()) if (!c.pickedUp && Mathf.Abs(c.transform.position.x - home) < 70f) avail++;
                foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>()) if (!ch.isEmpty && Mathf.Abs(ch.transform.position.x - home) < 70f) avail += ch.coins;
                _mapChecked = true;
                Plugin.Log("army1: coins reachable on this map = " + avail + " (reroll " + _rerolls + ")");
                if (avail < 24 && _rerolls < 10 && NormalizeRunType(ConfigMode) == "auto")
                {
                    _rerolls++; _lastDaySeen = -1; _pl.Queue.Clear();
                    Acc.Call(Managers.game, "Restart");
                    Status = "army1: reroll map"; Push("wait 2"); return;
                }
            }
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            // 2. danger
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 10f && !WallBetween(px, e.transform.position.x))
            { Status = "army1: dodge"; Push("goto " + F(px + (px > e.transform.position.x ? 14f : -14f)) + " 1 2"); return; }
            // night: stay by the fire (feast runs over several days)
            if (feast && (!k.isDaytime || GameState.Hour >= k.dayEnd - 1.5f))
            { Status = "Feast: night at the fire"; if (Mathf.Abs(px - home) > 2f) Push("goto " + F(home) + " 1 6"); else Push("wait 1"); return; }
            // 3. coins & chests
            foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>())
            {
                float cx = ch.transform.position.x;
                if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && Mathf.Abs(cx - home) < 70f) { Cool(ch.GetInstanceID(), 60f); Status = "army1: chest (" + ch.coins + ")"; Push("goto " + F(cx) + " 0.3 30"); Push("wait 1"); return; }
            }
            var coin = NearestCoin(px, home - 70f, home + 70f);
            if (coin != null && !(coin.droppedByPlayer && Time.time - _lastTap < 15f)) { Cool(coin.GetInstanceID(), 1e7f); Status = "army1: coin"; Push("goto " + F(coin.transform.position.x) + " 0.3 20"); return; }
            // 4. fire (opens the bow shop)
            var fire = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && coins >= fire.price) { Status = "army1: light fire"; PayAt(fire); return; }
            // 5. bows for every idle peasant, otherwise recruit until 8 archers are possible
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            int archersMade = am != null ? am.counters[3] : 0;
            PayableShop bow = null; int onDisplay = 0;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; }
            if (peasants > onDisplay && bow != null && SafeCanPay(bow) && coins >= bow.price) { Status = "army1: buy bow (" + archersMade + "/8)"; PayAt(bow); return; }
            int target = feast ? 15 : 8;
            if (archersMade + peasants + onDisplay < target && coins >= 3)
            {
                var b = NearestBeggar(px, home - 100f, home + 100f);
                if (b != null) { Cool(b.GetInstanceID(), 8f); _lastTap = Time.time; Status = "army1: recruit (" + archersMade + "/8)"; Push("goto " + F(b.transform.position.x) + " 0.8 25"); Push("tap 1"); Push("wait 2"); return; }
            }
            Status = (feast ? "Feast: waiting (deer " + (am != null ? am.counters[4] : -1) + ", " : "army1: waiting (") + archersMade + " archers, " + coins + " coins)";
            if (Mathf.Abs(px - home) > 3f) Push("walkto " + F(home) + " 1 6"); else Push("wait 1");
        }

        /// <summary>Furthest safe x on one side: a bit inside the nearest inner portal (enemies spawn there).</summary>
        static float PortalLimit(int side)
        {
            float px = GameState.PX, best = side < 0 ? -1e6f : 1e6f;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                float x = p.transform.position.x;
                if (side < 0 && x < px && x > best) best = x;
                if (side > 0 && x > px && x < best) best = x;
            }
            if (Mathf.Abs(best) > 1e5f) best = px + side * 60f;
            return best - side * 15f;
        }

        /// <summary>
        /// ON_THE_FOURTH_DAY: the 10th hunted deer must fall on day counter 3. Archers only hunt in safe daytime and
        /// deer flee from the rider -> ride around a deer and push it towards the camp, following a deer schedule.
        /// </summary>
        bool FeastStep()
        {
            var k = Managers.kingdom; var am = Managers.achievements;
            if (am == null || am.counters == null) return false;
            int dayC = am.counters[1], deer = am.counters[4];
            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour;
            int archers = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
            if (!k.isDaytime || hour > k.dayEnd - 4f || archers < 2 || dayC > 3) return false;
            var en = NearestEnemy(px);
            if (en != null && Mathf.Abs(en.transform.position.x - px) < 15f) return false;
            int allowed = dayC == 0 ? 3 : dayC == 1 ? 6 : dayC == 2 ? 9 : 10;   // never reach 10 before day counter 3
            if (deer >= allowed) return false;
            // nearest deer outside the walls
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            Deer best = null; float bd = 90f;
            foreach (var d in UnityEngine.Object.FindObjectsOfType<Deer>())
            {
                float x = d.transform.position.x;
                if (x > l && x < r) continue;
                float dist = Mathf.Abs(x - home);
                if (dist < bd) { bd = dist; best = d; }
            }
            if (best == null) return false;
            float dx = best.transform.position.x;
            int side = dx < home ? -1 : 1;
            float behind = dx + side * 7f;                      // get round the deer (far side from the camp)
            Status = "Feast: herd deer (" + deer + "/10, day counter " + dayC + ", allowed " + allowed + ")";
            _safeMode = true;
            if ((px - dx) * side < 3f) { Push("goto " + F(behind) + " 1.5 12"); return true; }
            Push("walkto " + F(home + side * 6f) + " 2 10");     // ride back towards the camp: the deer flees ahead into the archers
            return true;
        }

        /// <summary>
        /// Late-game survival = fully upgraded walls ("maxed two walls per side" - forum). Spend surplus coins on
        /// stone tech, a second wall per side and wall upgrades (innermost first).
        /// </summary>
        bool DefenseStep()
        {
            var k = Managers.kingdom;
            int day = GameState.Day; float hour = GameState.Hour, px = GameState.PX, home = k.campfirePosition;
            if (day < 3 || !k.isDaytime || hour > k.dayEnd - 5f) return false;
            var en = NearestEnemy(px);
            if (en != null && Mathf.Abs(en.transform.position.x - px) < 15f) return false;
            int coins = GameState.Coins;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            int workers = UnityEngine.Object.FindObjectsOfType<Worker>().Length;
            // stone tech (needed for wall levels 3+) - morning trip to the mason shrine
            if (Managers.techUpgrades != null && !Managers.techUpgrades.stoneBuilding && day >= 4 && hour < k.dayEnd - 9f)
            {
                var shrine = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Mason") && SafeCanPay(x));
                if (shrine != null && coins >= shrine.price + 3 && Mathf.Abs(shrine.transform.position.x - home) < 140f)
                { Status = "Defence: mason shrine (stone)"; Cool(shrine.GetInstanceID(), 30f); Push("goto " + F(PayX(shrine)) + " 0.3 40"); Push("payid " + shrine.GetInstanceID() + " " + F(PayX(shrine))); Push("goto " + F(home) + " 1 40"); return true; }
            }
            if (workers < 1) return false;
            // Archers always stand at the OUTERMOST wall of their side -> that wall must be the strongest one.
            // 1. upgrade the outermost wall of each side (weakest side first)
            Payable best = null; int bestLvl = 99;
            for (int side = -1; side <= 1; side += 2)
            {
                Wall outer = OuterWall(side, home);
                if (outer == null) continue;
                var up = UpgradeOf(payables, outer);
                if (up != null && up.price <= coins - 3 && outer.level < bestLvl) { bestLvl = outer.level; best = up; }
            }
            if (best != null) { Status = "Defence: upgrade outer wall (L" + bestLvl + ", " + best.price + ")"; PayAt(best); return true; }
            // 2. a second wall further out only once the outer wall is stone (level 3+) - otherwise the archers move to a weak wall
            for (int side = -1; side <= 1; side += 2)
            {
                Wall outer = OuterWall(side, home);
                if (outer == null || outer.level < 3 || WallsOnSide(side, home) >= 2) continue;
                var w = WallSpotOnSide(payables, home, side);
                if (w != null && coins >= w.price + 8) { Status = "Defence: 2nd wall " + (side < 0 ? "left" : "right"); PayAt(w); return true; }
            }
            return false;
        }

        /// <summary>
        /// An enemy got inside the walls (e.g. a boss that stays after dawn): the king keeps to the far side instead of
        /// running "home" into it, and waits there until the archers have dealt with it.
        /// </summary>
        bool AvoidIntruder()
        {
            var k = Managers.kingdom; float px = GameState.PX, home = k.campfirePosition;
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            Enemy worst = null; float wd = float.MaxValue;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (!IsLiveEnemy(e)) continue;
                float ex = e.transform.position.x;
                if (ex <= l || ex >= r) continue;                    // only intruders
                float d = Mathf.Abs(ex - px);
                if (d < wd) { wd = d; worst = e; }
            }
            if (worst == null || wd > 22f) return false;
            float ix = worst.transform.position.x;
            float target = ix > home ? l + 1.5f : r - 1.5f;      // far side, still inside the walls
            if (Mathf.Abs(target - ix) < 12f) target = ix > home ? l - 10f : r + 10f;   // too close anyway -> beyond the wall
            _safeMode = true;
            Status = "Intruder inside the walls - keep away (" + worst.name.Replace("(Clone)", "") + ")";
            if (Mathf.Abs(px - target) > 2f) Push("goto " + F(target) + " 1 4"); else Push("wait 1");
            return true;
        }

        /// <summary>Level of an existing tower behind a tower upgrade payable (0 = empty tower spot).</summary>
        static int TowerLevelOf(Payable p)
        {
            var t = p.GetComponent<Tower>() ?? (p.transform.parent != null ? p.transform.parent.GetComponent<Tower>() : null);
            return t != null ? t.level : 0;
        }

        /// <summary>Target level of the outer walls per day (boss nights get much harder from ~day 10 and ~day 25).</summary>
        static int WallTarget(int day) { return day >= 14 ? 4 : day >= 9 ? 3 : day >= 4 ? 2 : 1; }

        /// <summary>Buys the next scheduled defence item, or saves for it (patrol for taxes, pick up coins). true = acted.</summary>
        bool WallSchedule()
        {
            var k = Managers.kingdom;
            int day = GameState.Day; float hour = GameState.Hour, px = GameState.PX, home = k.campfirePosition;
            if (day < 4 || !k.isDaytime || hour > k.dayEnd - 4f) return false;
            var en = NearestEnemy(px);
            if (en != null && Mathf.Abs(en.transform.position.x - px) < 15f) return false;
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length < 1) return false;   // walls need builders
            int coins = GameState.Coins;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            Payable item = null; string what = null;
            // 1. stone tech first (wall levels 3+ need it)
            if (Managers.techUpgrades != null && !Managers.techUpgrades.stoneBuilding)
            {
                var shrine = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Mason") && SafeCanPay(x));
                if (shrine != null && Mathf.Abs(shrine.transform.position.x - home) < 140f && hour < k.dayEnd - 9f) { item = shrine; what = "mason shrine (stone)"; }
            }
            // 2. outer wall of the weaker side below today's target
            if (item == null)
            {
                int target = WallTarget(day), bestLvl = 99;
                for (int side = -1; side <= 1; side += 2)
                {
                    var outer = OuterWall(side, home);
                    if (outer == null || outer.level >= target) continue;
                    var up = UpgradeOf(payables, outer);
                    if (up != null && outer.level < bestLvl) { bestLvl = outer.level; item = up; what = "outer wall " + (side < 0 ? "left" : "right") + " L" + outer.level + "->" + (outer.level + 1); }
                }
            }
            // 3. towers next to the outer walls: archers in a guard slot are immune to troll/boss hits
            if (item == null && day >= 9)
            {
                int ttarget = day >= 20 ? 3 : day >= 14 ? 2 : 1, bestT = 99;
                for (int side = -1; side <= 1; side += 2)
                {
                    float outerX = WallEdge(side, home);
                    // best tower payable on this side inside the outer wall: unbuilt spot (Tower0*) or upgrade of a low tower
                    foreach (var p in payables)
                    {
                        if (!(p is PayableUpgrade) || !p.name.StartsWith("Tower") || !SafeCanPay(p) || OnCooldown(p.GetInstanceID())) continue;
                        float rel = (p.transform.position.x - home) * side, outerRel = (outerX - home) * side;
                        if (rel <= 0 || rel > outerRel + 0.5f) continue;
                        int lvl = TowerLevelOf(p);
                        if (lvl >= ttarget || lvl >= bestT) continue;
                        bestT = lvl; item = p; what = "tower " + (side < 0 ? "left" : "right") + " L" + lvl + "->" + (lvl + 1);
                    }
                }
            }
            if (item == null) return false;
            if (coins >= item.price + 4)   // keep a small purse: coins are knocked out before the crown
            {
                Status = "Defence: " + what + " (" + item.price + ")";
                if (item.name.StartsWith("Mason")) { Cool(item.GetInstanceID(), 30f); Push("goto " + F(PayX(item)) + " 0.3 40"); Push("payid " + item.GetInstanceID() + " " + F(PayX(item))); Push("goto " + F(home) + " 1 40"); }
                else PayAt(item);
                return true;
            }
            // while saving the army must keep growing: bows for idle peasants, then recruit beggars (hand-over)
            int archersNow = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
            if (archersNow < Mathf.Min(2 * day + 4, 30) && hour < k.dayEnd - 5f)
            {
                int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length, onDisplay = 0; PayableShop bow = null;
                foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; }
                if (peasants > onDisplay && bow != null && SafeCanPay(bow) && coins >= bow.price) { Status = "Army while saving: buy bow"; PayAt(bow); return true; }
                if (peasants + onDisplay < 2 && coins >= 3)
                {
                    var b = NearestBeggar(px, home - 150f, home + 150f);
                    if (b != null) { Cool(b.GetInstanceID(), 10f); Status = "Army while saving: recruit"; Push("walkto " + F(b.transform.position.x) + " 0.3 25"); Push("handtap " + b.GetInstanceID()); Push("wait 1.5"); return true; }
                }
            }
            // save: collect coins and taxes instead of spending
            var c = NearestCoin(px, home - 40f, home + 40f);
            if (c != null) { Cool(c.GetInstanceID(), 1e7f); Status = "Saving for " + what + ": coin"; Push("goto " + F(c.transform.position.x) + " 0.3 15"); return true; }
            Status = "Saving for " + what + " (" + coins + "/" + (item.price + 4) + ")";
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            Push("walkto " + F(px < home ? r - 1f : l + 1f) + " 1 20");   // ride along the walls past the archers (taxes)
            return true;
        }

        /// <summary>
        /// Castle levels above 3 cut the daily chest (level 0-3: 5-7 coins, level 4: 4, level 5-6: 1) - only the assault
        /// phase needs them (knights). Payable "CastleN" builds castle level N.
        /// </summary>
        public static bool CastleAllowed(string payableName)
        {
            var b = Plugin.Instance != null ? Plugin.Instance.Brain : null;
            if (b == null || b.Mode == "master") return true;
            int n;
            return payableName.Length > 6 && int.TryParse(payableName.Substring(6, 1), out n) ? n <= 3 : true;
        }

        public static Wall OuterWall(int side, float home)
        {
            Wall best = null; float bd = -1f;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>())
            {
                if (!w.isIntact) continue;
                float rel = (w.transform.position.x - home) * side;
                if (rel > 0 && rel > bd) { bd = rel; best = w; }
            }
            return best;
        }

        static Payable UpgradeOf(Payable[] payables, Wall w)
        {
            foreach (var p in payables)
            {
                if (!(p is PayableUpgrade) || !SafeCanPay(p)) continue;
                if (p.gameObject == w.gameObject || (p.transform.parent != null && p.transform.parent.gameObject == w.gameObject)) return p;
            }
            return null;
        }

        /// <summary>Farms = the main mid-game income. Build a farmhouse near the camp and staff it with farmers.</summary>
        bool EconomyStep()
        {
            var k = Managers.kingdom;
            int day = GameState.Day; float hour = GameState.Hour, px = GameState.PX, home = k.campfirePosition;
            if (day < 3 || !k.isDaytime || hour > k.dayEnd - 5f) return false;
            var en = NearestEnemy(px);
            if (en != null && Mathf.Abs(en.transform.position.x - px) < 15f) return false;
            int coins = GameState.Coins;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            // merchant: one payment = a whole shop restocked for free (forum tip: pay him on every visit)
            var merchant = Find(payables, x => x is Merchant && SafeCanPay(x) && Mathf.Abs(x.transform.position.x - home) < 60f, px);
            if (merchant != null && coins >= merchant.price + 2) { Status = "Economy: pay the merchant (" + merchant.price + ")"; PayAt(merchant); return true; }
            PayableShop scythe = null; int onDisplay = 0;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Scythe")) scythe = sh; }
            if (scythe == null) return false;                               // castle level 2+ needed
            int farmsBuilt = 0;
            foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>()) if (fh.level > 0 && Mathf.Abs(fh.transform.position.x - home) < 60f) farmsBuilt++;
            bool farmBuilt = farmsBuilt > 0;
            int farmersNow = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;
            if (farmsBuilt == 0 || (farmsBuilt == 1 && farmersNow >= 3 && day >= 12))
            {
                var farm = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Farmhouse") && SafeCanPay(x) && Mathf.Abs(x.transform.position.x - home) < 55f, home);
                if (farm != null && coins >= farm.price + 1) { Status = "Economy: build farm (" + farm.price + ")"; PayAt(farm); return true; }
                if (!farmBuilt) return false;
            }
            int farmers = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            if (farmers < Mathf.Min(6, 3 * farmsBuilt) && peasants > onDisplay && SafeCanPay(scythe) && coins >= scythe.price + 1)
            { Status = "Economy: buy scythe (farmers " + farmers + ")"; PayAt(scythe); return true; }
            return false;
        }

        public int KnightArmor = 4;

        /// <summary>Portal assault (master mode): returns true if it queued an action.</summary>
        bool AssaultStep(Payable[] payables, int day, float hour, int budget, int archers, float home, float px)
        {
            if ((Mode != "master" && Mode != "survive") || day < 5) return false;
            bool assault = Mode == "master";
            var k = Managers.kingdom; var am = Managers.achievements;
            bool finalWave = Managers.enemies != null && Managers.enemies.finalWaveSpawned;
            bool morning = k.isDaytime && hour < k.dayEnd - 9f;
            bool wallsOk = WallsOnSide(-1, home) > 0 && WallsOnSide(1, home) > 0;

            // castle first from day 8 (level 5 = shield shops) - only when attacking; survive upgrades normally
            if (assault && day >= 8 && archers >= 6)
            {
                var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x));
                if (castle != null && budget >= castle.price) { Status = "Assault prep: castle " + castle.name.Replace("(Clone)", "") + " (" + castle.price + ")"; PayAt(castle); return true; }
            }
            // stone tech from the mason shrine (morning trip)
            if (Managers.techUpgrades != null && !Managers.techUpgrades.stoneBuilding && archers >= 8 && morning && !_armyFirst)
            {
                var shrine = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Mason") && SafeCanPay(x));
                if (shrine != null && budget >= shrine.price && Mathf.Abs(shrine.transform.position.x - home) < 140f)
                { Status = "Assault prep: mason shrine (stone)"; Cool(shrine.GetInstanceID(), 30f); Push("goto " + F(PayX(shrine)) + " 0.3 40"); Push("payid " + shrine.GetInstanceID() + " " + F(PayX(shrine))); Push("goto " + F(home) + " 1 40"); return true; }
            }
            if (!assault && day >= 10 && archers >= 8)
            {
                // castle up to "Castle4" (-> level that turns nearby walls to stone and upgrades towers)
                var c4 = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x)
                                              && CastleIndex(x.name) >= 1 && CastleIndex(x.name) <= 4);
                if (c4 != null && budget >= c4.price) { Status = "Defence: castle " + c4.name.Replace("(Clone)", "") + " (" + c4.price + ")"; PayAt(c4); return true; }
            }
            if (_armyFirst && !assault) return false;
            // outer wall ring: the border banner (needed to send knights) only exists if the outermost wall
            // is far enough from the campfire -> keep a wall >= 14 units out on both sides
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length >= 1)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    float outer = WallEdge(side, home);
                    if (Mathf.Abs(outer - home) >= 11f) continue;
                    Payable best = null; float bd = float.MaxValue;
                    foreach (var p in payables)
                    {
                        if (!(p is PayableUpgrade) || !(p.name.StartsWith("Wall") || p.name.Contains("Wreck")) || IsBuiltWall(p) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                        float rel = (p.transform.position.x - home) * side;
                        if (rel < 11f || rel > 40f) continue;
                        if (rel < bd) { bd = rel; best = p; }
                    }
                    if (best != null && budget >= best.price + 1) { Status = "Outer wall " + (side < 0 ? "left" : "right") + " (banner range)"; PayAt(best); return true; }
                }
            }

            // catapults (workshop, castle level 3+): defensive artillery at the border, pushed there by workers
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length >= 2)
            {
                var ws = Find(payables, x => x is PayableWorkshop && SafeCanPay(x), home);
                if (ws != null && budget >= ws.price + 2) { Status = "Buy catapult (" + ws.price + ")"; PayAt(ws); return true; }
            }
            if (finalWave || !assault) return false;   // survive mode / all inner portals gone: only defend
            // shields -> knights
            var shield = Find(payables, x => x is PayableShield && SafeCanPay(x), home);
            if (shield != null && budget >= shield.price) { Status = "Assault: buy shield (" + shield.price + ")"; PayAt(shield); return true; }
            // arm the knights: coins in a knight's purse are knocked out before he loses his shield
            var knights = UnityEngine.Object.FindObjectsOfType<Knight>();
            Knight poor = null; int poorCoins = 99;
            foreach (var kn in knights)
            {
                int kc = 0; try { kc = Acc.Wallet(kn).coins; } catch { }
                if (kc < KnightArmor && kc < poorCoins && !OnCooldown(kn.GetInstanceID())) { poorCoins = kc; poor = kn; }
            }
            if (poor != null && budget >= 3 && k.isDaytime)
            {
                Cool(poor.GetInstanceID(), 6f);
                Status = "Assault: arm knight (" + poorCoins + "/" + KnightArmor + " coins)";
                Push("walkto " + F(poor.transform.position.x) + " 0.6 20"); Push("tap 2"); Push("wait 1.5"); return true;
            }
            bool armed = knights.Length > 0;
            foreach (var kn in knights) { int kc = 0; try { kc = Acc.Wallet(kn).coins; } catch { } if (kc < 2) armed = false; }
            // charge: pay the border banner while knights with archers are ready (and armed)
            if (morning && armed)
            {
                var border = Find(payables, x => x is PayableBorder && SafeCanPay(x), px);
                if (border != null && budget >= border.price) { Status = "Assault: knights CHARGE (" + border.name.Replace("(Clone)", "") + ")"; Cool(border.GetInstanceID(), 60f); PayAt(border); return true; }
            }
            return false;
        }

        static int CastleIndex(string name)
        {
            int i = 6, n;
            if (name.Length > 6 && int.TryParse(name.Substring(6, 1), out n)) i = n;
            return i;
        }

        bool GiftPending()
        {
            if ((Mode != "master" && Mode != "survive") || Campaign.Has(Campaign.SecondDay) == true) return false;
            var am = Managers.achievements;
            if (am == null || am.flags == null || am.flags[2]) return false;   // FreeWallBuilt already set this run
            return am.counters[1] <= 1;
        }

        /// <summary>Upgrade the castle as soon as its cooldown ends during achievement-days 0..1.</summary>
        bool GiftCastleDue(float px, float home, int coins)
        {
            if (!GiftPending()) return false;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            var c = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && !x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (c == null || coins < c.price) return false;
            if (Mathf.Abs(c.transform.position.x - px) > 25f) return false;   // only when near home (night safe)
            _safeMode = true;
            Status = "Castle upgrade for gift (" + c.name + ")";
            PayAt(c); return true;
        }

        /// <summary>The innermost foundation right of the castle (within 12) is kept free for the castle's free wall.</summary>
        Payable GiftFoundation(Payable[] payables, float home)
        {
            if (!GiftPending()) return null;
            Payable best = null; float bd = 12f;
            foreach (var p in payables)
            {
                if (!(p is PayableUpgrade) || !p.name.StartsWith("Wall0")) continue;
                float rel = p.transform.position.x - home;
                if (rel <= 0 || rel > bd) continue;
                bd = rel; best = p;
            }
            return best;
        }

        /// <summary>Checked several times per second: abort the running plan when it became dangerous.</summary>
        public bool ShouldInterrupt()
        {
            if (_safeMode || Mode == "pacifist") return false;
            // phases driven by the record strategy: use its own guard (it knows its flight/retreat moves)
            if ((Mode == "survive" || Mode == "master" || Mode == "feast") && _legacy != null && !Status.StartsWith("Assault") && !Status.StartsWith("Economy") && !Status.StartsWith("Defence"))
                return _legacy.ShouldInterrupt();
            var p = GameState.Player; var k = Managers.kingdom; var g = Managers.game;
            if (p == null || k == null || g == null || (int)g.gameState != 2) return false;
            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour; int day = GameState.Day;
            bool evening = hour >= k.dayEnd - 1.5f || (!k.isDaytime && !(day == 0 && hour < 12f));
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            if (evening && (px < l - 0.5f || px > r + 0.5f)) { Status = "Interrupt: dusk"; return true; }
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 9f && !WallBetween(px, e.transform.position.x)) { Status = "Interrupt: enemy"; return true; }
            return false;
        }

        int WorkerTarget(int day, int archers, bool acrePhase)
        {
            if (day == 0) return 1;
            if (acrePhase) return 6;                   // clearing for the acre
            return Mathf.Clamp(archers / 4, 3, 6);
        }

        void PayAt(Payable py)
        {
            Cool(py.GetInstanceID(), 6f);
            float x = PayX(py);
            Push("walkto " + F(x) + " 0.3");
            Push("payid " + py.GetInstanceID() + " " + F(x));
        }

        /// <summary>x of the payable's trigger (selection uses the collider, which can be offset from the transform).</summary>
        public static float PayX(Payable py)
        {
            var col = py.GetComponent<Collider2D>();
            return col != null ? col.bounds.center.x : py.transform.position.x;
        }

        // ----- helpers -----
        static string F(float f) { return f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
        static bool Between(float x, float a, float b) { return x >= a && x <= b; }
        static bool SafeCanPay(Payable p) { try { return p.isActiveAndEnabled && p.CanPay(); } catch { return false; } }

        Payable Find(Payable[] arr, Func<Payable, bool> pred, float near = 0f)
        {
            Payable best = null; float bd = float.MaxValue;
            foreach (var p in arr)
            {
                if (p == null || OnCooldown(p.GetInstanceID()) || !pred(p)) continue;
                float d = Mathf.Abs(p.transform.position.x - near);
                if (d < bd) { bd = d; best = p; }
            }
            return best;
        }

        /// <summary>Outermost intact wall on a side (or the kingdom border if none).</summary>
        static float WallEdge(int side, float home)
        {
            float edge = home + side * 6f;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>())
            {
                float x = w.transform.position.x;
                if (side < 0 && x < edge) edge = x;
                if (side > 0 && x > edge) edge = x;
            }
            return edge;
        }

        /// <summary>Finished walls + construction sites on one side of home.</summary>
        static int WallsOnSide(int side, float home)
        {
            int n = 0;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>())
                if ((w.transform.position.x - home) * side > 0 && w.isIntact) n++;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<WorkableBuilding>())
                if ((b.transform.position.x - home) * side > 0 && b.name.Contains("Wall") && b.GetComponent<Wall>() == null) n++;
            return n;
        }

        static bool IsBuiltWall(Payable p)
        {
            // upgrade payables of existing walls sit on the Wall object itself
            return p.GetComponent<Wall>() != null || (p.transform.parent != null && p.transform.parent.GetComponent<Wall>() != null);
        }

        Payable WallSpotOnSide(Payable[] payables, float home, int side)
        {
            Payable best = null; float bd = float.MaxValue;
            var keep = GiftFoundation(payables, home);
            foreach (var p in payables)
            {
                if (p == keep) continue;
                if (!(p is PayableUpgrade) || !(p.name.StartsWith("Wall") || p.name.Contains("Wreck")) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                if (IsBuiltWall(p)) continue;
                float rel = (p.transform.position.x - home) * side;
                if (rel <= 0 || rel > 45f) continue;
                if (rel < bd) { bd = rel; best = p; }
            }
            return best;
        }

        static bool WallBetween(float a, float b)
        {
            float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>())
            {
                float x = w.transform.position.x;
                if (x > lo && x < hi && w.isIntact) return true;
            }
            return false;
        }

        Payable InnerWallSpot(Payable[] payables, float home)
        {
            // only build a new wall if there is no wall yet between it and home (keep the ring compact)
            Payable best = null; float bd = float.MaxValue;
            var keep = GiftFoundation(payables, home);
            foreach (var p in payables)
            {
                if (p == keep) continue;
                if (!(p is PayableUpgrade) || !p.name.StartsWith("Wall0") || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                float d = Mathf.Abs(p.transform.position.x - home);
                if (d > 45f) continue;
                if (d < bd) { bd = d; best = p; }
            }
            return best;
        }

        Payable EdgeTree(Payable[] payables, Kingdom k)
        {
            // choose a chop-able tree closest to the forest edge on the narrower side
            float home = k.campfirePosition;
            int side = (home - k.leftForest) < (k.rightForest - home) ? -1 : 1;
            for (int attempt = 0; attempt < 2; attempt++, side = -side)
            {
                float edge = side < 0 ? k.leftForest : k.rightForest;
                Payable best = null; float bd = float.MaxValue;
                foreach (var p in payables)
                {
                    if (!(p is PayableTree) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                    float x = p.transform.position.x;
                    if (side < 0 ? x > edge + 2f : x < edge - 2f) continue;
                    float d = Mathf.Abs(x - edge);
                    if (d > 25f) continue;
                    if (d < bd) { bd = d; best = p; }
                }
                if (best != null) return best;
            }
            return null;
        }

        // enemies that have not moved for a while are ignored (frozen leftovers after loading a save)
        static readonly Dictionary<int, Vector2> _enemyTrack = new Dictionary<int, Vector2>();   // id -> (last x, time it last moved)
        public static bool IsLiveEnemy(Enemy e)
        {
            if (e == null || !e.isActiveAndEnabled) return false;
            int id = e.GetInstanceID(); float x = e.transform.position.x; Vector2 t;
            if (!_enemyTrack.TryGetValue(id, out t) || Mathf.Abs(t.x - x) > 0.2f) { _enemyTrack[id] = new Vector2(x, Time.time); return true; }
            return Time.time - t.y < 20f;
        }

        Enemy NearestEnemy(float px)
        {
            Enemy best = null; float bd = float.MaxValue;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (!IsLiveEnemy(e)) continue;
                float d = Mathf.Abs(e.transform.position.x - px);
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }

        Coin NearestCoin(float px, float minX, float maxX)
        {
            Coin best = null; float bd = float.MaxValue;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>())
            {
                if (c.pickedUp || OnCooldown(c.GetInstanceID())) continue;
                if (c.droppedByPlayer && Time.time - _lastTap < 15f) continue;   // leave recruitment coins for the beggars
                float x = c.transform.position.x;
                if (x < minX || x > maxX) continue;
                if (c.transform.position.y < -3f) continue; // fell in water
                float d = Mathf.Abs(x - px);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        Beggar NearestBeggar(float px, float minX, float maxX)
        {
            Beggar best = null; float bd = float.MaxValue;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Beggar>())
            {
                float x = b.transform.position.x;
                if (x < minX || x > maxX || OnCooldown(b.GetInstanceID())) continue;
                bool nearPortal = false;
                foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>()) if (Mathf.Abs(po.transform.position.x - x) < 25f) nearPortal = true;
                if (nearPortal) continue;
                float d = Mathf.Abs(x - px);
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }
    }
}
