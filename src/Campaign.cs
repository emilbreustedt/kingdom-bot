using System;
using System.Collections.Generic;

namespace KingdomBot
{
    /// <summary>
    /// Reads the player's achievement status from Steam (read-only!) and picks the run mode in "auto".
    /// The bot never unlocks achievements itself - they are always earned in game.
    /// </summary>
    public static class Campaign
    {
        // Steam API names (= Achievement.steamID in the game's data)
        public const string SecondDay = "ON_THE_SECOND_DAY";
        public const string ThirdDay = "ON_THE_THIRD_DAY";
        public const string FifthDays = "FOR_FIVE_DAYS";
        public const string EighthDay = "ON_THE_EIGTH_DAY";
        public const string NinthDay = "ON_THE_NINTH_DAY";
        public const string FirstDay = "ON_THE_FIRST_DAY";
        public const string FourthDay = "ON_THE_FOURTH_DAY";
        public const string Acre = "BY_THE_SEVENTH_DAY";
        public const string TenthDay = "THE_TENTH_DAY";

        static readonly Dictionary<string, bool> _cache = new Dictionary<string, bool>();
        static float _cacheTime = -1000f;

        /// <summary>true = unlocked, false = missing, null = unknown (Steam not available)</summary>
        public static bool? Has(string id)
        {
            Refresh();
            bool v;
            if (_cache.TryGetValue(id, out v)) return v;
            return null;
        }

        public static bool Missing(string id) { var h = Has(id); return h.HasValue && !h.Value; }

        static void Refresh()
        {
            if (UnityEngine.Time.unscaledTime - _cacheTime < 30f && _cache.Count > 0) return;
            _cacheTime = UnityEngine.Time.unscaledTime;
            try
            {
                if (!SteamManager.Initialized) return;
                uint n = Steamworks.SteamUserStats.GetNumAchievements();
                for (uint i = 0; i < n; i++)
                {
                    string name = Steamworks.SteamUserStats.GetAchievementName(i);
                    bool got;
                    if (Steamworks.SteamUserStats.GetAchievement(name, out got)) _cache[name] = got;
                }
            }
            catch (Exception e) { Plugin.Log("Steam achievement query failed: " + e.Message); }
        }

        public static int CountMissing()
        {
            Refresh();
            int n = 0;
            foreach (var kv in _cache) if (!kv.Value) n++;
            return n;
        }

        public static string Summary()
        {
            Refresh();
            var miss = new List<string>();
            foreach (var kv in _cache) if (!kv.Value) miss.Add(kv.Key);
            miss.Sort();
            return _cache.Count == 0 ? "Steam: unknown" : (_cache.Count - miss.Count) + "/" + _cache.Count + " | missing: " + string.Join(", ", miss.ToArray());
        }

        /// <summary>Mode for the next run in "auto".</summary>
        public static string PickMode(bool pacifistEnabled)
        {
            if (Missing(ThirdDay)) return "fire3";
            // master covers day 100, the portal assault (Safe in X, Tenth Day) and the win
            if (Missing(Acre)) return "acre";                                           // short run (~2 min)
            if (Missing("SURVIVED_UNTIL_DAY_100")) return "survive";                    // pure defence (record: day 98)
            if (Missing("SAFE_IN_40") || Missing(TenthDay)) return "master";              // portal assault
            if (pacifistEnabled && (Missing(FifthDays) || Missing(EighthDay))) return "pacifist";
            return "master";
        }
    }
}
