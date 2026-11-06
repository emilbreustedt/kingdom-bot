using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace KingdomBot
{
    /// <summary>
    /// "gamedata" command: dumps the numbers that decide survival (walls, towers, archers, enemies, wave sizes per day,
    /// day cycle, castle income) to BepInEx/KingdomBot/gamedata.txt. Used to design the defence from facts.
    /// </summary>
    public static class GameData
    {
        static string HP(Component c) { if (c == null) return "-"; var d = c.GetComponent<Damageable>() ?? c.GetComponentInChildren<Damageable>(true); return d != null ? d.initialHitPoints.ToString() : "-"; }
        static string V(object o, string f) { try { return Convert.ToString(Acc.Get<object>(o, f), System.Globalization.CultureInfo.InvariantCulture); } catch { return "?"; } }

        public static void Write(string path)
        {
            var sb = new StringBuilder();
            var holder = Managers.holder;
            sb.AppendLine("== WALLS (Holder.wallPrefabs)");
            if (holder != null && holder.wallPrefabs != null)
                for (int i = 0; i < holder.wallPrefabs.Length; i++)
                {
                    var w = holder.wallPrefabs[i]; if (w == null) { sb.AppendLine(i + ": null"); continue; }
                    sb.AppendLine(string.Format("{0}: {1} level={2} hp={3} stone={4} repairRate={5} repairAboveMax={6} bonusHpMult={7} minIntact={8}",
                        i, w.name, w.level, HP(w), w.isStone, w.repairRate, w.repairRateAboveMax, w.bonusHitPointsMultiplier, w.minimumIntactHitPoints));
                }
            sb.AppendLine("== TOWERS (Holder.towerPrefabs)");
            if (holder != null && holder.towerPrefabs != null)
                for (int i = 0; i < holder.towerPrefabs.Length; i++)
                {
                    var t = holder.towerPrefabs[i]; if (t == null) continue;
                    sb.AppendLine(string.Format("{0}: {1} level={2} guardSlots={3} hp={4}", i, t.name, t.level, t.GetComponentsInChildren<GuardSlot>(true).Length, HP(t)));
                }
            sb.AppendLine("== UNITS");
            if (holder != null)
            {
                var a = holder.archerPrefab != null ? holder.archerPrefab.GetComponent<Archer>() : null;
                if (a != null)
                {
                    sb.AppendLine(string.Format("archer: hp={0} shootRange={1} prep={2} interval={3} cooldown={4} cooldownWithKnight={5} fleeRange={6} perfect={7} maxPursue={8}",
                        HP(a), a.shootRange, a.shootPrepTime, a.shootIntervalTime, a.shootCooldownTime, a.shootCooldownWithKnightTime, a.fleeRange, a.perfectArrowProbability, a.maxPursueDistance));
                    if (a.arrowPrefab != null) sb.AppendLine(string.Format("arrow: damage={0} perfectMult={1}", a.arrowPrefab.hitDamage, a.arrowPrefab.perfectDamageMultiplier));
                }
                foreach (var ch in new[] { holder.archerPrefab, holder.workerPrefab, holder.knightPrefab, holder.farmerPrefab, holder.peasantPrefab })
                    if (ch != null) sb.AppendLine(string.Format("{0}: hp={1} demoteAtCoins={2} grabbable={3}", ch.name, HP(ch), ch.demoteAtCoins, ch.grabbable));
                var kn = holder.knightPrefab != null ? holder.knightPrefab.GetComponent<Knight>() : null;
                if (kn != null) sb.AppendLine(string.Format("knight: attackDamage={0} cooldown={1} range={2} maxArchers={3}", kn.attackDamage, kn.attackCooldown, kn.attackRange, kn.maxArchers));
            }
            var em = Managers.enemies;
            sb.AppendLine("== ENEMIES");
            if (em != null)
            {
                foreach (var e in new[] { em.trollPrefab, em.ogrePrefab, em.squidPrefab, em.bossPrefab })
                {
                    if (e == null) continue;
                    var tr = e.GetComponent<Troll>(); var og = e.GetComponent<Ogre>(); var sq = e.GetComponent<Squid>(); var bo = e.GetComponent<Boss>();
                    string extra = tr != null ? string.Format("hitDamage={0} toughHp={1} walk={2} run={3} attackCooldown={4}", tr.hitDamage, tr.toughTrollHealth, tr.walkSpeed, tr.runSpeed, tr.attackCooldown)
                                 : og != null ? string.Format("hitDamage={0} walk={1} bashCooldown={2}", og.hitDamage, og.walkSpeed, og.bashCooldown)
                                 : sq != null ? string.Format("attackCooldown={0} speed={1} scanRange={2}", sq.attackCooldown, sq.moveSpeed, sq.scanRange)
                                 : bo != null ? string.Format("hitDamage={0} walk={1}", bo.hitDamage, bo.walkSpeed) : "";
                    sb.AppendLine(string.Format("{0}: hp={1} {2}", e.name, HP(e), extra));
                }
            }
            sb.AppendLine("== WAVES (enemies per wave and day: TrollWeak/Medium/Tough/Ogre/Squid/Boss)");
            try
            {
                var waves = Acc.Get<Wave[]>(holder, "_allWaves");
                int[] days = { 1, 3, 5, 8, 10, 12, 15, 18, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100 };
                if (waves != null)
                    foreach (var w in waves)
                    {
                        if (w == null) continue;
                        var line = new StringBuilder(w.name + (w.spawnOnBothSides ? " [both sides]" : "") + (w.special ? " [special]" : "") + ": ");
                        foreach (int d in days)
                        {
                            var list = Acc.Call(em, "GetEnemies", w, d, false) as System.Collections.IList;
                            var cnt = new int[7];
                            if (list != null) foreach (var bp in list) { try { cnt[(int)((EnemyBlueprint)bp).type]++; } catch { } }
                            line.Append("d" + d + "=" + (list != null ? list.Count : -1) + "(" + string.Join("/", Array.ConvertAll(cnt, x => x.ToString())) + ") ");
                        }
                        sb.AppendLine(line.ToString());
                    }
            }
            catch (Exception ex) { sb.AppendLine("waves failed: " + ex.Message); }
            sb.AppendLine("== DAY CYCLE (Director)");
            var dir = Managers.director;
            if (dir != null)
            {
                Func<Day[], string> names = arr => arr == null ? "-" : string.Join(", ", Array.ConvertAll(arr, x => x == null ? "null" : x.name + "(" + (x.wave != null ? x.wave.name : "no wave") + ")"));
                sb.AppendLine("start: " + (dir.startDay != null ? dir.startDay.name : "-"));
                sb.AppendLine("regular: " + names(dir.regularDays));
                sb.AppendLine("boss: " + names(dir.bossDays));
                sb.AppendLine("recovery: " + names(dir.recoveryDays));
                sb.AppendLine("numRegularDays: " + V(dir.numRegularDays, "min") + ".." + V(dir.numRegularDays, "max") + "  killerDay: " + (dir.killerDay != null ? dir.killerDay.name : "-"));
                sb.AppendLine("secondsPerInGameHour: " + dir.secondsPerInGameHour);
            }
            var k = Managers.kingdom;
            if (k != null && k.dailyCoinsByCastle != null)
            {
                var line = new StringBuilder("== CASTLE DAILY COINS by level: ");
                for (int i = 0; i <= 6; i++) line.Append(i + "=" + k.dailyCoinsByCastle.Evaluate(i).ToString("0.#") + " ");
                sb.AppendLine(line.ToString());
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
