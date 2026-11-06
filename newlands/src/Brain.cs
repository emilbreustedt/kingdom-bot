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

        public Brain(Plugin pl) { _pl = pl; }
        public bool MaySprint { get { return GameState.Day >= 8; } }   // ON_THE_NINTH_DAY: no gallop before day 9

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
            if ((int)g.state == 3)   // ended
            {
                if (_gameOverAt < 0) _gameOverAt = Time.unscaledTime;
                if (Time.unscaledTime - _gameOverAt > 10f) { _gameOverAt = -1; _pl.Queue.Clear(); g.Restart(); Status = "restart"; }
                else Status = "game over";
                return;
            }
            _gameOverAt = -1;
            if ((int)g.state != 2) { Status = "waiting (" + g.state + ")"; return; }

            float px = GameState.PX, home = k.campfirePosition, hour = GameState.Hour;
            int coins = p.coins, day = k.day;
            var payables = UnityEngine.Object.FindObjectsOfType<Payable>();

            // 1. safety: enemy close and no wall in between -> run home / away
            var e = NearestEnemy(px);
            if (e != null && Mathf.Abs(e.transform.position.x - px) < 9f && !WallBetween(px, e.transform.position.x))
            {
                Status = "Dodging"; Push("walkto " + F(px + (px > e.transform.position.x ? 12f : -12f)) + " 1 3"); return;
            }
            if (!k.isDaytime)
            {
                if (Mathf.Abs(px - home) > 3f) { Status = "Night: going home"; Push("goto " + F(home) + " 1"); return; }
                Status = "Night: at the fire"; Push("wait 2"); return;
            }

            // 2. coins on the ground nearby
            var c = NearestCoin(px, 30f);
            if (c != null) { Cool(c.GetInstanceID(), 1e7f); Status = "Pick up coin"; Push("goto " + F(c.transform.position.x) + " 0.3 20"); return; }

            // 3. camp / castle upgrades (names are verified in game; any castle-like upgrade near home)
            var camp = Find(payables, x => x is PayableUpgrade && (x.name.StartsWith("Castle") || x.name.Contains("Camp")) && CanPay(x), home);
            if (camp != null && coins >= camp.price && (!k.castleBuilt || coins >= camp.price + 6)) { Status = "Camp/castle " + camp.name.Replace("(Clone)", ""); PayAt(camp); return; }

            // 4. tools for idle peasants: first bow, then hammer, then mostly bows
            int peasants = UnityEngine.Object.FindObjectsOfType<Peasant>().Length;
            int archers = UnityEngine.Object.FindObjectsOfType<Archer>().Length, workers = UnityEngine.Object.FindObjectsOfType<Worker>().Length;
            int onDisplay = 0; PayableShop bow = null, hammer = null;
            foreach (var py in payables) { var sh = py as PayableShop; if (sh == null) continue; onDisplay += sh.GetItemCount(); if (sh.name.Contains("Bow")) bow = sh; else if (sh.name.Contains("Hammer")) hammer = sh; }
            if (peasants > onDisplay)
            {
                PayableShop shop = archers < 1 ? bow : (workers < Mathf.Clamp(archers / 4, 1, 5) ? hammer : bow);
                if (shop != null && CanPay(shop) && coins >= shop.price) { Status = "Buy tool " + shop.name.Replace("(Clone)", ""); PayAt(shop); return; }
            }

            // 5. one wall per side (closest free foundation)
            if (workers > 0)
                for (int side = -1; side <= 1; side += 2)
                {
                    if (WallsOnSide(side, home) > 0) continue;
                    var w = Find(payables, x => x is PayableUpgrade && x.name.StartsWith("Wall") && x.GetComponent<Wall>() == null && (x.transform.position.x - home) * side > 0 && CanPay(x), home);
                    if (w != null && coins >= w.price) { Status = "Wall " + (side < 0 ? "left" : "right"); PayAt(w); return; }
                }

            // 6. recruit beggars (drop a coin next to them)
            if (coins >= 3 && peasants < 2 && hour < k.dayEnd - 4f)
            {
                Beggar b = null; float bd = 60f;
                foreach (var x in UnityEngine.Object.FindObjectsOfType<Beggar>()) { float d = Mathf.Abs(x.transform.position.x - home); if (d < bd && !OnCooldown(x.GetInstanceID())) { bd = d; b = x; } }
                if (b != null) { Cool(b.GetInstanceID(), 8f); Status = "Recruit beggar"; Push("walkto " + F(b.transform.position.x) + " 0.8 25"); Push("tap 1"); Push("wait 2.5"); return; }
            }

            // 7. patrol for taxes
            Status = "Patrol";
            Push("walkto " + F(px < home ? home + 10f : home - 10f) + " 1 20");
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
