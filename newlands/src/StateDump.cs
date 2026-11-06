using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KingdomBot.NewLands
{
    /// <summary>JSON snapshot of the world (written every second) for debugging/strategy development.</summary>
    public static class StateDump
    {
        static string N(float f) { return f.ToString("0.##", CultureInfo.InvariantCulture); }
        static string S(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "'") + "\""; }
        static string Clean(string n) { return n.Replace("(Clone)", "").Trim(); }
        static string B(bool b) { return b ? "true" : "false"; }

        public static void Write(string path, Plugin pl)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"timescale\":" + N(Time.timeScale) + ",\"bot\":" + B(pl.BotActive) + ",\"queue\":" + S(pl.Queue.Describe()) + ",\"brain\":" + S(pl.Brain.Status));
            var g = Managers.game;
            if (g != null) sb.Append(",\"gameState\":" + S(g.state.ToString()) + ",\"menu\":" + S(g.menuState.ToString()) + ",\"land\":" + S(g.currentLand.ToString()));
            var p = GameState.Player;
            var k = Managers.kingdom;
            if (p != null && k != null)
            {
                sb.Append(",\"day\":" + k.day + ",\"hour\":" + N(GameState.Hour) + ",\"isDay\":" + B(k.isDaytime) + ",\"safe\":" + B(k.isSafe));
                sb.Append(",\"player\":{\"x\":" + N(GameState.PX) + ",\"coins\":" + p.coins + ",\"crown\":" + B(p.hasCrown)
                    + ",\"selected\":" + S(p.selectedPayable != null ? Clean(p.selectedPayable.name) : "") + "}");
                sb.Append(",\"campfire\":" + N(k.campfirePosition) + ",\"castleBuilt\":" + B(k.castleBuilt));
                sb.Append(",\"counts\":{\"archers\":" + C<Archer>() + ",\"workers\":" + C<Worker>() + ",\"knights\":" + C<Knight>() + ",\"farmers\":" + C<Farmer>()
                    + ",\"peasants\":" + C<Peasant>() + ",\"beggars\":" + C<Beggar>() + ",\"enemies\":" + C<Enemy>() + "}");
                if (k.boat != null) sb.Append(",\"boat\":{\"x\":" + N(k.boat.transform.position.x) + ",\"state\":" + S(k.boat.state.ToString()) + "}");
            }
            // stats + achievement requirements (thresholds live in the assets)
            var st = Managers.stats;
            if (st != null)
            {
                sb.Append(",\"stats\":{");
                bool f = true;
                foreach (Stat s in Enum.GetValues(typeof(Stat)))
                {
                    float v; try { v = st.GetStat(s, 0f); } catch { continue; }
                    if (!f) sb.Append(","); f = false;
                    sb.Append(S(s.ToString()) + ":" + N(v));
                }
                sb.Append("}");
            }
            var am = Managers.achievements;
            if (am != null && am.achievements != null)
            {
                sb.Append(",\"achievements\":[");
                bool f = true;
                foreach (var a in am.achievements)
                {
                    if (a == null) continue;
                    if (!f) sb.Append(","); f = false;
                    var reqs = new List<string>();
                    if (a.requirements != null) foreach (var r in a.requirements) reqs.Add(r.stat + " " + r.comparison + " " + N(r.val));
                    var pre = new List<string>();
                    if (a.prerequisites != null) foreach (var r in a.prerequisites) pre.Add(r.stat + " " + r.comparison + " " + N(r.val));
                    bool preOk = false; try { preOk = Acc.Get<bool>(a, "_prereqComplete"); } catch { }
                    sb.Append("{\"id\":" + S(a.steamID) + ",\"done\":" + B(a.completed) + ",\"req\":" + S(string.Join(" & ", reqs.ToArray()))
                        + ",\"pre\":" + S(string.Join(" & ", pre.ToArray())) + ",\"preOk\":" + B(preOk) + "}");
                }
                sb.Append("]");
            }
            // coins lying around + coins carried by units (taxes are paid when the player passes)
            var gc = new List<string>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>()) gc.Add(N(c.transform.position.x));
            sb.Append(",\"groundCoins\":[" + string.Join(",", gc.ToArray()) + "]");
            int carried = 0;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wallet>()) if (GameState.Player == null || w.gameObject != GameState.Player.gameObject) carried += w.coins;
            sb.Append(",\"unitCoins\":" + carried);
            // payables
            sb.Append(",\"payables\":[");
            bool first = true;
            foreach (var py in UnityEngine.Object.FindObjectsOfType<Payable>())
            {
                if (!py.isActiveAndEnabled || py is PayableTree) continue;
                if (!first) sb.Append(","); first = false;
                bool can = false; try { can = py.CanPay(); } catch { }
                float payX = py.transform.position.x; try { payX = py.PlayerPayPoint(); } catch { }
                sb.Append("{\"t\":" + S(py.GetType().Name) + ",\"n\":" + S(Clean(py.name)) + ",\"x\":" + N(payX) + ",\"price\":" + py.price + ",\"can\":" + B(can) + "}");
            }
            sb.Append("]}");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static int C<T>() where T : Component { return UnityEngine.Object.FindObjectsOfType<T>().Length; }
    }
}
