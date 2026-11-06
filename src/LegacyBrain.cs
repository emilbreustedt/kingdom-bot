using System;
using System.Collections.Generic;
using UnityEngine;

namespace KingdomBot
{
    /// <summary>
    /// Strategy layer (v1). Called whenever the CommandQueue is idle; pushes a short plan (1-3 commands)
    /// so that the situation is re-evaluated every few seconds.
    /// </summary>
    /// <summary>Strategy of the day-98 record run (first commit, dddb58e), used by mode "survive".</summary>
    public class LegacyBrain
    {
        readonly Plugin _pl;
        public string Status = "idle";
        readonly Dictionary<int, float> _cooldown = new Dictionary<int, float>();   // payable/beggar id -> time until which we ignore it
        int _lastCastleUpgradeDay = -1;
        float _lastTap = -100f;
        int _coinsAtTap = -1, _tapFails; float _noRecruitUntil;
        float _gameOverAt = -1f;
        public bool AutoRestart = true;
        bool _safeMode;          // current plan is a retreat/home plan -> do not interrupt it
        public int Runs = 0;
        string _lastLogged = "";

        // Tunables
        public float AcreTarget = 120f;      // BY_THE_SEVENTH_DAY: forest gap width by day counter 6
        public int GallopFromDay = 8;        // ON_THE_NINTH_DAY: first gallop must happen at day counter >= 8
        /// <summary>"main": no walls on day 0, castle upgrade on day 1 -> free walls (ON_THE_SECOND_DAY).
        /// "fire3": first campfire only on day counter 2 (ON_THE_THIRD_DAY), survive two nights by evading.</summary>
        public string Mode = "main";
        public bool AcreRun = false;       // dedicated run: heavy forest clearing until day 6

        public LegacyBrain(Plugin pl) { _pl = pl; }

        void Push(string c) { _pl.Queue.Enqueue(c); }
        bool OnCooldown(int id) { float t; return _cooldown.TryGetValue(id, out t) && Time.time < t; }
        void Cool(int id, float s) { _cooldown[id] = Time.time + s; }

        public bool MaySprint { get { return GameState.Day >= GallopFromDay; } }

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
        public void ThinkInner()
        {
            _safeMode = false;
            // stuck guard: a recruitment tap must cost a coin; otherwise pause recruiting
            if (_coinsAtTap >= 0 && GameState.Player != null)
            {
                if (GameState.Coins >= _coinsAtTap) { _tapFails++; if (_tapFails >= 3) { _noRecruitUntil = Time.time + 120f; _tapFails = 0; Plugin.Log("Anwerben klappt nicht - Pause"); } }
                else _tapFails = 0;
                _coinsAtTap = -1;
            }
            int dNow = GameState.Day;
            if (dNow < _lastDaySeen) { _lastCastleUpgradeDay = -1; _cooldown.Clear(); Plugin.Log("Neuer Lauf erkannt"); }
            if (dNow != _lastDaySeen && Managers.achievements != null && Managers.achievements.flags != null)
            {
                var am = Managers.achievements;
                Plugin.Log(string.Format("== Tag {0} (Modus {1}) Flags: FreeWall={2} LitFire={3} Galloped={4} CoinOnGround={5} PortalsDestroyed={6} | Archers={7} Deer={8} Kills={9} Land={10}",
                    dNow, Mode, am.flags[2], am.flags[3], am.flags[4], am.flags[5], am.flags[7], am.counters[3], am.counters[4], am.counters[5], am.counters[7]));
            }
            _lastDaySeen = dNow;
            var game = Managers.game;
            var p = GameState.Player;
            if (game == null || p == null) { Status = "kein Spiel"; return; }
            if ((int)game.gameState == 3)
            {
                if (_gameOverAt < 0) _gameOverAt = Time.time;
                if (AutoRestart && Time.time - _gameOverAt > 10f) { _gameOverAt = -1; Runs++; Status = "Neustart (Versuch " + (Runs + 1) + ")"; Acc.Call(game, "Restart"); Push("wait 5"); return; }
                Status = "Game Over"; Push("wait 1"); return;
            }
            _gameOverAt = -1;
            if ((int)game.gameState == 1)
            {
                // the intro waits for a real stick movement (read directly from Rewired) -> trigger it ourselves
                var intro = Acc.Get<IntroSequence>(game, "_introSequence");
                if (intro != null) Acc.Call(intro, "PlayerTriedToMove");
                Status = "Intro überspringen"; Push("wait 1"); return;
            }
            if ((int)game.gameState != 2) { Status = "warte (" + game.gameState + ")"; Push("wait 1"); return; }
            if (!p.hasCrown) { Status = "Krone verloren!"; Push("wait 1"); return; }

            var k = Managers.kingdom;
            int day = GameState.Day;
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
                Status = "Flucht vor Gegner"; _safeMode = true;
                Push("goto " + F(home)); return;
            }
            if (nearest != null && Mathf.Abs(nearest.transform.position.x - px) < 7f && !WallBetween(px, nearest.transform.position.x))
            {
                float ex = nearest.transform.position.x;
                float away = px + (px > ex ? 12f : -12f);
                Status = "Ausweichen"; _safeMode = true;
                Push("walkto " + F(away) + " 1"); return;
            }
            // ON_THE_SECOND_DAY: castle level 2 while the achievement day counter is still 1 (until dawn of day 2)
            if (GiftCastleDue(px, home, coins)) return;

