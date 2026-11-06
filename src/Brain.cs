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
        public string ConfigMode = "auto";   // auto | master | fire3 | pacifist
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

        void ChooseModeForRun(bool continuing)
        {
            if (ConfigMode == "main") ConfigMode = "master";
            string saved = null;
            try { if (continuing && System.IO.File.Exists(ModeFile)) saved = System.IO.File.ReadAllText(ModeFile).Trim(); } catch { }
            if (ConfigMode == "auto" && !string.IsNullOrEmpty(saved)) Mode = saved;          // loaded run keeps its mode
            else Mode = ConfigMode == "auto" ? Campaign.PickMode(PacifistEnabled) : ConfigMode;
            try { System.IO.File.WriteAllText(ModeFile, Mode); } catch { }
            Plugin.Log((continuing ? "Continuing run" : "New run") + " - mode: " + Mode + " | " + Campaign.Summary());
        }

        /// <summary>Short special runs end as soon as their goal is reached or impossible (only in auto).</summary>
        void CheckRunEnd()
        {
            if (ConfigMode != "auto" || _newGameAt > 0f) return;
            var am = Managers.achievements; if (am == null || am.flags == null) return;
            int dayC = am.counters[1];
            string reason = null;
            if (Mode == "fire3")
            {
                if (am.flags[3] && dayC < 2) reason = "fire too early";
                else if (am.flags[3] && dayC >= 2) reason = "fire run done";
            }
            else if (Mode == "army1")
            {
                if (am.counters[3] >= 8 && dayC == 0) reason = "8 archers on day 1";
                else if (dayC >= 1) reason = "army1: day 1 over (" + am.counters[3] + " archers)";
            }
            else if (Mode == "acre")
            {
                if (am.counters[7] >= AcreTarget) reason = "acre cleared (" + am.counters[7] + ")";
                else if (dayC > 6) reason = "acre run: too late (gap " + am.counters[7] + ")";
            }
            else if (Mode == "pacifist")
            {
                bool fiveOpen = Campaign.Missing(Campaign.FifthDays) && am.counters[5] == 0 && dayC < 5;
                bool eightOpen = Campaign.Missing(Campaign.EighthDay) && !am.flags[5] && dayC < 7;
                if (!fiveOpen && !eightOpen) reason = "pacifist run done (kills=" + am.counters[5] + ", coin on ground=" + am.flags[5] + ")";
            }
            if (reason != null)
            {
                bool success = reason.StartsWith("8 archers") || reason.StartsWith("acre cleared") || reason == "fire run done";
                if (!success) Campaign.Fail(Mode);
                Plugin.Log("Run finished: " + reason + " -> new game");
                _newGameAt = Time.unscaledTime + 6f;   // let the unlock coroutine finish first
            }
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
            if (dNow < _lastDaySeen || _lastDaySeen < 0) { _lastCastleUpgradeDay = -1; _cooldown.Clear(); _mapChecked = false; ChooseModeForRun(_lastDaySeen < 0 && dNow > 0); }
            if (dNow != _lastDaySeen && Managers.achievements != null && Managers.achievements.flags != null)
            {
                var am = Managers.achievements;
                Plugin.Log(string.Format("== Day {0} (Modus {1}) Flags: FreeWall={2} LitFire={3} Galloped={4} CoinOnGround={5} PortalsDestroyed={6} | Archers={7} Deer={8} Kills={9} Land={10}",
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
                if (AutoRestart && Time.unscaledTime - _gameOverAt > 10f && Mode != "survive" && Mode != "master" && !_failCounted) { Campaign.Fail(Mode); _failCounted = true; }
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
            CheckRunEnd();
            if (Mode == "pacifist") { ThinkPacifist(); return; }
            if (Mode == "army1") { ThinkArmy1(); return; }
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
            if (Mode == "survive" || Mode == "master")
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
                float reach = hour < k.dayEnd - 7f ? 100f : 35f;
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
            // Strategy: never drop a coin, never recruit (no archers = no kills). Defence comes only from the castle:
            // castle level 2 builds free walls on both sides. Payments into payables are fine (coins never touch the ground).
            var k = Managers.kingdom;
            float px = GameState.PX, home = k.campfirePosition;
            int coins = GameState.Coins;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            var enemies = UnityEngine.Object.FindObjectsOfType<Enemy>();
            float nearL = float.MaxValue, nearR = float.MaxValue;
            foreach (var e in enemies)
            {
                if (!IsLiveEnemy(e)) continue;
                float d = e.transform.position.x - px;
                if (WallBetween(px, e.transform.position.x)) continue;   // walled off -> harmless
                if (d < 0) nearL = Mathf.Min(nearL, -d); else nearR = Mathf.Min(nearR, d);
            }
            float nearest = Mathf.Min(nearL, nearR);
            _safeMode = true;
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            bool walled = WallsOnSide(-1, home) > 0 && WallsOnSide(1, home) > 0;

            // 1. danger (no wall in between): dodge, staying inside the walls if there are any
            if (nearest < 18f)
            {
                float lo = walled ? l + 1f : PortalLimit(-1), hi = walled ? r - 1f : PortalLimit(1);
                float target;
                if (nearL < nearR) target = Mathf.Min(px + Mathf.Min(14f, nearR - 6f), hi);
                else target = Mathf.Max(px - Mathf.Min(14f, nearL - 6f), lo);
                if (Mathf.Abs(target - px) < 1.5f) target = nearL < nearR ? hi : lo;
                Status = "Pacifist: dodge (L " + (nearL < 999 ? nearL.ToString("0") : "-") + " / R " + (nearR < 999 ? nearR.ToString("0") : "-") + ")";
                Push("goto " + F(target) + " 1 2"); return;
            }
            // 2. campfire, castle upgrades (= free walls at level 2), daily chest
            var fire = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && coins >= fire.price) { Status = "Pacifist: light fire"; PayAt(fire); return; }
            var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && !x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (castle != null && coins >= castle.price) { Status = "Pacifist: castle " + castle.name.Replace("(Clone)", ""); PayAt(castle); return; }
            if (k.isDaytime)
            {
                foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>())
                {
                    float cx = ch.transform.position.x;
                    if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && Mathf.Abs(cx - home) < 40f) { Cool(ch.GetInstanceID(), 120f); Status = "Pacifist: chest"; Push("goto " + F(cx) + " 0.3 10"); Push("wait 1"); return; }
                }
                var c = NearestCoin(px, home - 35f, home + 35f);
                if (c != null && !c.droppedByPlayer) { Cool(c.GetInstanceID(), 1e7f); Status = "Pacifist: coin"; Push("walkto " + F(c.transform.position.x) + " 0.3 10"); return; }
            }
            // 3. otherwise wait at the castle
            Status = "Pacifist: waiting at the castle (day " + GameState.Day + (walled ? ", walled" : "") + ")";
            if (Mathf.Abs(px - home) > 2f) Push("walkto " + F(home + 0.5f) + " 1 6"); else Push("wait 1");
        }

        // ---------------- army1: ON_THE_FIRST_DAY (8 archers before the first dawn) ----------------
        int _rerolls; bool _mapChecked;
        void ThinkArmy1()
        {
            var k = Managers.kingdom; var am = Managers.achievements;
            float px = GameState.PX, home = k.campfirePosition;
            int coins = GameState.Coins;
            _safeMode = true;
            // 1. map check: enough coins on the ground and in chests near the start? otherwise reroll the map
            if (!_mapChecked)
            {
                int avail = coins;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>()) if (!c.pickedUp && Mathf.Abs(c.transform.position.x - home) < 70f) avail++;
                foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>()) if (!ch.isEmpty && Mathf.Abs(ch.transform.position.x - home) < 70f) avail += ch.coins;
                _mapChecked = true;
                Plugin.Log("army1: coins reachable on this map = " + avail + " (reroll " + _rerolls + ")");
                if (avail < 24 && _rerolls < 40 && ConfigMode == "auto")
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
            if (archersMade + peasants + onDisplay < 8 && coins >= 3)
            {
                var b = NearestBeggar(px, home - 100f, home + 100f);
                if (b != null) { Cool(b.GetInstanceID(), 8f); _lastTap = Time.time; Status = "army1: recruit (" + archersMade + "/8)"; Push("goto " + F(b.transform.position.x) + " 0.8 25"); Push("tap 1"); Push("wait 2"); return; }
            }
            Status = "army1: waiting (" + archersMade + "/8 archers, " + coins + " coins)";
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
                    if (Mathf.Abs(outer - home) >= 14f) continue;
                    Payable best = null; float bd = float.MaxValue;
                    foreach (var p in payables)
                    {
                        if (!(p is PayableUpgrade) || !(p.name.StartsWith("Wall") || p.name.Contains("Wreck")) || IsBuiltWall(p) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                        float rel = (p.transform.position.x - home) * side;
                        if (rel < 14f || rel > 40f) continue;
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
            // charge: pay the border banner while knights with archers are ready
            if (morning)
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
                float d = Mathf.Abs(x - px);
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }
    }
}
