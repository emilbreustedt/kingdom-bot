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
            else if (RunType == "main" && dayC <= 2 && BootDays) phase = "boot";   // days 0-2: loot chests, recruit, bows (army1 logic) - guide: archers first
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
                // assault phase: once the last open "Safe in X" day has passed nothing more can be won in this run -> next attempt
                // hopeless pace for the open "Safe in X": the last portal must fall ~5 days before X (dead-portal queues drain)
                if (Mode == "master" && SafeTargetDay() > 0 && SafeTargetDay() <= 30)
                {
                    int dead = 0; foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>()) { var dm = po.GetComponent<Damageable>(); if (!po.outerSpawner && dm != null && dm.hitPoints <= 0) dead++; }
                    int killed = Mathf.Max(dead, 4 - LiveInnerPortals());
                    if ((dayC > 20 && killed == 0) || (dayC > 23 && killed < 3))
                    {
                        Plugin.Log("Run finished (pace: day " + dayC + ", portals destroyed " + killed + " - Safe in " + SafeTargetDay() + " out of reach) -> new game");
                        _newGameAt = Time.unscaledTime + 6f;
                        return;
                    }
                }
                if (Mode == "master" && SafeTargetDay() > 0 && dayC > SafeTargetDay() && !Campaign.Missing("SURVIVED_UNTIL_DAY_100"))
                {
                    Plugin.Log("Run finished (assault, day " + dayC + ", portals left) -> new game");
                    _newGameAt = Time.unscaledTime + 6f;
                }
                return;
            }
            bool possible;
            if (RunType == "early")
                // user: First Day Army first - while it is missing, every early run is a fast day-0 attempt (no feast days)
                bool firstDayFocus = Campaign.Missing(Campaign.FirstDay);
                possible = (dayC == 0 && Campaign.Missing(Campaign.FirstDay))
                        || (!firstDayFocus && dayC <= 3 && am.counters[4] < 10 && Campaign.Missing(Campaign.FourthDay))
                        || (!firstDayFocus && dayC <= 6 && Campaign.Missing(Campaign.Acre));
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
                Plugin.Log(string.Format("== POP day {0}: beggars {1} peasants {2} archers {3} (made {4}) workers {5} knights {6} coins {7}{8}", dNow, begs, peas, arcs, am.counters[3], wrks, kns, GameState.Player != null ? GameState.Coins : -1,
                    " | bunnies " + UnityEngine.Object.FindObjectsOfType<Bunny>().Length + " tallGrass " + GrassCount() +
                    (PortalsAllDestroyed() ? " | PORTALS DESTROYED, finalWave=" + (Managers.enemies != null && Managers.enemies.finalWaveSpawned) + " enemies=" + UnityEngine.Object.FindObjectsOfType<Enemy>().Length + " bosses=" + UnityEngine.Object.FindObjectsOfType<Boss>().Length + " ogres=" + UnityEngine.Object.FindObjectsOfType<Ogre>().Length : "")));
                // destroyed portals that do not crumble keep "PortalsDestroyed" from being set: log their state
                foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
                {
                    var dm = po.GetComponent<Damageable>();
                    if (dm == null || dm.hitPoints > 0) continue;
                    var q = Acc.Get<object>(po, "_spawnQueue") as System.Collections.ICollection;
                    Plugin.Log("DEAD PORTAL x" + po.transform.position.x.ToString("0") + " outer=" + po.outerSpawner + " opened=" + Acc.Get<bool>(po, "_opened")
                        + " crumbleAfterSpawn=" + Acc.Get<bool>(po, "_crumbleAfterSpawn") + " closeAfterSpawn=" + Acc.Get<bool>(po, "_closeAfterSpawn")
                        + " queue=" + (q != null ? q.Count.ToString() : "?") + " isDead=" + dm.isDead + " active=" + po.isActiveAndEnabled
                        + " finalWave=" + (Managers.enemies != null && Managers.enemies.finalWaveSpawned)
                        + " enemies=" + UnityEngine.Object.FindObjectsOfType<Enemy>().Length + PortalFields(po) + EnemyMgrFields());
                }
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
            if (!p.hasCrown)
            {
                // the crown can lie on the ground (day 22: the game went on for days without it) -> fetch it
                Transform best = null; float bd = 1e6f; var sb = new System.Text.StringBuilder();
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    if (t.name.IndexOf("rown", System.StringComparison.OrdinalIgnoreCase) < 0 || t.IsChildOf(p.transform) || t.GetComponent<RectTransform>() != null) continue;
                    if (_crownLogAt < Time.time) sb.Append(" " + t.name + "@" + t.position.x.ToString("0") + "/" + t.position.y.ToString("0"));
                    float d = Mathf.Abs(t.position.x - GameState.PX);
                    if (d < bd) { bd = d; best = t; }
                }
                if (_crownLogAt < Time.time) { _crownLogAt = Time.time + 60f; Plugin.Log("CROWN candidates:" + sb); }
                if (best != null) { Status = "Crown lost: fetch it at x" + best.position.x.ToString("0"); Push("goto " + F(best.position.x) + " 0.3 20"); return; }
                Status = "Crown lost!"; Push("wait 1"); return;
            }

            var k = Managers.kingdom;
            int day = GameState.Day;
            UpdatePhase();
            CheckRunEnd();
            if (Mode == "pacifist") { ThinkPacifist(); return; }
            if (Mode == "army1") { ThinkArmy1(false); return; }
            if (Mode == "boot") { if (GameState.Player != null && (int)Managers.game.gameState == 2 && HorseStep()) return; ThinkArmy1(false, true); return; }
            if (Mode == "feast") { ThinkArmy1(true); return; }   // feast: maximum archers early - they hunt deer on their own in safe daytime
            if (RunType == "main" && GameState.Player != null && (int)Managers.game.gameState == 2 && GameState.Day == 0 && MainMapCheck()) return;
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && HorseStep()) return;
            if (Mode == "master" && GameState.Player != null && (int)Managers.game.gameState == 2)
            {
                TrackCharge();
                DeadPortalTrace();
                // master = record-run defence (LegacyBrain) + portal assault steps on top
                var kk = Managers.kingdom;
                int d = GameState.Day; float hr = GameState.Hour, pxx = GameState.PX, hm = kk.campfirePosition;
                int bud = GameState.Coins - ((Managers.achievements != null && Managers.achievements.flags != null && Managers.achievements.flags[7]) ? 8 : (d >= 10 || (d >= 1 && hr >= kk.dayEnd - 8f)) ? 4 : 0);   // final wave: 8 (day-28 game over with 2 coins at the fire)   // crown guard: a hit with an empty purse costs the crown (day-24 game over)
                int arch = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
                // track boss nights: the morning after one is the recovery day (forum tip: best time for an assault)
                if (!kk.isDaytime || hr >= kk.dayEnd - 1f) { foreach (var bo in UnityEngine.Object.FindObjectsOfType<Boss>()) { var be = bo.GetComponent<Enemy>(); if (be != null && IsLiveEnemy(be)) _bossNightDay = hr < 12f ? d - 1 : d; } }
                bool night = hr >= kk.dayEnd - 1.5f || !kk.isDaytime;
                var en = NearestEnemy(pxx);
                bool danger = en != null && Mathf.Abs(en.transform.position.x - pxx) < 12f && !WallBetween(pxx, en.transform.position.x);
                _armyFirst = false;
                // assault only once the defence is solid (the castle rush + shields cost ~40 coins)
                bool ready = (d >= 12 && (arch >= 15 || UnityEngine.Object.FindObjectsOfType<Knight>().Length > 0)) || d >= 20 || (TenthRush && d >= 4 && arch >= 8);   // a knight waiting for the 15th archer wasted days 11-15
                if (!night && DrainStep(hm, hr, pxx)) return;
                if (ready && !night && !danger && AssaultStep(UnityEngine.Object.FindObjectsOfType<Payable>(), d, hr, bud, arch, hm, pxx)) return;
            }
            // (master now shares the survival basis below; economy/defence extras are no longer used)
            // survival: stone walls decide late survival (L3/L4 = 200/300 HP vs 50 HP). Follow a wall schedule and save for
            // the next step during the day if needed; otherwise the record strategy plays as usual.
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && AvoidIntruder()) return;
            // income: farms + merchant (archers barely hunt in the late game - waves keep the kingdom "unsafe")
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && GameState.Day >= 10 && UseFarms && UnityEngine.Object.FindObjectsOfType<Archer>().Length >= 18 && EconomyStep()) return;
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && WallSchedule()) return;
            if ((Mode == "survive" || Mode == "master") && GameState.Player != null && (int)Managers.game.gameState == 2 && GrassStep()) return;
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
            bool evening = hour >= k.dayEnd - 1.5f - Mathf.Max(0f, Mathf.Abs(GameState.PX - k.campfirePosition) - 15f) / 30f ||   /* far out: leave earlier (gallop ~35 units per game hour); day 5 game over 127 out at dusk */ (!k.isDaytime && !(day == 0 && hour < 12f));
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
            foreach (var ch in AllChests())
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
            foreach (var ch in AllChests())
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
            _farmWallTrees = 0;   // new run
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
            // stone tech (mason shrine) is needed for castle level 5 = knights: the shrine must be reachable, i.e. closer
            // than the nearest inner portal on its side (with a margin) and not too far
            float shrineD = 1e6f; bool shrineOk = false;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Payable>())
            {
                if (!p.name.StartsWith("Mason")) continue;
                float d = p.transform.position.x - home;
                float portalD = d < 0 ? left : right;
                if (Mathf.Abs(d) < shrineD) { shrineD = Mathf.Abs(d); shrineOk = Mathf.Abs(d) <= 130f && Mathf.Abs(d) < portalD - 25f; }
            }
            bool good = left >= MinPortalDistance && right >= MinPortalDistance && camps >= 3 && shrineOk;
            Plugin.Log(string.Format("main map check: nearest portals L {0:0} / R {1:0}, camps within 90: {2}, mason shrine {3:0} ({4}) -> {5} (reroll {6})", left, right, camps, shrineD, shrineOk ? "ok" : "unreachable", good ? "keep" : "reroll", _mainRerolls));
            if (good || _mainRerolls >= 30) { _mainRerolls = 0; return false; }
            _mainRerolls++; _lastDaySeen = -1; _pl.Queue.Clear();
            Acc.Call(Managers.game, "Restart");
            Status = "main: reroll map"; Push("wait 2"); return true;
        }

        // ---------------- army1: ON_THE_FIRST_DAY (8 archers before the first dawn) ----------------
        /// <summary>All chests incl. far ones (deactivated objects are missed by FindObjectsOfType).</summary>
        static System.Collections.Generic.List<Chest> AllChests()
        {
            var l = new System.Collections.Generic.List<Chest>();
            foreach (var c in Resources.FindObjectsOfTypeAll<Chest>())
                if (c != null && c.hideFlags == HideFlags.None && c.gameObject.hideFlags == HideFlags.None && c.transform.root.gameObject.activeSelf) l.Add(c);
            return l;
        }
        /// <summary>Knights stay on the side they were created on; once that side has no living inner portal they idle.
        /// Move them to the side where portals remain (the knight walks over to that wall).</summary>
        void RebalanceKnights(float home)
        {
            int aliveL = 0, aliveR = 0;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                var dm = po.GetComponent<Damageable>();
                if (po.outerSpawner || (dm != null && dm.hitPoints <= 0)) continue;
                if (po.transform.position.x < home) aliveL++; else aliveR++;
            }
            // stale targets: a knight whose target spawner is already destroyed (or on the other side after a switch) stands
            // forever with _shouldCharge=True (day 26-36: target Portal@134 dead, last portal never charged)
            var fTs = typeof(Knight).GetField("_targetSpawner", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var fTc = typeof(Knight).GetField("_targetCoin", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>())
            {
                try
                {
                    var ts = fTs != null ? fTs.GetValue(kn) as Component : null;
                    if (ts == null) continue;
                    var dm = ts.GetComponent<Damageable>();
                    bool dead = dm != null && dm.hitPoints <= 0;
                    bool otherSide = (ts.transform.position.x - home) * (kn.transform.position.x - home) < 0;
                    if (!dead && !otherSide) continue;
                    // point it at the nearest living inner portal on its side (null alone: the game re-picked the dead one)
                    Component live = null; float ld = 1e6f; int ks = kn.transform.position.x < home ? -1 : 1;
                    foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
                    {
                        var pdm = po.GetComponent<Damageable>();
                        if (po.outerSpawner || (pdm != null && pdm.hitPoints <= 0) || (po.transform.position.x - home) * ks <= 0) continue;
                        float pd = Mathf.Abs(po.transform.position.x - kn.transform.position.x);
                        if (pd < ld) { ld = pd; live = po.GetComponent(fTs.FieldType); }
                    }
                    fTs.SetValue(kn, live);
                    if (fTc != null) fTc.SetValue(kn, null);
                    Plugin.Log("KNIGHT target reset -> " + (live != null ? "Portal@" + live.transform.position.x.ToString("0") : "none") + " (" + (dead ? "dead" : "other side") + " " + ts.name.Replace("(Clone)", "") + "@" + ts.transform.position.x.ToString("0") + ") x" + kn.transform.position.x.ToString("0"));
                }
                catch (System.Exception ex) { Plugin.Log("KNIGHT target reset failed: " + ex.Message); }
            }
            if ((aliveL == 0) == (aliveR == 0)) return;
            string target = aliveL == 0 ? "Right" : "Left";
            foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>())
            {
                try
                {
                    var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
                    System.Reflection.FieldInfo f = null; System.Reflection.PropertyInfo pr = null;
                    for (var t = kn.GetType(); t != null && f == null && pr == null; t = t.BaseType)
                    {
                        f = t.GetField("side", bf) ?? t.GetField("_side", bf);
                        if (f == null) { pr = t.GetProperty("side", bf); if (pr != null && !pr.CanWrite) pr = null; }
                    }
                    object cur = f != null ? f.GetValue(kn) : pr != null ? pr.GetValue(kn, null) : null;
                    if (cur == null) { if (_knightMoveWarned++ == 0) Plugin.Log("KNIGHT move: no writable side member found"); continue; }
                    if (cur.ToString() == target) continue;
                    var ft = f != null ? f.FieldType : pr.PropertyType;
                    if (f != null) f.SetValue(kn, System.Enum.Parse(ft, target)); else pr.SetValue(kn, System.Enum.Parse(ft, target), null);
                    Plugin.Log("KNIGHT moved " + cur + " -> " + target + " (no living portal on its side) x" + kn.transform.position.x.ToString("0"));
                }
                catch (System.Exception ex) { Plugin.Log("KNIGHT move failed: " + ex.Message); }
            }
        }
        /// <summary>Number of living inner portals.</summary>
        static int LiveInnerPortals()
        {
            int n = 0;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>()) { var dm = po.GetComponent<Damageable>(); if (!po.outerSpawner && (dm == null || dm.hitPoints > 0)) n++; }
            return n;
        }
        public int FinalArchers = 16, FinalBank = 10;   // the final wave (70-95 enemies) beat 11-14 archers with no coins twice
        /// <summary>Charge window (7h .. dusk-7h) while a knight could charge: the king should stay home.</summary>
        bool KnightReadyWindow()
        {
            var k = Managers.kingdom; float h = GameState.Hour;
            if (k == null || !k.isDaytime || h < 6f || h >= k.dayEnd - 7f) return false;
            foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>()) { try { if ((bool)Acc.Call(kn, "CanCharge")) return true; } catch { } }
            return false;
        }
        public bool UseDrain = false;
        float _drainNext;
        /// <summary>
        /// Experiment (DPQ trace): the last dead portal of a side keeps ~130 enemies queued - nightly waves of that side refill it
        /// and it never crumbles (no final wave). Portals release their queue as defenders when our units come within
        /// defenseTriggerRange (8). Ride close in the morning with a full purse, then gallop back to the archers.
        /// </summary>
        bool DrainStep(float home, float hour, float px)
        {
            var k = Managers.kingdom;
            if (!UseDrain) return false;   // tested 2026-11-07: queue 65 -> 63 in a day, the king lost 20 coins per ride
            if (Time.time < _drainNext || !k.isDaytime || hour < 7f || hour > k.dayEnd - 9f || GameState.Coins < 20) return false;
            Portal target = null; int bestQ = 0;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                var dm = po.GetComponent<Damageable>();
                if (po.outerSpawner || dm == null || dm.hitPoints > 0) continue;
                int side = po.transform.position.x < home ? -1 : 1;
                bool liveSameSide = false;
                foreach (var p2 in UnityEngine.Object.FindObjectsOfType<Portal>()) { var d2 = p2.GetComponent<Damageable>(); if (!p2.outerSpawner && (d2 == null || d2.hitPoints > 0) && (p2.transform.position.x - home) * side > 0) liveSameSide = true; }
                if (liveSameSide) continue;   // other dead portals drain on their own
                var q = Acc.Get<object>(po, "_spawnQueue") as System.Collections.ICollection;
                int qc = q != null ? q.Count : 0;
                if (qc > bestQ) { bestQ = qc; target = po; }
            }
            if (target == null || bestQ < 5) return false;
            var en = NearestEnemy(px);
            if (en != null && (en.GetComponent<Boss>() != null || en.GetComponent<Ogre>() != null)) return false;
            _drainNext = Time.time + 120f;   // at most every ~half day
            float tx = target.transform.position.x, s2 = tx < home ? -1f : 1f;
            Status = "Drain dead portal x" + tx.ToString("0") + " (queue " + bestQ + ")";
            Plugin.Log("DRAIN start: portal x" + tx.ToString("0") + " queue " + bestQ + " coins " + GameState.Coins);
            _safeMode = true;
            Push("goto " + F(tx - s2 * 6f) + " 1 " + F(20f + Mathf.Abs(tx - px) * 0.5f));
            Push("wait 1");
            Push("goto " + F(home) + " 2 " + F(20f + Mathf.Abs(tx - home) * 0.5f));
            return true;
        }
        float _dpqNext;
        /// <summary>Hourly: queue of every dead (uncrumbled) portal + live enemies, to see when the queue grows/shrinks.</summary>
        void DeadPortalTrace()
        {
            if (Time.time < _dpqNext) return;
            _dpqNext = Time.time + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                var dm = po.GetComponent<Damageable>();
                if (po.outerSpawner || dm == null || dm.hitPoints > 0) continue;
                var q = Acc.Get<object>(po, "_spawnQueue") as System.Collections.ICollection;
                sb.Append(" x" + po.transform.position.x.ToString("0") + ":" + (q != null ? q.Count.ToString() : "?"));
            }
            if (sb.Length == 0) return;
            int en = 0, near = 0; float kx = 0;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>()) if (IsLiveEnemy(e)) en++;
            Plugin.Log("DPQ d" + GameState.Day + " h" + GameState.Hour.ToString("0") + (Managers.kingdom.isDaytime ? " day" : " night") + sb + " enemies=" + en);
        }
        float _chargeTrackUntil, _chargeTrackNext, _chargePaidAt = -1e6f;
        /// <summary>After a charge: log every knight once per game hour (x, fsm state, escort, coins) to see where charges fail.</summary>
        void TrackCharge()
        {
            if (Time.time > _chargeTrackUntil || Time.time < _chargeTrackNext) return;
            _chargeTrackNext = Time.time + 10f;
            var sb = new System.Text.StringBuilder("CHARGE TRACK d" + GameState.Day + " h" + GameState.Hour.ToString("0.0") + ":");
            foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>())
            {
                object cur = null; try { var fsm = Acc.Get<object>(kn, "_fsm"); cur = fsm.GetType().GetField("current").GetValue(fsm); } catch { }
                int ar = -1; try { var a = Acc.Get<object>(kn, "_archers"); ar = (int)a.GetType().GetProperty("Count").GetValue(a, null); } catch { }
                int kc = -1; try { kc = Acc.Wallet(kn).coins; } catch { }
                var tg = new System.Text.StringBuilder();
                foreach (var f in typeof(Knight).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    string fn = f.Name.ToLower();
                    if (!(fn.Contains("wall") || fn.Contains("target") || fn.Contains("border") || fn.Contains("portal") || fn.Contains("dest") || fn.Contains("charge"))) continue;
                    object v = null; try { v = f.GetValue(kn); } catch { }
                    var comp = v as Component;
                    tg.Append(" " + f.Name + "=" + (comp != null ? comp.name.Replace("(Clone)", "") + "@" + comp.transform.position.x.ToString("0") : (v is float || v is int || v is bool || (v != null && v.GetType().IsEnum)) ? v.ToString() : (v == null ? "null" : v.GetType().Name)));
                }
                sb.Append(" [x" + kn.transform.position.x.ToString("0") + " " + cur + " esc=" + ar + " c=" + kc + tg + "]");
            }
            int en = 0; foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>()) if (IsLiveEnemy(e)) en++;
            sb.Append(" enemies=" + en);
            Plugin.Log(sb.ToString());
        }
        float _crownLogAt;
        /// <summary>Numeric fields of a portal (spawn timing) and of the EnemyManager (limits) for the "queue never drains" question.</summary>
        static string PortalFields(Portal po)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in po.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                if ((f.FieldType == typeof(float) || f.FieldType == typeof(int)) && !f.Name.StartsWith("<")) { try { sb.Append(" " + f.Name + "=" + f.GetValue(po)); } catch { } }
            return sb.ToString();
        }
        static float _emLogDay = -1;
        static string EnemyMgrFields()
        {
            var em = Managers.enemies; if (em == null || _emLogDay == GameState.Day) return "";
            _emLogDay = GameState.Day;
            var sb = new System.Text.StringBuilder(" | EM:");
            foreach (var f in em.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool)) { try { sb.Append(" " + f.Name + "=" + f.GetValue(em)); } catch { } }
            return sb.ToString();
        }
        int _finalPrepDay = -1;
        int _knightDiagDay = -1, _knightMoveWarned;
        /// <summary>A trip to x and back home cannot be finished before dusk (day-2 game over 78 out at night).</summary>
        static bool TooLateFor(float x, float home)
        {
            var k = Managers.kingdom; if (k == null) return false;
            float hoursNeeded = 2f * Mathf.Abs(x - home) / 30f + 2f;   // there and back at ~30 units per game hour, plus a margin
            return !k.isDaytime || GameState.Hour + hoursNeeded > k.dayEnd - 1f;
        }
        int _rerolls; bool _mapChecked;
        public float Army1Range = 130f;
        public bool BootDays = true;
        public float Army1Check = 80f;    // day 0 is short: 24 coins (8 recruits + 8 bows) must lie within ~80 of the camp, otherwise reroll   // guide: run to the camps on both sides (four camps incl. the town centre) and loot chests on the way
        void ThinkArmy1(bool feast, bool boot = false)
        {
            var k = Managers.kingdom; var am = Managers.achievements;
            float px = GameState.PX, home = k.campfirePosition;
            int coins = GameState.Coins;
            _safeMode = true;
            // 1. map check: enough coins on the ground and in chests near the start? otherwise reroll the map
            if (!_mapChecked && !feast && !boot)
            {
                int avail = coins;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>()) if (!c.pickedUp && Mathf.Abs(c.transform.position.x - home) < Army1Check) avail++;
                foreach (var ch in AllChests()) if (!ch.isEmpty && Mathf.Abs(ch.transform.position.x - home) < Army1Check) avail += ch.coins;
                _mapChecked = true;
                Plugin.Log("army1: coins reachable on this map = " + avail + " (reroll " + _rerolls + ")");
                if (avail < 25 && _rerolls < 40 && NormalizeRunType(ConfigMode) == "auto")
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
            if ((feast || boot) && (!k.isDaytime || GameState.Hour >= k.dayEnd - 1.5f))
            { Status = "Feast: night at the fire"; if (Mathf.Abs(px - home) > 2f) Push("goto " + F(home) + " 1 6"); else Push("wait 1"); return; }
            // feast: herding has priority once a small army exists (guide: herd deer towards the archers)
            if (feast && am != null && am.counters[3] >= 6 && FeastStep()) return;
            // 3. coins & chests close by (far ones only when coins are needed - see below)
            foreach (var ch in AllChests())
            {
                float cx = ch.transform.position.x;
                if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && Mathf.Abs(cx - px) < 25f) { Cool(ch.GetInstanceID(), 60f); Status = "army1: chest near (" + ch.coins + ")"; Push("goto " + F(cx) + " 0.3 15"); Push("wait 1"); return; }
            }
            var coin = NearestCoin(px, px - 25f, px + 25f);
            if (coin != null && !(coin.droppedByPlayer && Time.time - _lastTap < 15f)) { Cool(coin.GetInstanceID(), 1e7f); Status = "army1: coin"; Push("goto " + F(coin.transform.position.x) + " 0.3 15"); return; }
            // 4. fire (opens the bow shop)
            var fire = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && coins >= fire.price) { Status = "army1: light fire"; PayAt(fire); return; }
            // 5. bows for every idle peasant, otherwise recruit until 8 archers are possible
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            int archersMade = am != null ? am.counters[3] : 0;
            PayableShop bow = null; int onDisplay = 0;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; }
            if (boot && GameState.Day >= 1 && peasants > onDisplay && UnityEngine.Object.FindObjectsOfType<Worker>().Length < 2)
            {
                PayableShop hm = null; foreach (var py in payables) { var sh = py as PayableShop; if (sh != null && sh.name.Contains("Hammer")) hm = sh; }
                if (hm != null && SafeCanPay(hm) && coins >= hm.price) { Status = "boot: buy hammer"; PayAt(hm); return; }
            }
            if (peasants > onDisplay && bow != null && SafeCanPay(bow) && coins >= bow.price) { Status = "army1: buy bow (" + archersMade + "/8)"; PayAt(bow); return; }
            int target = boot ? 12 : feast ? 15 : 8;
            if (archersMade + peasants + onDisplay < target && coins >= 3)
            {
                var b = NearestBeggar(px, home - Army1Range, home + Army1Range);
                if (b != null && !TooLateFor(b.transform.position.x, home)) { Cool(b.GetInstanceID(), 8f); _lastTap = Time.time; Status = "army1: recruit (" + archersMade + "/8)"; Push("goto " + F(b.transform.position.x) + " 0.8 " + F(10f + Mathf.Abs(b.transform.position.x - px) * 0.6f)); Push("tap 1"); Push("wait 2"); return; }
            }
            if (feast && FeastStep()) return;   // guide: herd deer towards the archers, 10th deer exactly on day 4
            // far chests and coins (the bot starts with 0 coins: 8 archers need ~24)
            Chest far = null; float fd = 1e6f;
            foreach (var ch in AllChests())
            {
                float cx = ch.transform.position.x;
                if (ch.isEmpty || OnCooldown(ch.GetInstanceID()) || Mathf.Abs(cx - home) > Army1Range) continue;
                if (Mathf.Abs(cx - px) < fd) { fd = Mathf.Abs(cx - px); far = ch; }
            }
            if (far != null && !TooLateFor(far.transform.position.x, home)) { Cool(far.GetInstanceID(), 60f); float cx = far.transform.position.x; Status = "army1: chest (" + far.coins + ")"; Push("goto " + F(cx) + " 0.3 " + F(10f + Mathf.Abs(cx - px) * 0.6f)); Push("wait 1"); return; }
            var coinFar = NearestCoin(px, home - Army1Range, home + Army1Range);
            if (coinFar != null && !TooLateFor(coinFar.transform.position.x, home) && !(coinFar.droppedByPlayer && Time.time - _lastTap < 15f)) { Cool(coinFar.GetInstanceID(), 1e7f); Status = "army1: coin far"; Push("goto " + F(coinFar.transform.position.x) + " 0.3 " + F(10f + Mathf.Abs(coinFar.transform.position.x - px) * 0.6f)); return; }
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
            // guide: ride out past a whole group of deer (7-10), then walk back - the herd flees ahead into the archers
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            int[] cnt = new int[2]; float[] far = { home, home };
            foreach (var d in Resources.FindObjectsOfTypeAll<Deer>())
            {
                if (d == null || d.hideFlags != HideFlags.None || !d.transform.root.gameObject.activeSelf) continue;
                float x = d.transform.position.x;
                if ((x > l && x < r) || Mathf.Abs(x - home) > 110f) continue;
                int si = x < home ? 0 : 1;
                cnt[si]++;
                if (Mathf.Abs(x - home) > Mathf.Abs(far[si] - home)) far[si] = x;
            }
            int s2 = cnt[0] >= cnt[1] ? 0 : 1;
            if (cnt[s2] == 0) return false;
            int side = s2 == 0 ? -1 : 1;
            float behind = far[s2] + side * 8f;
            Status = "Feast: herd " + cnt[s2] + " deer (" + deer + "/10, day counter " + dayC + ", allowed " + allowed + ")";
            _safeMode = true;
            if (_herdOut && (px - home) * side >= (behind - home) * side - 2f) _herdOut = false;   // reached the far end: drive back
            if (!_herdOut && Mathf.Abs(px - home) < 10f) _herdOut = true;                        // back home: next round
            if (_herdOut) { Push("goto " + F(behind) + " 1.5 " + F(10f + Mathf.Abs(behind - px) * 0.5f)); return true; }
            Push("walkto " + F(home + side * 6f) + " 2 " + F(10f + Mathf.Abs(px - home)));      // walk (not gallop) so the herd stays ahead
            return true;
        }
        bool _herdOut = true;

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
                if (shrine != null && coins >= shrine.price + 3 && Mathf.Abs(shrine.transform.position.x - home) < 220f)
                { Status = "Defence: mason shrine (stone)"; Cool(shrine.GetInstanceID(), 30f); Push("goto " + F(PayX(shrine)) + " 0.3 120"); Push("payid " + shrine.GetInstanceID() + " " + F(PayX(shrine))); Push("goto " + F(home) + " 1 40"); return true; }
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
            bool noBuilders = UnityEngine.Object.FindObjectsOfType<Worker>().Length < 1;   // walls need builders - but the army must still grow
            int coins = GameState.Coins - (Mode == "master" ? AssaultReserve(false) : 0);   // castle 5 / shields / banner first
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            Payable item = null; string what = null;
            // 0. farms = the only income that scales; they come first from day 6
            if (UseFarms && day >= 10 && UnityEngine.Object.FindObjectsOfType<Archer>().Length >= 18) { var farm = FarmTarget(payables, home); if (farm != null) { item = farm; what = farm.name.StartsWith("Tree") ? "farmland (cut tree)" : "farm"; } }
            // 0b. a farm outside the walls loses its farmers every night: next wall goes up beyond the farm
            if (false && UseFarms && item == null) { var fw = FarmWall(payables, home); if (fw != null && fw.name.StartsWith("Tree") && coins >= fw.price + 4) _farmWallTrees++;
            if (fw != null) { item = fw; what = (fw.name.StartsWith("Tree") ? "farm wall (cut tree" : "wall beyond the farm (") + " @" + F(fw.transform.position.x) + " edges " + F(WallEdge(-1, home)) + "/" + F(WallEdge(1, home)) + ")"; } }
            // 1. stone tech first (wall levels 3+ need it)
            if (item == null && Managers.techUpgrades != null && !Managers.techUpgrades.stoneBuilding)
            {
                var shrine = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Mason") && SafeCanPay(x));
                if (shrine != null && Mathf.Abs(shrine.transform.position.x - home) < 220f && hour < k.dayEnd - 9f) { item = shrine; what = "mason shrine (stone)"; }
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
            // (guides say "no towers" for fast wins, but without them a boss broke in on day 27 and took the crown)
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
            bool tooEarly = item != null && item.name.StartsWith("Mason") && hour < 7f;   // trolls still about at dawn
            if (noBuilders && item != null && !item.name.StartsWith("Mason")) item = null;   // nothing gets built without workers
            // guide: archers first. While the army is below today's target and coins are plentiful, recruit/arm nearby
            // beggars before long trips (the mason shrine trip took 10 game hours with 38 coins in the purse)
            {
                int arNow = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
                if (arNow < Mathf.Min(3 * day + 6, LiveInnerPortals() == 1 ? FinalArchers : 20) && hour < k.dayEnd - 5f && GameState.Coins >= (item != null ? item.price : 0) + 6)
                {
                    int pe = UnityEngine.Object.FindObjectsOfType<Peasant>().Length, disp = 0; PayableShop bw = null, hm = null;
                    foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; disp += sh.GetItemCount(); if (sh.name.Contains("Bow")) bw = sh; if (sh.name.Contains("Hammer")) hm = sh; }
                    int wk = UnityEngine.Object.FindObjectsOfType<Worker>().Length;
                    if (pe > disp + ShieldsWaiting() && hm != null && wk < (day >= 6 ? 3 : 2) && SafeCanPay(hm)) { Status = "Army first: buy hammer"; PayAt(hm); return true; }
                    if (pe > disp + ShieldsWaiting() && bw != null && SafeCanPay(bw)) { Status = "Army first: buy bow"; PayAt(bw); return true; }
                    if (pe < disp + 3 && pe < 5)
                    {
                        var bg = NearestBeggar(px, home - 100f, home + 100f);
                        if (bg != null) { Cool(bg.GetInstanceID(), 10f); Status = "Army first: recruit"; Recruit(bg); return true; }
                    }
                }
            }
            if (item != null && coins >= item.price + 4 && !tooEarly)   // keep a small purse: coins are knocked out before the crown
            {
                Status = "Defence: " + what + " (" + item.price + ")";
                if (item.name.StartsWith("Mason")) { Cool(item.GetInstanceID(), 30f); Push("goto " + F(PayX(item)) + " 0.3 120"); Push("payid " + item.GetInstanceID() + " " + F(PayX(item))); Push("goto " + F(home) + " 1 40"); }
                else { if (item.name.StartsWith("Tree") || what.StartsWith("wall beyond the farm")) Cool(item.GetInstanceID(), 120f); PayAt(item); }
                return true;
            }
            // while saving the army must keep growing: bows for idle peasants, then recruit beggars (hand-over)
            int archersNow = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
            int armyCap = item != null && item.name.StartsWith("Mason") ? 20 : (Mode == "master" && GameState.Day >= 12 ? AssaultArmyCap : 30);   // guide: archers first (20-30 by day 7)   // stone tech first: castle 5 / knights depend on it
            int armyCoins = (archersNow < MinChargeArchers ? GameState.Coins : coins) - (day >= 10 ? 4 : 0);   // crown guard   // a weak army is rebuilt even from the assault reserve
            if (archersNow < Mathf.Min(3 * day + 6, armyCap) && hour < k.dayEnd - 5f)
            {
                int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length, onDisplay = 0; PayableShop bow = null;
                PayableShop hammer = null;
                foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; if (sh.name.Contains("Hammer")) hammer = sh; }
                int workersNow = UnityEngine.Object.FindObjectsOfType<Worker>().Length;
                if (peasants > onDisplay + ShieldsWaiting() && hammer != null && workersNow < (day >= 6 ? 3 : 2) && SafeCanPay(hammer) && armyCoins >= hammer.price) { Status = "Army while saving: buy hammer"; PayAt(hammer); return true; }   // walls need builders
                if (peasants > onDisplay + ShieldsWaiting() && bow != null && SafeCanPay(bow) && armyCoins >= bow.price) { Status = "Army while saving: buy bow"; PayAt(bow); return true; }
                if (peasants < onDisplay + 2 && peasants < 4 && armyCoins >= 3)
                {
                    var b = NearestBeggar(px, home - 150f, home + 150f);
                    if (b != null) { Cool(b.GetInstanceID(), 10f); Status = "Army while saving: recruit"; Recruit(b); return true; }
                }
            }
            if (item == null) return false;                 // nothing to save for: the rest of the brain spends
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
            if (b == null) return true;
            int n;
            if (!(payableName.Length > 6 && int.TryParse(payableName.Substring(6, 1), out n))) return true;
            if (b.Mode != "master") return n <= 3;
            // master: castle 4 cuts the daily chest (5-7 -> 4, castle 5 -> 1). Bank first, then buy castle 4, castle 5
            // and the shields in one go.
            if (n == 4) return GameState.Coins >= b.BankTarget();
            return n <= 5;
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
            if (merchant != null && UseMerchant && coins >= merchant.price + 2) { Status = "Economy: pay the merchant (" + merchant.price + ")"; PayAt(merchant); return true; }
            PayableShop scythe = null; int onDisplay = 0;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Scythe")) scythe = sh; }
            if (scythe == null) return false;                               // castle level 2+ needed
            int farmsBuilt = 0;
            foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>()) if (fh.level > 0 && Mathf.Abs(fh.transform.position.x - home) < 60f) farmsBuilt++;
            bool farmBuilt = farmsBuilt > 0;
            int farmersNow = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;
            if (farmsBuilt == 0 || (farmsBuilt == 1 && farmersNow >= 3 && day >= 12))
            {
                var farm = FarmTarget(payables, home);
                if (farm != null && !farm.name.StartsWith("Farmhouse")) farm = null;
                if (farm != null && coins >= farm.price + 1) { Status = "Economy: build farm (" + farm.price + ")"; PayAt(farm); return true; }
                if (!farmBuilt) return false;
            }
            int farmers = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            if (farmers < Mathf.Min(FarmerTarget, 4 * farmsBuilt) && (!FarmsExposed(home) || _farmWallTrees >= FarmWallTreeCap) && peasants > onDisplay && SafeCanPay(scythe) && coins >= scythe.price + 1)
            { Status = "Economy: buy scythe (farmers " + farmers + ")"; PayAt(scythe); return true; }
            return false;
        }

        public int KnightArmor = 2;   // knights pick up coins themselves (often 8/8 in the logs); our coins are needed for banners
        public bool UseMerchant = false;
        // Farms (tree clearing + wall beyond the farm + scythes) cost more than they earned in tests: the army stayed
        // at ~12 archers by day 30 instead of 25-30. Off by default.
        public bool UseFarms = true;   /* retry 2026-11-07: only from day 10 with 18+ archers, no farm walls (those cleared beggar camps); forum: 1 farm + 4 farmers carries the late game */     // tested twice: farm runs had ~9 archers on day 20 (vs ~25) - clearing for the farm wall destroys beggar camps
        public bool FarmClearTrees = true;   // only a few trees (<= 4) for a spot right in front of the walls
        public int FarmerTarget = 4;
        public int FarmWallTreeCap = 6;
        int _farmWallTrees;   // 4 coins every other day = ~half of the late-game income; not worth it
        /// THE_TENTH_DAY (a portal destroyed by day 10, guide: castle max + 1 knight, charge on day 8 after the night):
        /// while it is missing the master run rushes castle 5 and one knight before day 11.
        public bool TenthRush { get { return Mode == "master" && GameState.Day <= 10 && UnityEngine.Object.FindObjectsOfType<Archer>().Length >= 12; } }   // kept after the achievement: the early castle/knight rush gave 3 portals by day 13 (Safe in 35 run); without it knights came on day ~22   // castle-first with a small army starved it (10 archers on day 10) and never made day 10
        int KnightGoal { get { return TenthRush ? 1 : KnightTarget; } }
        int ChargeArchers { get { return TenthRush ? 10 : MinChargeArchers; } }
        public bool UseCatapults = false;   // guide + logs: big enemies throw our catapult boulders back over the wall (378 archer hits in one run)
        public int KnightTarget = 3;   // 3 knights destroy portals reliably; every shield (6) is a banner payment less at castle 5 (~1 coin/day)

        /// <summary>Recruit a beggar: hand-over (coin never touches the ground) only in the challenge run (Eighth Day);
        /// elsewhere a normal tap, which works more reliably.</summary>
        void Recruit(Beggar b)
        {
            // far beggars (camps 80+ away): gallop close first - walking (speed 2) with a 25 s timeout never arrived
            float bx = b.transform.position.x, dist = Mathf.Abs(bx - GameState.PX);
            if (dist > 12f) Push("goto " + F(bx + (GameState.PX < bx ? -4f : 4f)) + " 1.5 " + F(Mathf.Max(30f, dist * 0.6f)));
            if (RunType == "challenge") { Push("walkto " + F(bx) + " 0.3 25"); Push("handtap " + b.GetInstanceID()); Push("wait 1.5"); }
            else
            {
                Push("walkto " + F(bx) + " 0.8 25"); Push("tap 1"); Push("wait 2.5");
                // the whole camp in one trip: other beggars standing close by get a coin too (up to 3 per trip)
                if (GameState.Coins >= 4)
                {
                    int extra = 0;
                    foreach (var o in UnityEngine.Object.FindObjectsOfType<Beggar>())
                    {
                        if (o == b || extra >= 2 || GameState.Coins < 4 + 2 * extra) continue;
                        float ox = o.transform.position.x;
                        if (Mathf.Abs(ox - bx) > 10f || OnCooldown(o.GetInstanceID())) continue;
                        Cool(o.GetInstanceID(), 10f); extra++;
                        Push("walkto " + F(ox) + " 0.8 10"); Push("tap 1"); Push("wait 2.5");
                    }
                }
            }
        }
        public int AssaultArmyCap = 25;
        public int MasterRunEndDay = 40;   // after day 40 no "Safe in X" is reachable (runs with all portals destroyed continue)
        public int MinChargeArchers = 12;   // 16 blocked charges for days in weaker runs; escort = 4 per knight
        public float GrassStrip = 30f;   // tree-free strip in front of the outer walls (rabbits / hunting income)

        /// <summary>
        /// Horse care (tips from the user): the fast horse (3 coins) gallops a bit faster and much longer; a horse that
        /// stands still on grass eats and recovers stamina faster. Buy the fast horse early if it is near, and let the
        /// horse graze when stamina is low and nothing is happening nearby.
        /// </summary>
        /// <summary>
        /// Hunting income (tip from the user): rabbits live in tall grass, which only grows on cleared land. Keep a strip
        /// of ~30 units in front of each outer wall free of trees so rabbits appear within the archers' reach. At most one
        /// tree per side and day, only from spare coins.
        /// </summary>
        bool GrassStep()
        {
            if (KnightReadyWindow()) return false;   // the king outside the walls blocked the morning charge (days 18-21)
            var k = Managers.kingdom;
            int day = GameState.Day; float hour = GameState.Hour, px = GameState.PX, home = k.campfirePosition;
            if (day < 10 || !k.isDaytime || hour < 8f || hour > k.dayEnd - 6f) return false;
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length < 2) return false;
            int spare = GameState.Coins - (Mode == "master" ? AssaultReserve(true) : 0);
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            for (int side = -1; side <= 1; side += 2)
            {
                if (OnCooldown(777100 + side) || !FieldClear(home, side)) continue;
                float outer = WallEdge(side, home), forest = side < 0 ? k.leftForest : k.rightForest;
                if ((forest - outer) * side >= GrassStrip) continue;
                Payable tree = null; float td = float.MaxValue;
                foreach (var p in payables)
                {
                    if (!p.name.StartsWith("Tree") || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                    float rel = (p.transform.position.x - outer) * side;
                    if (rel < 0f || rel > GrassStrip + 10f) continue;
                    if (rel < td) { td = rel; tree = p; }
                }
                if (tree == null || spare < tree.price + 4) continue;
                Cool(777100 + side, 200f);   // ~ one game day
                Cool(tree.GetInstanceID(), 120f);
                Status = "Grass for rabbits " + (side < 0 ? "left" : "right") + ": cut tree (forest " + ((forest - outer) * side).ToString("0") + " from the wall)";
                PayAt(tree); return true;
            }
            return false;
        }

        bool HorseStep()
        {
            var k = Managers.kingdom; var p = GameState.Player;
            int day = GameState.Day; float hour = GameState.Hour, px = GameState.PX, home = k.campfirePosition;
            if (!k.isDaytime || hour > k.dayEnd - 4f) return false;
            var en = NearestEnemy(px);
            bool quiet = en == null || Mathf.Abs(en.transform.position.x - px) > 40f;
            // 1. fast horse: days 0-3, within 90 of the camp, 3 coins + purse
            bool ridingFast = false; foreach (var st0 in p.GetComponentsInChildren<Steed>()) if (st0.name.Contains("Fast")) ridingFast = true;
            if (!ridingFast && quiet && !OnCooldown(777001))
            {
                foreach (var st in UnityEngine.Object.FindObjectsOfType<Steed>())
                {
                    if (!st.name.Contains("Fast") || st.transform.parent == p.transform) continue;
                    // any day (user: it rode past the black horse without buying it): early near the camp, later whenever we pass close by
                    float sx = st.transform.position.x;
                    bool near = (day <= 3 && Mathf.Abs(sx - home) <= 90f) || Mathf.Abs(sx - px) <= 30f;
                    if (!near || !SafeCanPay(st) || GameState.Coins < st.price + (day >= 10 ? 6 : 2)) continue;
                    Cool(777001, 60f);
                    Status = "Fast horse (" + st.price + ")";
                    PayAt(st); return true;
                }
            }
            // 2. graze: low stamina, daytime, quiet -> stand still a moment so the horse eats
            if (p.stamina < 0.25f && quiet && day >= 1 && !OnCooldown(777002))
            {
                Cool(777002, 40f);
                Status = "Horse grazing (stamina " + p.stamina.ToString("0.00") + ")";
                Push("wait 4"); return true;
            }
            return false;
        }
        public float ForwardGap = 9999f;   // forward walls towards living portals made runs worse (archers spread thin, coins spent); only used for dead, uncrumbled portals

        /// <summary>The game's PortalsDestroyed flag (all inner portals gone): the run continues to the final wave / win.</summary>
        /// <summary>Largest X of a still missing "Safe in X" (a win on day d unlocks every X >= d); 0 = all done.</summary>
        static int SafeTargetDay()
        {
            int t = 0;
            foreach (int x in new[] { 25, 30, 35, 40 }) if (Campaign.Missing("SAFE_IN_" + x)) t = x;
            return t;
        }

        static int GrassCount()
        {
            try { var gm = UnityEngine.Object.FindObjectOfType<GrassManager>(); return gm != null ? gm.tallGrassPatchesCount : -1; } catch { return -1; }
        }

        static bool PortalsAllDestroyed()
        {
            try { var f = Managers.achievements.flags; return f != null && f.Length > 7 && f[7]; } catch { return false; }
        }

        /// <summary>Every inner portal is at 0 HP (some may not have crumbled yet).</summary>
        static bool InnerPortalsDead()
        {
            int alive = 0, total = 0;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                if (po.outerSpawner) continue;
                total++;
                var dm = po.GetComponent<Damageable>();
                if (dm == null || dm.hitPoints > 0) alive++;
            }
            return alive == 0;
        }
        int _bossNightDay = -10, _chargeDiagDay = -1;

        /// <summary>No live enemy outside the walls within 60 units of either wall (a charge would run into it).</summary>
        static bool FieldClear(float home, int side)
        {
            // a few trolls in front of the wall are no match for knights with escorts; only many enemies or a big one block
            float edge = WallEdge(side, home);
            int n = 0;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (!IsLiveEnemy(e)) continue;
                float rel = (e.transform.position.x - edge) * side;   // > 0: outside that wall
                if (rel <= -3f || rel >= 60f) continue;
                if (e.GetComponent<Boss>() != null || e.GetComponent<Ogre>() != null || e.GetComponent<Squid>() != null) return false;   // a squid throws boulders all day from ~50 out
                n++;
            }
            // several knights with escorts break through small trolls; a dead portal next to the wall keeps spawning (day 22-25: clearL never true)
            return n <= (UnityEngine.Object.FindObjectsOfType<Knight>().Length >= 2 ? 10 : 4);
        }   // escorts (4 per knight) + enough defenders at home   // after day 40 no "Safe in X" is reachable
        public int BurstCoins = 50;   // castle 4 (8) + castle 5 (9) + 2 shields (12) + 4 banners (16) + purse; castle 5 earns ~1 coin/day, every charge (4) must come from the bank
        /// <summary>Bank needed before castle 4: the full bank early, a smaller one from day 24 (late income ~= upkeep, the bank plateaus ~40).</summary>
        public int BankTarget() { int d = GameState.Day; if (TenthRush) return 9 + 6 + 4 + 3; return d >= 24 ? 38 : 40; }   /* castle 4 (8) + castle 5 (9) + 2 shields (12) + banner (4) + purse; 60 delayed the knights by days */   // the assault (3 shields + 5-8 charges) is paid from the bank: castle 5 earns ~1 coin/day

        /// <summary>Coins the record strategy must leave alone while the assault saves for castle 4/5 and shields.</summary>
        public static int ShieldsWaitingPublic() { return ShieldsWaiting(); }

        public static int AssaultReserve() { return AssaultReserve(true); }
        /// <param name="includeBank">false: the 35-coin bank before castle 4 is not reserved (walls still get built
        /// while banking); castle 5, shields and the banner are.</param>
        public static int AssaultReserve(bool includeBank)
        {
            var b = Plugin.Instance != null ? Plugin.Instance.Brain : null;
            if (b == null || b.Mode != "master" || GameState.Day < 12) return 0;
            // final wave / all portals down: nothing left to charge - every coin goes into bows and walls
            // (day 27-36: 9 peasants idle without bows with 40 coins in the purse, final wave never beaten)
            var amR = Managers.achievements;
            if ((amR != null && amR.flags != null && amR.flags.Length > 7 && amR.flags[7]) || (Managers.enemies != null && Managers.enemies.finalWaveSpawned)) return 0;
            // a weak base comes first: no builders or too few archers -> the reserve must not block hammers/bows/recruits
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length < 2 || UnityEngine.Object.FindObjectsOfType<Archer>().Length < b.MinChargeArchers) return 0;
            if (LiveInnerPortals() == 1 && UnityEngine.Object.FindObjectsOfType<Archer>().Length >= b.FinalArchers - 6) return b.FinalBank + 4;   // bank for the final wave before the last charge
            Payable castle = null;
            foreach (var x in UnityEngine.Object.FindObjectsOfType<Payable>())
                if (x is PayableUpgrade && x.name.StartsWith("Castle") && CastleIndex(x.name) >= 4 && CastleIndex(x.name) <= 5) { castle = x; break; }
            if (castle != null) return CastleIndex(castle.name) == 4 ? (includeBank ? b.BankTarget() : 0) : castle.price + 2 * 6 + 4 * 4 + 3;   // castle 5 + 2 shields + 4 banners + purse
            int missing = Mathf.Max(0, 2 - UnityEngine.Object.FindObjectsOfType<Knight>().Length - ShieldsWaiting());
            int inner = 0; foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>()) { var dm = po.GetComponent<Damageable>(); if (!po.outerSpawner && (dm == null || dm.hitPoints > 0)) inner++; }
            return missing * 6 + Mathf.Max(1, inner) * 4 + 3;   // shields for 2 knights + one banner per remaining portal + purse
        }       // castle 4 (8) + castle 5 (12) + 2 shields (12) + purse   // while saving for castle/shields the army only grows to this size (else nothing is ever saved)

        /// <summary>Farmhouse payable to build next (first farm once scythes are sold, second one from day 12), or null.</summary>
        Payable FarmTarget(Payable[] payables, float home)
        {
            bool scytheShop = false;
            foreach (var py in payables) if (py is PayableShop && py.name.Contains("Scythe")) scytheShop = true;
            if (!scytheShop) return null;
            int farmsBuilt = 0;
            foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>()) if (fh.level > 0 && Mathf.Abs(fh.transform.position.x - home) < 60f) farmsBuilt++;
            int farmers = UnityEngine.Object.FindObjectsOfType<Farmer>().Length;
            if (farmsBuilt > 0) return null;   // one farm with 4 farmers (guide: "you don't need more than 4 farmers")
            // only a farmhouse spot inside the walls or at most 20 in front of them (the wall then goes beyond the farm);
            // no forest clearing for a farm (that cost the army a lot in the first farm test)
            float lw = WallEdge(-1, home), rw = WallEdge(1, home);
            var farm = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Farmhouse") && SafeCanPay(x)
                                           && x.transform.position.x > lw - 20f && x.transform.position.x < rw + 20f, home);
            if (farm != null) return farm;
            if (!FarmClearTrees) return null;
            // farmhouse spots only become payable inside the buildable (cleared) land: cut the trees between the camp
            // and the nearest spot, fewest trees first
            Payable bestTree = null; int bestCount = 99;
            foreach (var fp in payables)
            {
                if (!(fp is PayableUpgrade) || !fp.name.StartsWith("Farmhouse")) continue;
                float fx = fp.transform.position.x;
                if (fx < lw - 20f || fx > rw + 20f) continue;   // only spots right in front of the walls
                float lo = Mathf.Min(home, fx) - 2f, hi = Mathf.Max(home, fx) + 2f;
                int count = 0; Payable nearest = null; float nd = float.MaxValue;
                foreach (var t in payables)
                {
                    if (!t.name.StartsWith("Tree")) continue;
                    float tx = t.transform.position.x;
                    if (tx < lo || tx > hi) continue;
                    count++;
                    if (SafeCanPay(t) && !OnCooldown(t.GetInstanceID()) && Mathf.Abs(tx - home) < nd) { nd = Mathf.Abs(tx - home); nearest = t; }
                }
                if (nearest != null && count <= 4 && count < bestCount) { bestCount = count; bestTree = nearest; }   // a few trees at most
            }
            return bestTree;
        }

        /// <summary>Shields lying at a shield shop (bought, not yet taken by a peasant).</summary>
        static int ShieldsWaiting()
        {
            int n = 0;
            var shops = UnityEngine.Object.FindObjectsOfType<PayableShield>();
            foreach (var ds in UnityEngine.Object.FindObjectsOfType<DroppableShield>())
            {
                if (ds == null || ds.GetComponentInParent<Character>() != null) continue;
                // only shields at a shield shop count: one dropped by a dead knight far outside blocked bow purchases forever
                bool atShop = false;
                foreach (var sh in shops) if (Mathf.Abs(sh.transform.position.x - ds.transform.position.x) < 10f) atShop = true;
                if (atShop) n++;
            }
            return n;
        }

        /// <summary>
        /// Next assault purchase not affordable yet -> save for it (army growth still continues). Order: castle up to
        /// level 5 (shield shops), then shields until KnightTarget knights exist.
        /// </summary>
        bool AssaultSaving(Payable[] payables, int day, float hour, float home, float px)
        {
            var k = Managers.kingdom;
            if (!k.isDaytime || hour > k.dayEnd - 4f) return false;
            int coins = GameState.Coins;
            // a bought shield is a tool: only a peasant can take it (and becomes a knight). Without idle peasants it
            // just lies at the shop -> recruit a beggar for it.
            if (ShieldsWaiting() > 0)
            {
                int peasantsNow = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
                if (peasantsNow < ShieldsWaiting() && coins >= 4 && hour < k.dayEnd - 5f)
                {
                    var b = NearestBeggar(px, home - 150f, home + 150f);
                    if (b != null) { Cool(b.GetInstanceID(), 10f); Status = "Assault: recruit a peasant for the shield"; Recruit(b); return true; }
                }
            }
            // a ready knight goes first: saving for the next shield (but after recruiting for waiting shields) must not block the morning charge
            if (hour >= 7f && hour < k.dayEnd - 7f) foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>()) { try { if ((bool)Acc.Call(kn, "CanCharge")) return false; } catch { } }
            Payable item = null; string what = null;
            var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x) && CastleIndex(x.name) <= 5);
            int target = 0;
            if (castle != null)
            {
                item = castle; what = "castle " + castle.name.Replace("(Clone)", "");
                target = CastleIndex(castle.name) == 4 ? BankTarget() : castle.price + 3;
                if (CastleIndex(castle.name) == 4) what = "castle 4+5 and shields";
            }
            else if (UnityEngine.Object.FindObjectsOfType<Knight>().Length + ShieldsWaiting() < 2)   // with 2 knights the charges come first; more shields only from spare coins
            {
                var sh = Find(payables, x => x is PayableShield && SafeCanPay(x), home);
                if (sh != null) { item = sh; what = "shield"; }
            }
            if (target == 0 && item != null) target = item.price + 3;
            if (item == null || coins >= target) return false;     // nothing to save for / affordable: AssaultStep buys it
            // keep the army growing while saving (same as the wall schedule)
            int archersNow = UnityEngine.Object.FindObjectsOfType<Archer>().Length;
            var payablesAll = payables;
            if (archersNow < Mathf.Min(2 * day + 4, AssaultArmyCap) && hour < k.dayEnd - 5f)
            {
                int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length, onDisplay = 0; PayableShop bow = null;
                foreach (var py in payablesAll) { var shp = py as PayableShop; if (shp == null) continue; onDisplay += shp.GetItemCount(); if (shp.name.Contains("Bow")) bow = shp; }
                if (peasants > onDisplay + ShieldsWaiting() && bow != null && SafeCanPay(bow) && coins >= bow.price) { Status = "Army while saving: buy bow"; PayAt(bow); return true; }
                if (peasants < onDisplay + 2 && peasants < 4 && coins >= 4)
                {
                    var b = NearestBeggar(px, home - 150f, home + 150f);
                    if (b != null) { Cool(b.GetInstanceID(), 10f); Status = "Army while saving: recruit"; Recruit(b); return true; }
                }
            }
            var c = NearestCoin(px, home - 40f, home + 40f);
            if (c != null) { Cool(c.GetInstanceID(), 1e7f); Status = "Saving for " + what + ": coin"; Push("goto " + F(c.transform.position.x) + " 0.3 15"); return true; }
            Status = "Saving for " + what + " (" + coins + "/" + target + ")";
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            Push("walkto " + F(px < home ? r - 1f : l + 1f) + " 1 20");
            return true;
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

            // farm first (wall schedule saves for it), then the castle
            if (UseFarms && day >= 10 && day < 14 && UnityEngine.Object.FindObjectsOfType<Knight>().Length == 0 && UnityEngine.Object.FindObjectsOfType<Archer>().Length >= 18 && FarmTarget(payables, home) != null) return false;
            // castle first from day 8 (level 5 = shield shops) - only when attacking; survive upgrades normally
            if (assault && day >= (TenthRush ? 4 : 8) && archers >= 6)
            {
                var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x) && CastleAllowed(x.name));
                if (castle != null && k.isDaytime && hour < k.dayEnd - 5f && budget >= castle.price + 3) { Status = "Assault prep: castle " + castle.name.Replace("(Clone)", "") + " (" + castle.price + ")"; PayAt(castle); return true; }
            }
            // statues (guide): archer statue buffs all archers, worker statue buffs the walls; a charge lasts some days,
            // 5-6 coins per level. Archer statue before charges, worker statue for the final assault phase.
            if (assault && morning && hour >= 7f && archers >= MinChargeArchers && !_armyFirst && (UnityEngine.Object.FindObjectsOfType<Knight>().Length > 0 || (am != null && am.flags != null && am.flags[7])))   /* day>=18 without knights ate the castle-5 savings (5 coins/day) */
            {
                int innerAlive = 0; foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>()) { var dm = po.GetComponent<Damageable>(); if (!po.outerSpawner && (dm == null || dm.hitPoints > 0)) innerAlive++; }
                bool lateGame = innerAlive <= 1 || (am != null && am.flags != null && am.flags[7]);   // worker statue (walls) for the last portal + final wave
                foreach (var su in UnityEngine.Object.FindObjectsOfType<Statue>())
                {
                    bool archerStatue = su.name.Contains("Archer");
                    if (!archerStatue && !lateGame) continue;
                    bool knightReady = false; foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>()) { try { if ((bool)Acc.Call(kn, "CanCharge")) knightReady = true; } catch { } }
                    if (knightReady && !(am != null && am.flags != null && am.flags[7])) continue;   // a statue trip costs the morning: charges first
                    if (!SafeCanPay(su) || OnCooldown(su.GetInstanceID()) || Mathf.Abs(su.transform.position.x - home) > 200f) continue;
                    if (budget - AssaultReserve(false) < su.price + 8) continue;
                    Cool(su.GetInstanceID(), 60f);
                    Status = "Statue: charge " + su.name.Replace("(Clone)", "") + " (" + su.price + ")";
                    Push("goto " + F(PayX(su)) + " 0.3 120"); Push("payid " + su.GetInstanceID() + " " + F(PayX(su))); Push("goto " + F(home) + " 1 40");
                    return true;
                }
            }
            // stone tech from the mason shrine (morning trip)
            if (Managers.techUpgrades != null && !Managers.techUpgrades.stoneBuilding && archers >= 8 && morning && !_armyFirst)
            {
                var shrine = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Mason") && SafeCanPay(x));
                if (shrine != null && hour >= 7f && budget >= shrine.price + 3 && Mathf.Abs(shrine.transform.position.x - home) < 220f)
                { Status = "Assault prep: mason shrine (stone)"; Cool(shrine.GetInstanceID(), 30f); Push("goto " + F(PayX(shrine)) + " 0.3 120"); Push("payid " + shrine.GetInstanceID() + " " + F(PayX(shrine))); Push("goto " + F(home) + " 1 40"); return true; }
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
                    bool bannerHere = false;   // the banner already stands on this side (e.g. outer wall is a wreck) -> nothing to do
                    foreach (var bo in UnityEngine.Object.FindObjectsOfType<PayableBorder>()) if ((bo.transform.position.x - home) * side > 0) bannerHere = true;
                    if (bannerHere) continue;
                    if (!FieldClear(home, side)) continue;   // no wall work out front while enemies are about
                    Payable best = null; float bd = float.MaxValue;
                    foreach (var p in payables)
                    {
                        if (!(p is PayableUpgrade) || !(p.name.StartsWith("Wall") || p.name.Contains("Wreck")) || IsBuiltWall(p) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                        float rel = (p.transform.position.x - home) * side;
                        if (rel < 11f || rel > 40f) continue;
                        if (rel < bd) { bd = rel; best = p; }
                    }
                    if (best != null && budget >= best.price + 1) { Status = "Outer wall " + (side < 0 ? "left" : "right") + " (banner range)"; PayAt(best); Cool(best.GetInstanceID(), 120f); return true; }
                    if (best == null)
                    {
                        // wall spots are only buildable on cleared land: cut the nearest tree on this side first
                        Payable tree = null; float td = float.MaxValue;
                        foreach (var p in payables)
                        {
                            if (!p.name.StartsWith("Tree") || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                            float rel = (p.transform.position.x - home) * side;
                            if (rel < 8f || rel > 40f) continue;
                            if (rel < td) { td = rel; tree = p; }
                        }
                        if (tree != null && budget >= tree.price + 1) { Status = "Banner range " + (side < 0 ? "left" : "right") + ": cut tree"; Cool(tree.GetInstanceID(), 60f); PayAt(tree); return true; }
                    }
                }
            }

            // forward launch point (forum tip): knights die on the long way to far portals -> move the outer wall (and
            // with it the banner) towards the nearest intact portal on a side once the gap is large
            if (assault && !finalWave && (UnityEngine.Object.FindObjectsOfType<Knight>().Length >= 1 || InnerPortalsDead()) && UnityEngine.Object.FindObjectsOfType<Worker>().Length >= 2 && hour >= 12f && hour < k.dayEnd - 5f)   // afternoons: mornings are for charges
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    float portalRel = float.MaxValue; bool deadTarget = false;
                    foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
                    {
                        var dm = po.GetComponent<Damageable>();
                        if (po.outerSpawner) continue;
                        bool dead = dm != null && dm.hitPoints <= 0;
                        float r = (po.transform.position.x - home) * side;
                        if (r > 0 && r < portalRel) { portalRel = r; deadTarget = dead; }
                    }
                    float outerRel = (WallEdge(side, home) - home) * side;
                    // dead but not crumbled: enemies camping there block its collapse -> bring the archers (outermost wall) close
                    // the last intact portal is usually the far one the knights never reach: move the launch point towards it
                    int intact = 0;
                    foreach (var po2 in UnityEngine.Object.FindObjectsOfType<Portal>()) { var dm2 = po2.GetComponent<Damageable>(); if (!po2.outerSpawner && (dm2 == null || dm2.hitPoints > 0)) intact++; }
                    float gapNeeded = deadTarget ? 45f : intact == 1 ? 90f : ForwardGap;
                    if (portalRel == float.MaxValue || portalRel - outerRel < gapNeeded) continue;
                    Payable spot = null, tree = null; float sd = float.MaxValue, td = float.MaxValue;
                    foreach (var p in payables)
                    {
                        if (OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                        float r = (p.transform.position.x - home) * side;
                        if (r <= outerRel + 8f || r >= portalRel - (deadTarget ? 25f : 60f)) continue;
                        if (p is PayableUpgrade && p.name.StartsWith("Wall") && !IsBuiltWall(p) && r < sd) { sd = r; spot = p; }
                        else if (p.name.StartsWith("Tree") && r < td) { td = r; tree = p; }
                    }
                    var pick = spot ?? (portalRel - outerRel < 140f ? tree : null);   // clearing a whole forest for a far portal eats the charge money
                    if (pick != null && budget - AssaultReserve(false) >= pick.price + 2)
                    {
                        Status = "Forward wall " + (side < 0 ? "left" : "right") + (spot != null ? "" : ": cut tree") + " (portal gap " + (portalRel - outerRel).ToString("0") + ")";
                        Cool(pick.GetInstanceID(), 90f); PayAt(pick); return true;
                    }
                }
            }
            if (assault && morning && hour >= 7f && _knightDiagDay != day)
            {
                var kd = UnityEngine.Object.FindObjectsOfType<Knight>();
                if (kd.Length > 0)
                {
                    _knightDiagDay = day;
                    var sb = new System.Text.StringBuilder("KNIGHTS d" + day + " h" + hour.ToString("0.0") + ":");
                    foreach (var kn in kd) { bool cc = false; try { cc = (bool)Acc.Call(kn, "CanCharge"); } catch { } int kc = -1; try { kc = Acc.Wallet(kn).coins; } catch { } sb.Append(" [x" + kn.transform.position.x.ToString("0") + " canCharge=" + cc + " coins=" + kc + "]"); }
                    Plugin.Log(sb.ToString());
                }
            }
            if (assault && !finalWave && AssaultSaving(payables, day, hour, home, px)) return true;
            // catapults (workshop, castle level 3+): defensive artillery at the border, pushed there by workers
            if (UnityEngine.Object.FindObjectsOfType<Worker>().Length >= 2)
            {
                var ws = Find(payables, x => x is PayableWorkshop && SafeCanPay(x), home);
                if (UseCatapults && ws != null && hour >= 12f && budget - AssaultReserve(true) >= ws.price + 3) { Status = "Buy catapult (" + ws.price + ")"; PayAt(ws); return true; }
            }
            if (finalWave || !assault) return false;   // survive mode / all inner portals gone: only defend
            RebalanceKnights(home);
            var knights = UnityEngine.Object.FindObjectsOfType<Knight>();
            // ready to charge: at least 2 knights, each with a coin of armour (waiting for 4 fully armed knights took
            // until the next boss night, which killed them before any charge)
            // charge every morning as soon as a knight is ready (the game's own CanCharge: escort archers assembled).
            // Coins in the purse are a bonus, not a requirement - after a charge they are gone and refilling rarely works.
            bool armed = false;
            foreach (var kn in knights) { try { if ((bool)Acc.Call(kn, "CanCharge")) armed = true; } catch { } }
            // charge: pay the border banner while knights with archers are ready (and armed)
            bool recovery = _bossNightDay == day - 1;
            if (morning && hour >= 7f && knights.Length > 0 && _chargeDiagDay != day)
            {
                _chargeDiagDay = day;
                var bl = new System.Text.StringBuilder();
                foreach (var bo in UnityEngine.Object.FindObjectsOfType<PayableBorder>()) bl.Append(" " + bo.transform.position.x.ToString("0") + (SafeCanPay(bo) ? "" : "!"));
                Plugin.Log("CHARGE CHECK d" + day + " h" + hour.ToString("0.0") + ": knights " + knights.Length + " armed(CanCharge)=" + armed + " archers " + archers + "/" + ChargeArchers
                    + " recovery=" + recovery + " clearL=" + FieldClear(home, -1) + " clearR=" + FieldClear(home, 1) + " budget " + budget + " banners" + bl);
            }
            bool lastPortalReady = LiveInnerPortals() != 1 || (archers >= FinalArchers && budget >= FinalBank);
            if (!lastPortalReady && morning && _finalPrepDay != day) { _finalPrepDay = day; Plugin.Log("FINAL PREP d" + day + ": last portal waits - archers " + archers + "/" + FinalArchers + " budget " + budget + "/" + FinalBank); }
            if (lastPortalReady && k.isDaytime && hour < k.dayEnd - 7f && hour >= 7f && armed && archers >= ChargeArchers &&   /* coins arrive during the day (hunting): morning-only charges never had the banner price */ (recovery || FieldClear(home, -1) || FieldClear(home, 1)))
            {
                // banner on the side of the nearest intact inner portal (each charge goes to that side's portal)
                float bestPd = float.MaxValue; int pside = 0;
                foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
                {
                    var dm = po.GetComponent<Damageable>();
                    if (po.outerSpawner || (dm != null && dm.hitPoints <= 0)) continue;   // knights only charge living portals (CanCharge is false otherwise)
                    float pd = Mathf.Abs(po.transform.position.x - home);
                    int ps = po.transform.position.x < home ? -1 : 1;
                    if (!FieldClear(home, ps)) continue;             // enemies (e.g. boulder throwers) in front of that wall: other side
                    // that side needs a payable banner and a ready knight standing there (day 9/10: the nearest portal was
                    // right, the only knight and the only payable banner left -> no charge, Tenth Day missed)
                    bool bannerOk = false; foreach (var bo in UnityEngine.Object.FindObjectsOfType<PayableBorder>()) if ((bo.transform.position.x - home) * ps > 0 && SafeCanPay(bo)) bannerOk = true;
                    bool knightOk = false; foreach (var kn in knights) { try { if ((kn.transform.position.x - home) * ps > 0 && (bool)Acc.Call(kn, "CanCharge")) knightOk = true; } catch { } }
                    if (!bannerOk || !knightOk) continue;
                    if (pd < bestPd) { bestPd = pd; pside = ps; }
                }
                if (pside == 0) return false;
                var border = Find(payables, x => x is PayableBorder && SafeCanPay(x) && (x.transform.position.x - home) * pside > 0, px);
                if (border != null && budget >= border.price)
                {
                    // charge tracking: a knight first walks to every coin near his wall (hours of delay -> night fight).
                    // Pick those coins up ourselves first.
                    int picked = 0;
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>())
                    {
                        if (c.pickedUp || picked >= 4) continue;
                        float cx = c.transform.position.x; bool nearKnight = false;
                        foreach (var kn in knights) if ((kn.transform.position.x - home) * pside > 0 && Mathf.Abs(kn.transform.position.x - cx) < 12f) nearKnight = true;
                        if (!nearKnight) continue;
                        Push("goto " + F(cx) + " 0.3 10"); picked++;
                    }
                }
                if (border != null && budget >= border.price) {   /* budget already keeps the 4-coin crown guard */ Status = "Assault: knights CHARGE (" + border.name.Replace("(Clone)", "") + ")"; Cool(border.GetInstanceID(), 60f); _chargeTrackUntil = Time.time + 24f * 10f; _chargePaidAt = Time.time; _chargeTrackNext = 0f; PayAt(border); return true; }
            }
            // shields -> knights
            var shield = Find(payables, x => x is PayableShield && SafeCanPay(x), home);
            if (shield != null && UnityEngine.Object.FindObjectsOfType<Knight>().Length + ShieldsWaiting() < KnightGoal && budget - (UnityEngine.Object.FindObjectsOfType<Knight>().Length >= 2 ? AssaultReserve(false) : 0) >= shield.price + 3 + (UnityEngine.Object.FindObjectsOfType<Knight>().Length >= 1 ? 4 : 0)) { Status = "Assault: buy shield (" + shield.price + ")"; PayAt(shield); return true; }
            // arm the knights: coins in a knight's purse are knocked out before he loses his shield
            Knight poor = null; int poorCoins = 99;
            foreach (var kn in knights)
            {
                int kc = 0; try { kc = Acc.Wallet(kn).coins; } catch { }
                if (kc < KnightArmor && kc < poorCoins && !OnCooldown(kn.GetInstanceID())) { poorCoins = kc; poor = kn; }
            }
            if (poor != null && k.isDaytime && !KnightReadyWindow() && Time.time - _chargePaidAt > 24f * 10f && (poorCoins < 1 ? budget >= 2 : budget >= 4 + 3))   /* charge tracking: knights sat in GrabCoin for hours after the banner was paid */   // keep the banner price once every knight has a coin
            {
                Cool(poor.GetInstanceID(), 6f);
                Status = "Assault: arm knight (" + poorCoins + "/" + KnightArmor + " coins)";
                Push("walkto " + F(poor.transform.position.x) + " 0.6 20"); Push("tap 2"); Push("wait 1.5"); return true;
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
            bool evening = hour >= k.dayEnd - 1.5f - Mathf.Max(0f, Mathf.Abs(GameState.PX - k.campfirePosition) - 15f) / 30f ||   /* far out: leave earlier (gallop ~35 units per game hour); day 5 game over 127 out at dusk */ (!k.isDaytime && !(day == 0 && hour < 12f));
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            if (evening && (px < l - 0.5f || px > r + 0.5f)) { Status = "Interrupt: dusk"; return true; }
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 9f && !WallBetween(px, e.transform.position.x))
            {
                // paying the charge banner (outside the wall): dead portals keep spawning trolls, so small ones must not
                // abort it (day 21-23: every banner payment was interrupted, the knights never left)
                bool big = e.GetComponent<Boss>() != null || e.GetComponent<Ogre>() != null || e.GetComponent<Squid>() != null;
                if (Status.StartsWith("Assault: knights CHARGE") && !big && GameState.Coins >= 6) return false;
                Status = "Interrupt: enemy"; return true;
            }
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
            float x = PayX(py), d = Mathf.Abs(x - GameState.PX);
            if (d > 8f) Push("goto " + F(x + (GameState.PX < x ? -3f : 3f)) + " 1.5 " + F(Mathf.Max(20f, d * 0.6f)));   // gallop the long way (walking everywhere wasted hours a day)
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
        /// <summary>True if a built farm stands outside (or right at) the outer wall: farmers there die at night.</summary>
        static bool FarmsExposed(float home)
        {
            foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>())
            {
                if (fh.level <= 0) continue;
                float x = fh.transform.position.x; int side = x < home ? -1 : 1;
                if ((WallEdge(side, home) - home) * side < (x - home) * side + 4f) return true;
            }
            return false;
        }

        /// <summary>Unbuilt wall spot just beyond a farm that stands outside the outer wall (or null).</summary>
        Payable FarmWall(Payable[] payables, float home)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                float farRel = 0f;
                foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>())
                {
                    float rel = (fh.transform.position.x - home) * side;
                    if (fh.level > 0 && rel > farRel && rel < 70f) farRel = rel;
                }
                if (farRel <= 0f || (WallEdge(side, home) - home) * side > farRel + 6f) continue;
                // a wall construction site beyond the farm already exists -> nothing to do on this side
                bool pending = false;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<WorkableBuilding>())
                    if (b.name.Contains("Wall") && (b.transform.position.x - home) * side > farRel + 6f) pending = true;
                if (pending) continue;
                Payable best = null; float bd = float.MaxValue;
                foreach (var p in payables)
                {
                    if (!(p is PayableUpgrade) || !p.name.StartsWith("Wall") || IsBuiltWall(p) || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                    float rel = (p.transform.position.x - home) * side;
                    if (rel < farRel + 6f || rel > farRel + 35f) continue;
                    if (rel < bd) { bd = rel; best = p; }
                }
                if (best != null) return best;
                // wall spots beyond the farm are still in the forest: cut the nearest tree beyond the farm (capped -
                // on some maps the spot never becomes buildable and the clearing ate the army money)
                if (_farmWallTrees >= FarmWallTreeCap) continue;
                foreach (var p in payables)
                {
                    if (!p.name.StartsWith("Tree") || OnCooldown(p.GetInstanceID()) || !SafeCanPay(p)) continue;
                    float rel = (p.transform.position.x - home) * side;
                    if (rel < farRel || rel > farRel + 40f) continue;
                    if (rel < bd) { bd = rel; best = p; }
                }
                if (best != null) return best;
            }
            return null;
        }

        static float WallEdge(int side, float home)
        {
            float edge = home + side * 6f;
            // the kingdom's own outer-wall tracking: FindObjectsOfType misses walls that are deactivated far from the camera
            try
            {
                var ow = Managers.kingdom.outerWall;
                if (ow != null)
                {
                    var w = side < 0 ? ow.left : ow.right;
                    if (w != null)
                    {
                        float wx = w.transform.position.x;
                        if (side < 0 && wx < edge) edge = wx;
                        if (side > 0 && wx > edge) edge = wx;
                    }
                }
            }
            catch { }
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

        public static bool WallBetween(float a, float b)
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
                if (Managers.kingdom != null && TooLateFor(x, Managers.kingdom.campfirePosition)) continue;   // day-4 game over: recruited a beggar 98 out at 16:30
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