            if (evening || (dawn && nearest != null && Mathf.Abs(nearest.transform.position.x - home) < 40f))
            {
                _safeMode = true;
                if (Mathf.Abs(px - home) > 3f) { Status = "Abend: nach Hause"; Push("goto " + F(home + 1.5f) + " 1"); return; }
                // at night: grab coins that fall inside the walls, otherwise wait by the fire
                var nc = NearestCoin(px, leftSafe + 1f, rightSafe - 1f);
                if (nc != null && Mathf.Abs(nc.transform.position.x - px) < 8f) { Status = "Nacht: Münze"; Push("walkto " + F(nc.transform.position.x) + " 0.3"); return; }
                Status = "Nacht: am Feuer"; Push("wait 2"); return;
            }

            // ---------- 2. campfire first, then chest & coins ----------
            var payables0 = UnityEngine.Object.FindObjectsOfType<Payable>();
            var fire = Find(payables0, x => x is PayableUpgrade && x.name.StartsWith("Castle0") && SafeCanPay(x));
            if (fire != null && Mode == "fire3" && day < 2)
            {
                // no kingdom yet: collect coins near the camp site, evade enemies, otherwise wait
                var c0 = NearestCoin(px, fire.transform.position.x - 30f, fire.transform.position.x + 30f);
                if (c0 != null && k.isDaytime) { Status = "Feuer-Lauf: Münzen sammeln"; Push("walkto " + F(c0.transform.position.x) + " 0.3 20"); return; }
                var en = NearestEnemy(px);
                if (en != null && Mathf.Abs(en.transform.position.x - px) < 14f)
                {
                    float ex = en.transform.position.x;
                    Status = "Feuer-Lauf: ausweichen"; _safeMode = true;
                    Push("walkto " + F(px + (px > ex ? 15f : -15f)) + " 1 6"); return;
                }
                Status = "Feuer-Lauf: warten bis Tag 3"; Push("walkto " + F(fire.transform.position.x) + " 2 10"); return;
            }
            if (fire != null && coins >= fire.price) { Status = "Lagerfeuer entzünden"; PayAt(fire); return; }
            bool fireLit = fire == null && Find(payables0, x => x.name.StartsWith("Castle0")) == null;
            foreach (var ch in UnityEngine.Object.FindObjectsOfType<Chest>())
            {
                if (!fireLit) break;
                float cx = ch.transform.position.x;
                if (!ch.isEmpty && !OnCooldown(ch.GetInstanceID()) && cx > leftSafe - 70f && cx < rightSafe + 70f && hour < k.dayEnd - 4f) { Cool(ch.GetInstanceID(), 1e7f); /* one try per chest and run */ Status = "Truhe"; Push("goto " + F(cx) + " 0.3"); Push("wait 2"); return; }
            }
            var coin = NearestCoin(px, leftSafe - 25f, rightSafe + 25f);
            if (coin != null && Mathf.Abs(coin.transform.position.x - px) < 30f)
            {
                Cool(coin.GetInstanceID(), 1e7f); /* one attempt per coin - unreachable coins are ignored afterwards */
                Status = "Münze einsammeln"; Push("goto " + F(coin.transform.position.x) + " 0.3 20"); return;
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
                    if (w0 != null && coins >= w0.price) { Status = "Erste Mauer " + (side < 0 ? "links" : "rechts"); PayAt(w0); return; }
                }
            }

