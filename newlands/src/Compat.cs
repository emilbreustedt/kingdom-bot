using UnityEngine;

namespace KingdomBot.NewLands
{
    /// <summary>
    /// Classic-style achievement counters/flags and forest edges for the New Lands port of the Classic strategy.
    /// Classic indices: counters 1=Day 3=Archers 4=HuntedDeers 5=KilledEnemies 6=DestroyedPortals 7=ClearedLand;
    /// flags 2=FreeWallBuilt 3=LitFire 4=Galloped 5=CoinDroppedOnGround 7=PortalsDestroyed 8=CoinFellOut 9=Won.
    /// </summary>
    public static class Compat
    {
        static float S(Stat s) { var st = Managers.stats; return st != null ? st.GetStat(s, 0f) : 0f; }

        public static int[] Counters
        {
            get
            {
                return new int[] { 0, (int)S(Stat.Day), 0, (int)S(Stat.Archers), (int)S(Stat.HuntedDeers), (int)S(Stat.KilledEnemies),
                                   (int)S(Stat.DestroyedPortals), (int)S(Stat.ClearedLand) };
            }
        }

        public static bool[] Flags
        {
            get
            {
                return new bool[] { false, false, S(Stat.FreeWallBuilt) > 0, S(Stat.LitFire) > 0, S(Stat.Galloped) > 0, S(Stat.CoinDroppedOnGround) > 0,
                                    false, S(Stat.DestroyedPortals) > 0, S(Stat.CoinFellOut) > 0, S(Stat.Won) > 0 };
            }
        }

        // enemies that have not moved for 20s are ignored (frozen leftovers after loading a save)
        static readonly System.Collections.Generic.Dictionary<int, Vector2> _track = new System.Collections.Generic.Dictionary<int, Vector2>();
        public static bool IsLiveEnemy(Enemy e)
        {
            if (e == null || !e.isActiveAndEnabled) return false;
            int id = e.GetInstanceID(); float x = e.transform.position.x; Vector2 t;
            if (!_track.TryGetValue(id, out t) || Mathf.Abs(t.x - x) > 0.2f) { _track[id] = new Vector2(x, Time.time); return true; }
            return Time.time - t.y < 20f;
        }

        /// <summary>inner edge of the forest on each side of the camp</summary>
        public static float LeftForest { get { return Edge(-1); } }
        public static float RightForest { get { return Edge(1); } }
        static float Edge(int side)
        {
            var k = Managers.kingdom;
            if (k == null) return 0f;
            if (k.closestForest == null) return k.campfirePosition + side * 20f;
            return side < 0 ? k.closestForest.left : k.closestForest.right;
        }
    }
}
