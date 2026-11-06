using System;
using System.Collections.Generic;
using UnityEngine;

namespace KingdomBot.NewLands
{
    /// <summary>
    /// Strategy v0 for New Lands: the proven Classic core loop (fire, recruit, tools, walls, retreat at night).
    /// Boat/escape logic and achievement routines follow once the scaffold has been tested in game.
    /// </summary>
    public class Brain
    {
        readonly Plugin _pl;
        public string Status = "idle";
        string _lastLogged = "";
        readonly Dictionary<int, float> _cooldown = new Dictionary<int, float>();
        float _gameOverAt = -1f;
        LegacyBrain _legacy;

        public Brain(Plugin pl) { _pl = pl; }
        /// <summary>ON_THE_NINTH_DAY needs day 9 reached WITHOUT galloping, then a gallop. Once unlocked: gallop freely.</summary>
        public bool MaySprint { get { return Unlocked("ON_THE_NINTH_DAY") || GameState.Day >= 9; } }

        public static bool Unlocked(string id)
        {
            var am = Managers.achievements;
            if (am == null || am.achievements == null) return false;
            foreach (var a in am.achievements) if (a != null && a.steamID == id) return a.completed;
            return false;
        }

        void Push(string c) { _pl.Queue.Enqueue(c); }
        bool OnCooldown(int id) { float t; return _cooldown.TryGetValue(id, out t) && Time.time < t; }
        void Cool(int id, float s) { _cooldown[id] = Time.time + s; }
        static string F(float f) { return f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
        static bool CanPay(Payable p) { try { return p.isActiveAndEnabled && p.CanPay(); } catch { return false; } }
        static float PayX(Payable p) { try { return p.PlayerPayPoint(); } catch { return p.transform.position.x; } }

        public void Think()
        {
            ThinkInner();
            if (Status != _lastLogged)
            {
                _lastLogged = Status;
                Plugin.Log(string.Format("[d{0} h{1:0.0} c{2} x{3:0}] {4}", GameState.Day, GameState.Hour, GameState.Player != null ? GameState.Coins : -1, GameState.Player != null ? GameState.PX : 0f, Status));
            }
        }

        void ThinkInner()
        {
            var g = Managers.game; var k = Managers.kingdom; var p = GameState.Player;
            if (g == null || k == null || p == null) { Status = "no game"; return; }
            if ((int)g.state == 3)   // land ended: won (escaped by boat) or lost (crown)
            {
                if (_gameOverAt < 0) { _gameOverAt = Time.unscaledTime; Plugin.Log("Land ended: " + g.result + " on day " + k.day + " (land " + g.currentLand + ")"); }
                _pl.Queue.Clear();
                try { Acc.Set(g, "_continueButtonPressed", true); } catch { }          // skip credits / "press to continue"
                if ((int)g.menuState == 3)                                               // map: next land is already queued
                {
                    var map = UnityEngine.Object.FindObjectOfType<Map>();
                    if (map != null && Time.unscaledTime - _gameOverAt > 4f) { map.OnButtonAction(); _gameOverAt = -1; Status = "map: next land"; return; }
                }
                if (Time.unscaledTime - _gameOverAt > 40f) { _gameOverAt = -1; g.Restart(); Status = "restart"; return; }
                Status = g.result + ": waiting for the map";
                return;
            }
            _gameOverAt = -1;
            if ((int)g.state != 2) { Status = "waiting (" + g.state + ")"; return; }

            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour;
            int coins = p.coins, day = k.day;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();
            int archersNow = UnityEngine.Object.FindObjectsOfType<Archer>().Length, workersNow = UnityEngine.Object.FindObjectsOfType<Worker>().Length;
            var e = NearestEnemy(px);
            bool danger = e != null && Mathf.Abs(e.transform.position.x - px) < 12f && !WallBetween(px, e.transform.position.x);
            bool dayOk = k.isDaytime && hour < k.dayEnd - 3f && !danger;

            // boat: pay whatever boat step is affordable; once the army stands, save up for it (speedrun achievements)
            int boatGoal = BoatGoal(payables);
            if (dayOk && EscapeStep(payables, coins, day, workersNow, home)) return;
            if (dayOk && boatGoal > 0 && archersNow >= 8 && workersNow >= 2 && coins < boatGoal)
            {
                var c = NearestCoin(px, 30f);
                if (c != null) { Cool(c.GetInstanceID(), 1e7f); Status = "Saving for boat: pick up coin"; Push("goto " + F(c.transform.position.x) + " 0.3 20"); return; }
                Status = "Saving for boat (" + coins + "/" + boatGoal + ")";
                Push("walkto " + F(px < home ? home + 14f : home - 14f) + " 1 20");   // patrol past the archers to collect taxes
                return;
            }
            // everything else: the Classic record strategy (defence, recruiting, gift castle rush, gallop lock)
            if (_legacy == null) _legacy = new LegacyBrain(_pl);
            _legacy.ThinkInner();
            Status = _legacy.Status;
        }

        /// <summary>coins needed for the next boat step (0 = nothing to save for)</summary>
        static int BoatGoal(Payable[] payables)
        {
            foreach (var py in payables) if (py is Beach && CanPay(py)) return py.price;
            foreach (var py in payables) if (py is Wharf && CanPay(py)) return py.price;
            foreach (var py in payables) if (py is PayableUpgrade && py.name.StartsWith("Wreck") && CanPay(py)) return py.price;
            return 0;
        }

        bool EscapeStep(Payable[] payables, int coins, int day, int workers, float home)
        {
            // sail as soon as the boat waits at the beach
            foreach (var py in payables)
                if (py is Beach && CanPay(py) && coins >= py.price) { Status = "ESCAPE: pay the beach, sail away"; PayAt(py); return true; }
            if (workers < 2 || day < 2) return false;
            // keep paying the wharf (each payment stacks a boat part for the workers)
            foreach (var py in payables)
                if (py is Wharf && CanPay(py) && coins >= py.price + 1) { Status = "Boat: wharf part (" + py.price + ")"; PayAt(py); return true; }
            // repair the wreck once there is money for it
            var wreck = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Wreck") && CanPay(x), home);
            if (wreck != null && coins >= wreck.price) { Status = "Boat: repair wreck (" + wreck.price + ")"; PayAt(wreck); return true; }
            return false;
        }

        public bool ShouldInterrupt()
        {
            var k = Managers.kingdom; if (k == null || GameState.Player == null) return false;
            if (Status.StartsWith("Night") || Status == "Dodging") return false;
            float px = GameState.PX;
            if (!k.isDaytime && Mathf.Abs(px - k.campfirePosition) > 6f) { Status = "Interrupt: night"; return true; }
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 9f && !WallBetween(px, e.transform.position.x)) { Status = "Interrupt: enemy"; return true; }
            return false;
        }

        void PayAt(Payable py)
        {
            Cool(py.GetInstanceID(), 6f);
            float x = PayX(py);
            Push("walkto " + F(x) + " 0.3"); Push("payid " + py.GetInstanceID() + " " + F(x));
        }

        Payable Find(Payable[] arr, Func<Payable, bool> pred, float near)
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

        static int WallsOnSide(int side, float home)
        {
            int n = 0;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>()) if ((w.transform.position.x - home) * side > 0) n++;
            return n;
        }

        static bool WallBetween(float a, float b)
        {
            float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>()) { float x = w.transform.position.x; if (x > lo && x < hi) return true; }
            return false;
        }

        static Enemy NearestEnemy(float px)
        {
            Enemy best = null; float bd = float.MaxValue;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>()) { float d = Mathf.Abs(e.transform.position.x - px); if (d < bd) { bd = d; best = e; } }
            return best;
        }

        Coin NearestCoin(float px, float range)
        {
            Coin best = null; float bd = range;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>())
            {
                if (OnCooldown(c.GetInstanceID()) || c.transform.position.y < -3f) continue;
                float d = Mathf.Abs(c.transform.position.x - px);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }
    }
}