            // ---------- 3. budget ----------
            var castleAny = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && !x.name.StartsWith("Castle0"));
            int reserve = 0;
            // coins in the purse are the buffer against hits at night (coins get knocked out before the crown)
            if (day >= 1 && hour >= k.dayEnd - 8f) reserve = 4;
            if (GiftPending() && castleAny != null) reserve = Mathf.Max(reserve, castleAny.price);
            int budget = coins - reserve;
            bool acrePhase = AcreRun && day <= 6 && (k.rightForest - k.leftForest) < AcreTarget + 5f;
            int archerCap = Mathf.Min(40, 6 + 3 * day);

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

            // ---------- 4. tools for idle peasants (archers capped, workers for building/clearing) ----------
            if (peasants > toolsOnDisplay)
            {
                bool farmExists = false;
                foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>()) if (fh.level > 0) farmExists = true;
                PayableShop shop = null; string what = "";
                if (archers < 1 && bowShop != null) { shop = bowShop; what = "Bogen"; }
                else if (workers < 1 && hammerShop != null) { shop = hammerShop; what = "Hammer"; }
                else if (archers < 2 && bowShop != null) { shop = bowShop; what = "Bogen"; }
                else if (workers < WorkerTarget(day, archers, acrePhase) && hammerShop != null) { shop = hammerShop; what = "Hammer"; }
                else if (scytheShop != null && farmExists && farmers < 3) { shop = scytheShop; what = "Sense"; }
                else if (archers < archerCap && bowShop != null) { shop = bowShop; what = "Bogen"; }
                if (shop != null && SafeCanPay(shop) && budget >= shop.price)
                {
                    Status = "Werkzeug kaufen: " + what;
                    PayAt(shop); return;
                }
            }

            // ---------- 5. castle (achievement timing handled above; later when affordable: lvl 4 = stone walls) ----------
            var castle = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Castle") && SafeCanPay(x));
            bool wallsOk = WallsOnSide(-1, home) > 0 && WallsOnSide(1, home) > 0;
            if (castle != null && !GiftPending() && day >= 2 && wallsOk && archers >= 8 && budget >= castle.price + 2)
            {
                Status = "Burg ausbauen (" + castle.name.Replace("(Clone)", "") + ", " + castle.price + ")";
                PayAt(castle); return;
            }

            // ---------- 6. clear forest for the acre achievement (days 1-6) ----------
            float gap = k.rightForest - k.leftForest;
            if (acrePhase && workers > 0 && budget >= 1 && archers >= 2)
            {
                var tree = EdgeTree(payables, k);
                if (tree != null) { Status = "Baum fällen (Lücke " + gap.ToString("0") + ")"; PayAt(tree); return; }
            }

            // ---------- 7. strengthen walls, then towers ----------
            if (archers >= 8 && budget >= 3)
            {
                var up = Find(payables, x => x is PayableUpgrade && IsBuiltWall(x) && SafeCanPay(x) && x.price <= budget, home);
                if (up != null) { Status = "Mauer verstärken (" + up.price + ")"; PayAt(up); return; }
                var tower = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Tower") && SafeCanPay(x) && x.price <= budget && Between(x.transform.position.x, leftSafe - 1f, rightSafe + 1f), home);
                if (tower != null) { Status = "Turm bauen (" + tower.price + ")"; PayAt(tower); return; }
            }

            // ---------- 8. more walls further out ----------
            var wall = InnerWallSpot(payables, home);
            if (wall != null && budget >= wall.price + 1 && archers >= 6)
            {
                Status = "Mauer bauen";
                PayAt(wall); return;
            }

            // ---------- 9a. recruit beggars (only while the army is below the cap) ----------
            int army = archers + workers + farmers;
            if (Time.time >= _noRecruitUntil && budget >= 2 && peasants < 3 && hour < k.dayEnd - 4f && army < archerCap + WorkerTarget(day, archers, acrePhase) + 3)
            {
                // reach: 35 beyond the walls, but at least 60 around the fire while there is time left today
                float reach = hour < k.dayEnd - 7f ? 100f : 35f;
                var b = NearestBeggar(px, Mathf.Min(leftSafe - 35f, home - reach), Mathf.Max(rightSafe + 35f, home + reach));
                if (b != null)
                {
                    Status = "Bettler anwerben";
                    float bx = b.transform.position.x;
                    Cool(b.GetInstanceID(), 8f);
                    _lastTap = Time.time; _coinsAtTap = coins;
                    Push("walkto " + F(bx) + " 0.3 25"); Push("handtap " + b.GetInstanceID()); Push("wait 1.5"); return;   /* coin straight into the beggar's hands */
                }
            }

            // ---------- 9b. farms + trees for income ----------
            if (archers >= 6 && budget >= 6)
            {
                var farm = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Farmhouse") && SafeCanPay(x) && Between(x.transform.position.x, leftSafe + 1f, rightSafe - 1f), home);
                if (farm != null) { Status = "Bauernhof (" + farm.price + ")"; PayAt(farm); return; }
            }
            if (workers > 0 && budget >= 3 && !acrePhase && gap < 200f)
            {
                var tree = EdgeTree(payables, k);
                if (tree != null) { Status = "Baum fällen (Lücke " + gap.ToString("0") + ")"; PayAt(tree); return; }
            }

            // ---------- 9. patrol to collect taxes ----------
            Status = "Patrouille (Steuern)";
            float target = px < home ? rightSafe - 1f : leftSafe + 1f;
            Push("walkto " + F(target) + " 1");
        }

        bool GiftPending()
        {
            if (Mode != "main") return false;
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
            Status = "Burg ausbauen für Geschenk (" + c.name + ")";
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
            if (_safeMode) return false;
            var p = GameState.Player; var k = Managers.kingdom; var g = Managers.game;
            if (p == null || k == null || g == null || (int)g.gameState != 2) return false;
            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour; int day = GameState.Day;
            bool evening = hour >= k.dayEnd - 1.5f || (!k.isDaytime && !(day == 0 && hour < 12f));
            float l = WallEdge(-1, home), r = WallEdge(1, home);
            if (evening && (px < l - 0.5f || px > r + 0.5f)) { Status = "Abbruch: Abend"; return true; }
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 9f && !WallBetween(px, e.transform.position.x)) { Status = "Abbruch: Gegner"; return true; }
            return false;
        }

        int WorkerTarget(int day, int archers, bool acrePhase)
        {
            if (day == 0) return 1;
            if (acrePhase) return 6;                   // clearing for the acre
            return Mathf.Max(3, archers / 3);
        }

        void PayAt(Payable py)
        {
            Cool(py.GetInstanceID(), 6f);
            Push("walkto " + F(py.transform.position.x) + " 0.3");
            Push("payid " + py.GetInstanceID() + " " + F(py.transform.position.x));
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

        Enemy NearestEnemy(float px)
        {
            Enemy best = null; float bd = float.MaxValue;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
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
