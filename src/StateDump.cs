using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KingdomBot
{
    /// <summary>Writes a JSON snapshot of the world so the strategy can be developed/debugged from outside.</summary>
    public static class StateDump
    {
        static string N(float f) { return f.ToString("0.##", CultureInfo.InvariantCulture); }
        static string S(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "'") + "\""; }
        static string Clean(string n) { return n.Replace("(Clone)", "").Trim(); }

        public static void Write(string path, Plugin pl)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"realtime\":" + N(Time.unscaledTime));
            sb.Append(",\"timescale\":" + N(Time.timeScale));
            sb.Append(",\"bot\":" + (pl.BotActive ? "true" : "false"));
            sb.Append(",\"auto\":" + (pl.AutoBrain ? "true" : "false"));
            sb.Append(",\"queue\":" + S(pl.Queue.Describe()));
            sb.Append(",\"brain\":" + S(pl.Brain.Status));
            var game = Managers.game;
            if (game != null) sb.Append(",\"gameState\":" + S(game.gameState.ToString()) + ",\"menu\":" + S(game.menuState.ToString()));
            var p = GameState.Player;
            if (p == null) { sb.Append("}"); File.WriteAllText(path, sb.ToString()); return; }

            var k = Managers.kingdom;
            if (Managers.director != null) sb.Append(",\"secPerHour\":" + N(Managers.director.secondsPerInGameHour));
            sb.Append(",\"day\":" + GameState.Day + ",\"hour\":" + N(GameState.Hour) + ",\"isDay\":" + (k.isDaytime ? "true" : "false") + ",\"safe\":" + (k.isSafe ? "true" : "false"));
            sb.Append(",\"player\":{\"x\":" + N(GameState.PX) + ",\"coins\":" + GameState.Coins + ",\"max\":" + GameState.MaxCoins
                + ",\"crown\":" + (p.hasCrown ? "true" : "false") + ",\"stamina\":" + N(p.stamina) + ",\"action\":" + S(Acc.ActionName(p))
                + ",\"pay\":" + S(Acc.PayStateName(p)) + ",\"selected\":" + S(Acc.Selected(p) != null ? Clean(Acc.Selected(p).name) : "") + "}");
            sb.Append(",\"campfire\":" + N(k.campfirePosition));
            if (k.border != null) sb.Append(",\"border\":[" + N(k.border.left) + "," + N(k.border.right) + "]");
            if (k.borderBanner != null) sb.Append(",\"borderBanner\":[" + X(k.borderBanner.left) + "," + X(k.borderBanner.right) + "]");
            if (k.outerWall != null) sb.Append(",\"outerWall\":[" + X(k.outerWall.left) + "," + X(k.outerWall.right) + "]");
            sb.Append(",\"forest\":[" + N(k.leftForest) + "," + N(k.rightForest) + "]");
            sb.Append(",\"counts\":{\"archers\":" + UnityEngine.Object.FindObjectsOfType<Archer>().Length + ",\"knights\":" + UnityEngine.Object.FindObjectsOfType<Knight>().Length + ",\"workers\":" + UnityEngine.Object.FindObjectsOfType<Worker>().Length + ",\"farmers\":" + UnityEngine.Object.FindObjectsOfType<Farmer>().Length
                + ",\"beggars\":" + UnityEngine.Object.FindObjectsOfType<Beggar>().Length + ",\"peasants\":" + UnityEngine.Object.FindObjectsOfType<Peasant>().Length
                + ",\"deer\":" + UnityEngine.Object.FindObjectsOfType<Deer>().Length + ",\"bunnies\":" + UnityEngine.Object.FindObjectsOfType<Bunny>().Length + "}");
            var ach = Managers.achievements;
            if (ach != null && ach.counters != null)
            {
                sb.Append(",\"ach\":{\"counters\":[" + string.Join(",", Array.ConvertAll(ach.counters, x => x.ToString())) + "],\"flags\":[" + string.Join(",", Array.ConvertAll(ach.flags, x => x ? "1" : "0")) + "]}");
            }

            if (ach != null && ach.achievements != null)
            {
                sb.Append(",\"achDefs\":[");
                bool f1 = true;
                foreach (var a in ach.achievements)
                {
                    if (a == null) continue;
                    if (!f1) sb.Append(","); f1 = false;
                    sb.Append("{\"id\":" + S(a.steamID) + ",\"x\":" + a.conditionX + ",\"y\":" + a.conditionY + ",\"done\":" + (a.completed ? "true" : "false")
                        + ",\"series\":[" + (a.series != null ? string.Join(",", a.series.ConvertAll(v => v.ToString()).ToArray()) : "") + "]}");
                }
                sb.Append("]");
            }
            sb.Append(",\"beggarX\":[" + Xs(UnityEngine.Object.FindObjectsOfType<Beggar>()) + "]");
            sb.Append(",\"peasantX\":[" + Xs(UnityEngine.Object.FindObjectsOfType<Peasant>()) + "]");
            sb.Append(",\"merchantX\":[" + Xs(UnityEngine.Object.FindObjectsOfType<Merchant>()) + "]");

            sb.Append(",\"portals\":[");
            bool first = true;
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
            {
                if (!first) sb.Append(","); first = false;
                var d = po.GetComponent<Damageable>();
                sb.Append("{\"x\":" + N(po.transform.position.x) + ",\"outer\":" + (po.outerSpawner ? "true" : "false") + ",\"hp\":" + (d != null ? d.hitPoints : -1) + ",\"open\":" + (Acc.Get<bool>(po, "_opened") ? "true" : "false") + "}");
            }
            sb.Append("]");

            sb.Append(",\"enemies\":[");
            first = true;
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (!first) sb.Append(","); first = false;
                sb.Append("{\"n\":" + S(Clean(e.name)) + ",\"x\":" + N(e.transform.position.x) + "}");
            }
            sb.Append("]");

            sb.Append(",\"payables\":[");
            first = true;
            foreach (var py in UnityEngine.Object.FindObjectsOfType<Payable>())
            {
                if (!py.isActiveAndEnabled) continue;
                if (!first) sb.Append(","); first = false;
                bool can = false; try { can = py.CanPay(); } catch { }
                bool onWall = py.GetComponent<Wall>() != null || (py.transform.parent != null && py.transform.parent.GetComponent<Wall>() != null);
                sb.Append("{\"t\":" + S(py.GetType().Name) + ",\"n\":" + S(Clean(py.name)) + ",\"x\":" + N(py.transform.position.x) + ",\"price\":" + py.price + ",\"can\":" + (can ? "true" : "false")
                    + (py is PayableUpgrade ? ",\"cd\":" + N(((PayableUpgrade)py).cooldown) : "")
                    + (onWall ? ",\"wall\":true" : "") + (py.transform.parent != null ? ",\"parent\":" + S(Clean(py.transform.parent.name)) : "") + "}");
            }
            sb.Append("]");

            sb.Append(",\"walls\":[");
            first = true;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>())
            {
                if (!first) sb.Append(","); first = false;
                var d = w.GetComponent<Damageable>();
                sb.Append("{\"x\":" + N(w.transform.position.x) + ",\"lvl\":" + w.level + ",\"hp\":" + (d != null ? d.hitPoints : -1) + ",\"intact\":" + (w.isIntact ? "true" : "false") + "}");
            }
            sb.Append("]");

            sb.Append(",\"construction\":[");
            first = true;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<WorkableBuilding>())
            {
                if (!first) sb.Append(","); first = false;
                sb.Append("{\"n\":" + S(Clean(b.name)) + ",\"x\":" + N(b.transform.position.x) + "}");
            }
            sb.Append("]");
            sb.Append(",\"groundCoins\":[");
            first = true;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Coin>())
            {
                if (c.pickedUp) continue;
                if (!first) sb.Append(","); first = false;
                sb.Append(N(c.transform.position.x));
            }
            sb.Append("]");

            sb.Append(",\"beggarCamps\":[");
            first = true;
            foreach (var bc in UnityEngine.Object.FindObjectsOfType<BeggarCamp>())
            {
                if (!first) sb.Append(","); first = false;
                sb.Append(N(bc.transform.position.x));
            }
            sb.Append("]");

            var castle = UnityEngine.Object.FindObjectOfType<Castle>();
            if (castle != null) sb.Append(",\"castle\":{\"x\":" + N(castle.transform.position.x) + ",\"level\":" + S(castle.level.ToString()) + "}");
            var wb = Managers.world != null ? Managers.world.worldBounds : null;
            if (wb != null) sb.Append(",\"worldBounds\":[" + N(wb.left) + "," + N(wb.right) + "]");

            sb.Append("}");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string Xs<T>(T[] arr) where T : Component
        {
            var parts = new List<string>();
            foreach (var c in arr) parts.Add(N(c.transform.position.x));
            return string.Join(",", parts.ToArray());
        }
        static string X(Component c) { return c != null ? N(c.transform.position.x) : "null"; }
        static string X(GameObject g) { return g != null ? N(g.transform.position.x) : "null"; }
        static string C<T>(ICollection<T> c) { return c != null ? c.Count.ToString() : "0"; }
    }
}
