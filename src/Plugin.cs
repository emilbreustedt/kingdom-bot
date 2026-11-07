using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace KingdomBot
{
    [BepInPlugin("emil.kingdombot", "KingdomBot", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static string Dir;

        public bool BotActive;          // F8: bot owns the controls
        public bool AutoBrain;          // F9: strategy generates commands (otherwise only cmd.txt)
        public float Speed = 32f;        // F10: game speed while playing normally
        public readonly Controller Ctl = new Controller();
        public readonly CommandQueue Queue = new CommandQueue();
        public Brain Brain;

        float _nextDump, _nextCmdPoll, _nextGuard, _nextIntroPoke, _notPlayingSince = -1;
        string _status = "";

        void Awake()
        {
            Instance = this;
            Dir = Path.Combine(Paths.BepInExRootPath, "KingdomBot");
            Directory.CreateDirectory(Dir);
            Brain = new Brain(this);
            // user settings: BepInEx/config/emil.kingdombot.cfg
            Brain.ConfigMode = Config.Bind("General", "Mode", "auto",
                "auto = pick the run type from the achievements still missing on Steam; or main | early | challenge").Value;
            Speed = Config.Bind("General", "Speed", 32f, "Game speed while the bot plays (1-32; ~30x effective). F10 cycles at runtime.").Value;
            BotActive = AutoBrain = Config.Bind("General", "AutoStart", true, "Bot takes over as soon as the game runs (F8/F9 toggle).").Value;
            Brain.AutoRestart = Config.Bind("General", "AutoRestart", true, "Start a new game automatically after game over.").Value;
            Brain.PacifistEnabled = Config.Bind("Modes", "Pacifist", true, "auto: try the no-kill / no-dropped-coin run (Five Days + Eighth Day).").Value;
            new Harmony("emil.kingdombot").PatchAll(typeof(Plugin).Assembly);
            // BepInEx's manager object gets destroyed by Kingdom's scene loading -> our own persistent Runner
            // is created later from a Harmony hook (creating GameObjects this early crashes Unity 5.3).
            Logger.LogInfo("KingdomBot loaded. F8 = bot on/off, F9 = auto strategy on/off");
        }

        public static void Log(string s) { Instance.Logger.LogInfo(s); }

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F8)) { BotActive = !BotActive; if (!BotActive) { Queue.Clear(); Ctl.Reset(); } Log("Bot active: " + BotActive); }
            if (Input.GetKeyDown(KeyCode.F9)) { AutoBrain = !AutoBrain; Log("Auto strategy: " + AutoBrain); }
            if (Input.GetKeyDown(KeyCode.F10)) { Speed = Speed >= 32f ? 1f : Speed * 2f; Log("Speed: " + Speed); }
            ApplySpeed();

            if (Brain != null && Managers.game != null && (int)Managers.game.gameState == 2) { try { Brain.CoinLedger(); } catch (System.Exception e) { Log("ledger: " + e.Message); } }
            if (Time.unscaledTime >= _nextCmdPoll) { _nextCmdPoll = Time.unscaledTime + 0.5f; PollCommandFile(); }

            // intro (also after a new game): queued commands can never finish there -> clear them and skip the intro directly
            var gm = Managers.game;
            if (gm != null && (int)gm.gameState == 1 && BotActive && AutoBrain)
            {
                if (!Queue.Idle) Queue.Clear();
                Ctl.Reset();
                if (Time.unscaledTime >= _nextIntroPoke)
                {
                    _nextIntroPoke = Time.unscaledTime + 1f;
                    // same as holding the skip button: ends intro AND tutorial (the tutorial unlocks shops only step by step)
                    try { Acc.Call(gm, "AbortIntroAndTutorial"); Log("Intro + tutorial skipped"); }
                    catch (Exception e)
                    {
                        Log("AbortIntroAndTutorial failed: " + e.Message);
                        try { var intro = Acc.Get<IntroSequence>(gm, "_introSequence"); if (intro != null) Acc.Call(intro, "PlayerTriedToMove"); } catch { }
                    }
                }
            }

            // game time only starts when the king crosses a trigger near the start; if the bot heads elsewhere, start it ourselves
            if (gm != null && (int)gm.gameState == 2 && BotActive && Managers.director != null && !Managers.director.playing)
            {
                if (_notPlayingSince < 0) _notPlayingSince = Time.unscaledTime;
                else if (Time.unscaledTime - _notPlayingSince > 6f) { try { Acc.Call(gm, "StartTime"); Log("Game time started"); } catch (Exception e) { Log("StartTime failed: " + e.Message); } _notPlayingSince = -1; }
            }
            else _notPlayingSince = -1;

            var player = GameState.Player;
            if (player != null)
            {
                if (BotActive)
                {
                    if (AutoBrain && Time.unscaledTime >= _nextGuard)
                    {
                        _nextGuard = Time.unscaledTime + 0.25f;
                        try { if (!Queue.Idle && Brain.ShouldInterrupt()) { Log("Interrupt: " + Brain.Status); Queue.Clear(); } } catch (Exception e) { Log("Guard error: " + e.Message); }
                    }
                    if (AutoBrain && Queue.Idle) { try { Brain.Think(); } catch (Exception e) { Log("Brain error: " + e); } }
                    Queue.Tick(this);
                }
                _status = Queue.Describe() + " | " + Brain.Status;
            }

            if (Time.unscaledTime >= _nextDump)
            {
                _nextDump = Time.unscaledTime + 1f;
                try { StateDump.Write(Path.Combine(Dir, "state.json"), this); }
                catch (Exception e) { Log("Dump error: " + e.Message); }
            }
        }

        float _appliedSpeed = 1f;
        /// <summary>Only touch timeScale while the game runs normally, so pause/menus/crown slow-mo keep working.</summary>
        void ApplySpeed()
        {
            var g = Managers.game;
            bool normal = g != null && (int)g.gameState == 2 && (int)g.menuState == 0;
            if (!normal) { _appliedSpeed = 1f; return; }
            // game sets 1.0 itself after effects; we only replace "1" or our own previous value
            float want = Queue.Positioning ? Mathf.Min(Speed, 2f) : Speed;
            if (Brain != null && Time.unscaledTime < Brain.SlowUntil) want = Mathf.Min(want, 3f);   // chest triggers are missed at 32x (king skips over the small collider)   // precise positioning for payments
            if (Mathf.Approximately(Time.timeScale, 1f) || Mathf.Approximately(Time.timeScale, _appliedSpeed))
            {
                Time.timeScale = want;
                _appliedSpeed = want;
            }
        }

        void PollCommandFile()
        {
            string f = Path.Combine(Dir, "cmd.txt");
            if (!File.Exists(f)) return;
            string[] lines;
            try { lines = File.ReadAllLines(f); File.Delete(f); } catch { return; }
            foreach (var raw in lines)
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                if (l == "bot on") { BotActive = true; continue; }
                if (l == "bot off") { BotActive = false; Queue.Clear(); Ctl.Reset(); continue; }
                if (l == "auto on") { AutoBrain = true; continue; }
                if (l == "auto off") { AutoBrain = false; continue; }
                if (l == "savequit")
                {
                    // same as the menu's "save & quit": keeps the current run when the bot is updated
                    // never during the final wave / with all portals down: such a save does not load back (two won-able runs lost)
                    try
                    {
                        var am = Managers.achievements;
                        bool fw = (am != null && am.flags != null && am.flags.Length > 7 && am.flags[7]) || (Managers.enemies != null && Managers.enemies.finalWaveSpawned);
                        if (fw) { Log("savequit REFUSED: final wave / all portals destroyed - the run would be lost"); continue; }
                    }
                    catch { }
                    try { if (Managers.saves != null && GameState.Player != null && Managers.game != null && (int)Managers.game.gameState == 2) Managers.saves.Save("AUTO"); Log("Saved run (day " + GameState.Day + ")"); }
                    catch (Exception e) { Log("save failed: " + e.Message); }
                    Application.Quit(); continue;
                }
                if (l == "newgame") { try { Queue.Clear(); Ctl.Reset(); Acc.Call(Managers.game, "Restart"); Log("New game requested"); } catch (Exception e) { Log("newgame failed: " + e.Message); } continue; }
                if (l.StartsWith("mode ")) { Brain.ConfigMode = Brain.NormalizeRunType(l.Substring(5)); Log("Run type for the next run: " + Brain.ConfigMode); continue; }
                if (l == "achievements") { Log(Campaign.Summary()); continue; }
                if (l == "debug") { DebugDump(); continue; }
                if (l.StartsWith("givecoins"))
                {
                    // TEST ONLY: refuses before day 41 - from then on no assault achievement can unlock any more
                    // (Safe in 40 needs day < 40, Tenth Day day <= 9), so nothing is earned by it.
                    int n; if (!int.TryParse(l.Substring(9).Trim(), out n)) n = 30;
                    if (GameState.Day <= 40) { Log("givecoins refused: only after day 40 (achievements must be earned legitimately)"); continue; }
                    try
                    {
                        var w = Acc.Wallet(GameState.Player);
                        var cf = typeof(Wallet).GetField("_coins", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        cf.SetValue(w, (int)cf.GetValue(w) + n);
                        Log("givecoins: +" + n + " (test, day " + GameState.Day + ")");
                    }
                    catch (Exception e) { Log("givecoins failed: " + e.Message); }
                    continue;
                }
                if (l == "gamedata") { try { GameData.Write(Path.Combine(Dir, "gamedata.txt")); Log("gamedata written"); } catch (Exception e) { Log("gamedata failed: " + e); } continue; }
                if (l == "clear") { Queue.Clear(); Ctl.Reset(); continue; }
                if (l.StartsWith("speed ")) { Speed = float.Parse(l.Substring(6), System.Globalization.CultureInfo.InvariantCulture); continue; }
                Queue.Enqueue(l);
            }
        }

        void DebugDump()
        {
            try
            {
                foreach (var sh in UnityEngine.Object.FindObjectsOfType<PayableShop>())
                    Log("SHOP " + sh.name + " x" + sh.transform.position.x.ToString("0.0") + " items=" + sh.GetItemCount() + "/" + sh.maxItems
                        + " allow=" + Acc.Get<bool>(sh, "_allowPayment") + " canPay=" + sh.CanPay() + " enabled=" + sh.isActiveAndEnabled);
                foreach (var pe in UnityEngine.Object.FindObjectsOfType<Peasant>())
                {
                    var fsm = Acc.Get<object>(pe, "_fsm");
                    object cur = null; try { cur = fsm.GetType().GetField("current").GetValue(fsm); } catch { }
                    var tgt = Acc.Get<GameObject>(pe, "_target");
                    Log("PEASANT x" + pe.transform.position.x.ToString("0.0") + " state=" + cur + " target=" + (tgt != null ? tgt.name : "-") + " stunned=" + Acc.Get<bool>(pe, "_stunned"));
                }
                foreach (var d in UnityEngine.Object.FindObjectsOfType<DroppableTool>())
                    Log("TOOL " + d.name + " x" + d.transform.position.x.ToString("0.0") + " picked=" + d.pickedUp + " fake=" + d.fake + " active=" + d.gameObject.activeInHierarchy);
                foreach (var kn in UnityEngine.Object.FindObjectsOfType<Knight>())
                {
                    var fsm = Acc.Get<object>(kn, "_fsm");
                    object cur = null; try { cur = fsm.GetType().GetField("current").GetValue(fsm); } catch { }
                    var arObj = Acc.Get<object>(kn, "_archers");
                    int arCount = arObj != null ? (int)arObj.GetType().GetProperty("Count").GetValue(arObj, null) : -1;
                    bool cc = false; try { cc = (bool)Acc.Call(kn, "CanCharge"); } catch { }
                    Log("KNIGHT x" + kn.transform.position.x.ToString("0.0") + " side=" + kn.side + " state=" + cur + " archers=" + arCount + "/" + kn.maxArchers + " canCharge=" + cc + " coins=" + (Acc.Wallet(kn) != null ? Acc.Wallet(kn).coins + "/" + Acc.Wallet(kn).maxCoins : "-") + GameState.CoinFields(kn) + " hp=" + (kn.GetComponent<Damageable>() != null ? kn.GetComponent<Damageable>().hitPoints : -1));
                }
                foreach (var b in UnityEngine.Object.FindObjectsOfType<PayableBorder>())
                    Log("BORDER x" + b.transform.position.x.ToString("0.0") + " canPay=" + b.CanPay() + " range=" + b.knightRange);
                {
                    var grass = UnityEngine.Object.FindObjectsOfType<Grass>();
                    var stages = new int[8]; int tall = 0; float gmin = 1e6f, gmax = -1e6f;
                    foreach (var g in grass)
                    {
                        int st0 = 0; try { st0 = g.stage; } catch { }
                        if (st0 >= 0 && st0 < 8) stages[st0]++;
                        if (Acc.Get<GameObject>(g, "_tallGrass") != null) tall++;
                        gmin = Mathf.Min(gmin, g.transform.position.x); gmax = Mathf.Max(gmax, g.transform.position.x);
                    }
                    var bx = new System.Text.StringBuilder();
                    foreach (var bu in UnityEngine.Object.FindObjectsOfType<Bunny>()) bx.Append(" " + bu.transform.position.x.ToString("0"));
                    Log("GRASS patches=" + grass.Length + " x" + gmin.ToString("0") + ".." + gmax.ToString("0") + " stages=" + string.Join("/", Array.ConvertAll(stages, v => v.ToString())) + " tall=" + tall + " | bunnies at" + bx);
                }
                foreach (var su in UnityEngine.Object.FindObjectsOfType<Statue>())
                {
                    var sb2 = new System.Text.StringBuilder("STATUE " + su.name.Replace("(Clone)", "") + " x" + su.transform.position.x.ToString("0"));
                    foreach (var f in su.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) sb2.Append(" " + f.Name + "=" + f.GetValue(su));
                    Log(sb2.ToString());
                }
                foreach (var st in UnityEngine.Object.FindObjectsOfType<Steed>())
                    Log("STEED " + st.name.Replace("(Clone)", "") + " x" + st.transform.position.x.ToString("0") + " price=" + st.price + " walk=" + st.walkSpeed + " run=" + st.runSpeed + " runStamina=" + st.runStaminaRate + " walkStamina=" + st.walkStaminaRate + " forest=" + st.forestSpeedMultiplier + " tired=" + st.tiredDuration + " eatDelay=" + st.eatDelay + " eatDuration=" + st.eatDuration + " wellFed=" + st.wellFedDuration + " standStamina=" + st.standStaminaRate);
                try
                {
                    var am = Managers.achievements;
                    foreach (var ach in am.achievements)
                        Log("ACH " + ach.steamID + " counter=" + ach.triggeringCounter + " flag=" + ach.triggeringFlag + " x=" + ach.conditionX + " y=" + ach.conditionY
                            + " series=" + (ach.series != null ? string.Join(",", ach.series.ConvertAll(v => v.ToString()).ToArray()) : "-")
                            + " custom=" + (ach.customCheck != null && ach.customCheck.GetPersistentEventCount() > 0 ? ach.customCheck.GetPersistentMethodName(0) : "-"));
                }
                catch (Exception e) { Log("ACH dump failed: " + e.Message); }
                foreach (var po in UnityEngine.Object.FindObjectsOfType<Portal>())
                {
                    var d = po.GetComponent<Damageable>();
                    Log("PORTAL x" + po.transform.position.x.ToString("0") + " outer=" + po.outerSpawner + " hp=" + (d != null ? d.hitPoints + "/" + d.initialHitPoints + " useHp=" + d.useHitPoints + " damagedBy=" + d.damagedBy + " invulnerable=" + d.invulnerable : "no damageable") + " defenseRange=" + po.defenseTriggerRange
                        + " opened=" + Acc.Get<bool>(po, "_opened") + " crumbleAfterSpawn=" + Acc.Get<bool>(po, "_crumbleAfterSpawn") + " closeAfterSpawn=" + Acc.Get<bool>(po, "_closeAfterSpawn")
                        + " queue=" + ((Acc.Get<object>(po, "_spawnQueue") as System.Collections.ICollection) != null ? (Acc.Get<object>(po, "_spawnQueue") as System.Collections.ICollection).Count.ToString() : "?")
                        + " nextAt=" + Acc.Get<float>(po, "_spawnNextAt").ToString("0.0") + " now=" + Time.time.ToString("0.0") + " active=" + po.isActiveAndEnabled);
                }
                foreach (var fh in UnityEngine.Object.FindObjectsOfType<Farmhouse>())
                    Log("FARMHOUSE x" + fh.transform.position.x.ToString("0.0") + " level=" + fh.level + " name=" + fh.name);
                var sbp = new System.Text.StringBuilder("PAYABLE LIST (campfire x" + Managers.kingdom.campfirePosition.ToString("0") + "):");
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Payable>())
                    sbp.Append(" " + p.name.Replace("(Clone)", "") + "@" + p.transform.position.x.ToString("0") + (p.CanPay() ? "" : "!"));
                Log(sbp.ToString());
                var pm = Managers.payables;
                if (pm != null) Log("PAYABLES enabledByDefault=" + Acc.Get<bool>(pm, "_enabledByDefault"));
                var tm = Managers.tutorial;
                if (tm != null) Log("TUTORIAL " + string.Join(", ", Array.ConvertAll(tm.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public), f => { object v = null; try { v = f.GetValue(tm); } catch { } return f.Name + "=" + v; })));
            }
            catch (Exception e) { Log("debug failed: " + e); }
        }

        public void DrawGUI()
        {
            if (!BotActive && !AutoBrain) { GUI.Label(new Rect(10, 10, 600, 22), "KingdomBot: off (F8)"); return; }
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, 10, 1200, 22), "KingdomBot " + (BotActive ? "ACTIVE" : "paused") + (AutoBrain ? " [AUTO]" : "") + " " + Speed + "x  mode " + Brain.Mode + (Brain.ConfigMode == "auto" ? " (auto)" : "") + "  attempt " + (Brain.Runs + 1) + "  " + _status);
        }
    }

    [HarmonyPatch(typeof(Managers), "Start")]
    static class Patch_ManagersStart
    {
        static void Postfix() { Runner.Ensure(); }
    }

    [HarmonyPatch(typeof(Game), "Update")]
    static class Patch_GameUpdate
    {
        static void Postfix() { Runner.Ensure(); }
    }

    public class Runner : MonoBehaviour
    {
        static Runner _inst;
        public static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("KingdomBotRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _inst = go.AddComponent<Runner>();
            Plugin.Log("Runner created");
        }
        void Start()
        {
            Application.runInBackground = true;           // keep running when minimized / unfocused
            // NOTE: Screen.SetResolution here crashes Unity 5.3 -> windowed mode is set via launch args (start.ps1)
        }
        void Update()
        {
            if (!Application.runInBackground) Application.runInBackground = true;
            try { Plugin.Instance.Tick(); } catch (Exception e) { Plugin.Log("Tick error: " + e); }
        }
        void OnGUI() { Plugin.Instance.DrawGUI(); }
    }

    /// <summary>Low-level input the bot feeds into Player each frame.</summary>
    public class Controller
    {
        public int Dir;
        public bool Sprint;
        public bool Pay;
        bool _payPrev, _sprintPrev;

        public void Reset() { Dir = 0; Sprint = false; Pay = false; }

        public void Apply(Player p)
        {
            bool payDown = Pay && !_payPrev;
            bool sprintStart = Sprint && !_sprintPrev;
            bool sprintStop = !Sprint && _sprintPrev;
            Acc.Call(p, "UpdateActionState", Dir, sprintStart, sprintStop);
            Acc.Call(p, "UpdatePayState", Pay, payDown);
            _payPrev = Pay; _sprintPrev = Sprint;
        }
    }

    /// <summary>User observation "overflowing purse": a full coin bag spills coins (some into the water = lost). Count them.</summary>
    [HarmonyPatch(typeof(Player), "CoinFellFromBag")]
    static class Patch_CoinFell
    {
        public static int Fell;
        static void Prefix(Player __instance)
        {
            Fell++;
            if (Fell == 1 || Fell % 10 == 0) { float p = -1f; try { p = KingdomBot.Acc.Get<float>(__instance, "overflowCoinsWaterProbability"); } catch { } Plugin.Log("PURSE OVERFLOW: coin fell out of the bag (#" + Fell + ", purse " + GameState.Coins + ", water prob " + p + ", day " + GameState.Day + ")"); }
        }
    }

    /// <summary>Diagnostics: log who sets the achievement flags (e.g. "coin dropped on ground").</summary>
    [HarmonyPatch(typeof(AchievementManager), "SetFlag")]
    static class Patch_SetFlag
    {
        static void Prefix(AchievementManager __instance, object flag)
        {
            try
            {
                int f = Convert.ToInt32(flag);
                if (__instance.flags != null && f < __instance.flags.Length && !__instance.flags[f])
                {
                    var p = GameState.Player;
                    Plugin.Log("FLAG " + flag + " set (day " + GameState.Day + ", h" + GameState.Hour.ToString("0.0") + ", x" + (p != null ? GameState.PX.ToString("0") : "?")
                        + ", queue " + Plugin.Instance.Queue.Describe() + ") " + Environment.StackTrace);
                }
            }
            catch { }
        }
    }

    /// <summary>Diagnostics: who hits our archers (and when/where)?</summary>
    [HarmonyPatch(typeof(Damageable), "ReceiveDamage")]
    static class Patch_ArcherDamage
    {
        static void Prefix(Damageable __instance, GameObject damager)
        {
            try
            {
                if (__instance == null || __instance.GetComponent<Archer>() == null) return;
                var k = Managers.kingdom;
                Plugin.Log(string.Format("ARCHER HIT by {0} at x{1:0} (h{2:0.0}, day {3}, {4})", damager != null ? damager.name.Replace("(Clone)", "") : "?",
                    __instance.transform.position.x, GameState.Hour, GameState.Day, k != null && k.isDaytime ? "day" : "night")
                    + (damager != null ? " src x" + damager.transform.position.x.ToString("0") + " comps " + string.Join(",", System.Array.ConvertAll(damager.GetComponents<Component>(), c => c.GetType().Name)) + " layer " + LayerMask.LayerToName(damager.layer) : ""));
            }
            catch { }
        }
    }

    /// <summary>Diagnostics: every hit on a portal (who hits, damage, hp left).</summary>
    [HarmonyPatch(typeof(Damageable), "ReceiveDamage")]
    static class Patch_PortalHit
    {
        static void Postfix(Damageable __instance, int __0, UnityEngine.GameObject __1, DamageSource __2)
        {
            try
            {
                if (__instance == null || __instance.GetComponentInParent<Portal>() == null) return;
                Plugin.Log("PORTAL HIT x" + __instance.transform.position.x.ToString("0") + " dmg=" + __0 + " by " + (__1 != null ? __1.name : "?") + " src=" + __2 + " hp=" + __instance.hitPoints);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Character), "Grab")]
    static class Patch_Grab
    {
        static void Prefix(Character __instance)
        {
            try { Plugin.Log(string.Format("GRABBED: {0} at x{1:0} (h{2:0.0}, day {3})", __instance.name.Replace("(Clone)", ""), __instance.transform.position.x, GameState.Hour, GameState.Day)); } catch { }
        }
    }

    [HarmonyPatch(typeof(Player), "ReceiveInput")]
    static class Patch_ReceiveInput
    {
        static bool Prefix(Player __instance)
        {
            var pl = Plugin.Instance;
            if (pl == null || !pl.BotActive) return true;
            pl.Ctl.Apply(__instance);
            return false;
        }
    }

    public static class GameState
    {
        public static Player Player { get { var k = Managers.kingdom; return k != null ? k.player : null; } }
        public static float PX { get { return Player.transform.position.x; } }
        public static int Coins { get { return Acc.Wallet(Player).coins; } }
        public static string CoinFields(Component c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var comp in c.GetComponents<Component>())
                foreach (var f in comp.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (f.Name.IndexOf("oin", System.StringComparison.OrdinalIgnoreCase) >= 0 && (f.FieldType.IsPrimitive || f.FieldType.IsEnum))
                        sb.Append(" " + comp.GetType().Name + "." + f.Name + "=" + f.GetValue(comp));
            return sb.ToString();
        }
        public static int MaxCoins { get { return Acc.Wallet(Player).maxCoins; } }
        public static int Day { get { return Managers.director != null ? Managers.director.currentDay : -1; } }
        public static float Hour { get { return Managers.director != null ? Managers.director.currentTime : -1; } }
        public static bool IsDay { get { return Managers.kingdom.isDaytime; } }
        public static float Campfire { get { return Managers.kingdom.campfirePosition; } }
    }

    /// <summary>Sequential command execution: goto X / pay / tap N / wait S / walkto X</summary>
    public class CommandQueue
    {
        readonly Queue<string> _q = new Queue<string>();
        string _cur; float _t0, _u0; int _n; bool _started;
        int _lastCoinIdx = -1, _pressFrames;

        public bool Idle { get { return _cur == null && _q.Count == 0; } }
        /// <summary>true while creeping onto a payable (game speed is lowered for precision)</summary>
        int _payTarget, _matchFrames, _lastPs = -1;
        float _payStartDist = -1f;
        static bool IdIsShield(int id)
        {
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<PayableShield>()) if (ps.GetInstanceID() == id) return true;
            return false;
        }
        public bool Positioning { get { return _cur != null && ((_cur.StartsWith("payid") && !_started) || _cur.StartsWith("handtap")); } }
        public void Clear() { _q.Clear(); _cur = null; }
        public void Enqueue(string c) { _q.Enqueue(c); }
        public string Describe() { return (_cur ?? "-") + (_q.Count > 0 ? " (+" + _q.Count + ")" : ""); }

        static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

        public void Tick(Plugin pl)
        {
            var c = pl.Ctl;
            if (_cur == null)
            {
                if (_q.Count == 0) { c.Dir = 0; c.Sprint = false; c.Pay = false; return; }
                _cur = _q.Dequeue(); _t0 = Time.time; _u0 = Time.unscaledTime; _n = 0; _started = false; _payTarget = 0; _matchFrames = 0; _payStartDist = -1f; _lastPs = -1; _lastCoinIdx = -1; _pressFrames = 0;
            }
            var parts = _cur.Split(' ');
            var p = GameState.Player;
            float px = GameState.PX;
            bool done = false;
            switch (parts[0])
            {
                case "goto":   // goto X [tol] – gallops when far away
                case "walkto":
                {
                    float x = F(parts[1]);
                    float reqTol = parts.Length > 2 ? F(parts[2]) : 0.4f;
                    float tol = Mathf.Max(reqTol, 0.08f * Time.timeScale);   // larger steps per frame at high speed
                    float dx = x - px;
                    c.Pay = false;
                    // at 32x the tolerance is ~2.5: coins/chests/beggars closer than that were "reached" without ever being touched
                    // (8 chest coins stayed on the ground). Slow the game down for the last few units instead of giving up.
                    if (Mathf.Abs(dx) <= tol && Mathf.Abs(dx) > reqTol && Time.timeScale > 3.5f && pl.Brain != null && Time.time - _t0 < 30f) { pl.Brain.SlowUntil = Time.unscaledTime + 0.6f; }
                    else if (Mathf.Abs(dx) <= tol) { c.Dir = 0; c.Sprint = false; done = true; break; }
                    c.Dir = dx > 0 ? 1 : -1;
                    c.Sprint = parts[0] == "goto" && pl.Brain.MaySprint && Mathf.Abs(dx) > 6f && p.stamina > 0.3f;
                    float maxT = parts.Length > 3 ? F(parts[3]) : 90f;
                    if (Time.time - _t0 > maxT) { c.Dir = 0; c.Sprint = false; done = true; }
                    break;
                }
                case "pay":    // hold pay until the transaction completes or nothing selectable
                {
                    c.Dir = 0; c.Sprint = false;
                    float timeout = parts.Length > 1 ? F(parts[1]) : 12f;
                    if (!_started) { c.Pay = true; _started = true; break; }
                    if (Acc.Selected(p) == null && Time.time - _t0 > 0.5f) { c.Pay = false; done = true; break; }
                    if (Acc.PayState(p) == 3 || (Acc.PayState(p) == 0 && Time.time - _t0 > 1f) || Time.time - _t0 > timeout || GameState.Coins == 0 && Acc.PayState(p) != 2)
                    { c.Pay = false; done = true; }
                    break;
                }
                case "payid":  // pay a specific payable (by instance id); aborts if something else is selected
                {
                    c.Dir = 0; c.Sprint = false;
                    int id = int.Parse(parts[1]);
                    var sel = Acc.Selected(p);
                    if (!_started)
                    {
                        // never stand around paying while an enemy is close (daytime squids took the crown during a long payid)
                        bool danger = false;
                        foreach (var en in UnityEngine.Object.FindObjectsOfType<Enemy>())
                        {
                            if (!Brain.IsLiveEnemy(en) || Mathf.Abs(en.transform.position.x - px) >= 8f || Brain.WallBetween(px, en.transform.position.x)) continue;
                            // a troll hit only costs a coin: with a purse, small enemies are no reason to abort (the banner
                            // payment at the outer wall was aborted every day); big ones and squids (grab) still are
                            bool big = en.GetComponent<Boss>() != null || en.GetComponent<Ogre>() != null || en.GetComponent<Squid>() != null;
                            if (big || GameState.Coins < 6) { danger = true; break; }
                        }
                        if (danger) { Plugin.Log("payid: aborted, enemy close (player x" + px.ToString("0.0") + ")"); c.Dir = 0; c.Pay = false; done = true; break; }
                    }
                    if (_payStartDist < 0f) _payStartDist = parts.Length > 2 ? Mathf.Abs(F(parts[2]) - px) : 0f;   // timeout grows with the distance still to walk
                    if (!_started)
                    {
                        // only press once the intended payable is selected (selection updates every frame)
                        // press only if the target can really be paid now - pressing on an unpayable target drops a coin
                        bool payable = false; try { payable = sel != null && sel.CanPay() && GameState.Coins > 0; } catch { }
                        // shield shops stand next to each other and are interchangeable: any selected shield shop will do
                        bool match = sel != null && (sel.GetInstanceID() == id || (sel is PayableShield && IdIsShield(id)));
                        if (match && payable && _matchFrames < 3) { _matchFrames++; c.Dir = 0; break; }   // stand still first: a moving king slides out of the trigger mid-payment
                        if (!(match && payable)) _matchFrames = 0;
                        if (match && payable) { id = sel.GetInstanceID(); _payTarget = id; _started = true; _t0 = Time.time; c.Dir = 0; c.Pay = true; }
                        else if (Time.time - _t0 > 10f + _payStartDist * 1.5f)
                        {
                            string tinfo = "?";
                            foreach (var tp in UnityEngine.Object.FindObjectsOfType<Payable>())
                                if (tp.GetInstanceID() == id)
                                {
                                    var col = tp.GetComponent<Collider2D>();
                                    bool cp = false; try { cp = tp.CanPay(); } catch { }
                                    tinfo = tp.name.Replace("(Clone)", "") + " x" + tp.transform.position.x.ToString("0.0") + (col != null ? " col " + col.bounds.min.x.ToString("0.0") + ".." + col.bounds.max.x.ToString("0.0") + " enabled=" + col.enabled : " no collider") + " canPay=" + cp + " active=" + tp.isActiveAndEnabled;
                                }
                            Plugin.Log("payid: target not selected (" + (sel != null ? sel.name : "none") + ") player x" + px.ToString("0.0") + " target " + tinfo);
                            done = true;
                        }
                        else if (parts.Length > 2)
                        {
                            // creep towards the target until its trigger selects it (we may have stopped just outside at high speed)
                            float tx = F(parts[2]);
                            // a neighbouring payable (e.g. a tower next to the castle) holds the selection: sweep +-1.5
                            // around the target until the target's trigger wins
                            if (sel != null && sel.GetInstanceID() != id)
                            {
                                // a neighbouring payable holds the selection: aim at the far side of the target's trigger
                                // (away from the neighbour); if that does not help, sweep
                                Collider2D tcol = null;
                                foreach (var tp in UnityEngine.Object.FindObjectsOfType<Payable>()) if (tp.GetInstanceID() == id) { tcol = tp.GetComponent<Collider2D>(); break; }
                                if (tcol != null)
                                {
                                    float cx = tcol.bounds.center.x, side = cx >= sel.transform.position.x ? 1f : -1f;
                                    tx = cx + side * tcol.bounds.extents.x * 0.7f;
                                }
                                if (Time.time - _t0 > 5f) tx += Mathf.PingPong((Time.time - _t0) * 2f, 6f) - 3f;
                            }
                            float dx = tx - px;
                            if (Mathf.Abs(dx) > 0.05f) c.Dir = dx > 0 ? 1 : -1;
                            // step-walk: move every other frame to avoid overshooting narrow triggers
                            if (Mathf.Abs(dx) < 1f && (Time.frameCount & 1) == 1) c.Dir = 0;   // step-walk only close to the trigger
                        }
                        break;
                    }
                    int ps = Acc.PayState(p);
                    if (ps != _lastPs) { Plugin.Log("payid trace: state " + Acc.PayStateName(p) + " coins " + GameState.Coins + " t " + (Time.time - _t0).ToString("0.00") + " sel " + (sel != null ? sel.name.Replace("(Clone)", "") : "none")); _lastPs = ps; }
                    // never release in the "Holding" phase (state 1): releasing there drops a coin on the ground
                    // keep holding through "Holding" (1) AND "Transaction" (2): releasing during the transaction cancelled
                    // expensive purchases (castle 5 for 9 coins) half-way and the coins were lost
                    if ((ps == 1 || ps == 2) && Time.time - _t0 < 15f) { c.Pay = true; break; }
                    if (_payTarget != 0) id = _payTarget;
                    if (sel != null && sel.GetInstanceID() != id && ps != 3) { Plugin.Log("payid: wrong selection " + sel.name); c.Pay = false; done = true; break; }
                    if (sel == null && Time.time - _t0 > 0.6f && ps != 3) { Plugin.Log("payid: selection lost while paying (player x" + px.ToString("0.0") + ")"); c.Pay = false; done = true; break; }
                    if (ps == 3 || (ps == 0 && Time.time - _t0 > 1f) || Time.time - _t0 > 16f) { c.Pay = false; done = true; }
                    break;
                }
                case "handtap":   // handtap <beggarId>: drop one coin only while standing right over that beggar (coin never touches the ground)
                {
                    c.Sprint = false; c.Pay = false;
                    int bid = int.Parse(parts[1]);
                    Beggar target = null;
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<Beggar>()) if (b.GetInstanceID() == bid) { target = b; break; }
                    if (target == null || Time.time - _t0 > 8f) { c.Dir = 0; done = true; break; }
                    float dx = target.transform.position.x - px;
                    if (Mathf.Abs(dx) > 0.2f) { c.Dir = dx > 0 ? 1 : -1; if (Mathf.Abs(dx) < 1f && (Time.frameCount & 1) == 1) c.Dir = 0; break; }
                    c.Dir = 0;
                    if (!_started)
                    {
                        _started = true; _pressFrames = 1;
                        var selH = GameState.Player != null ? Acc.Selected(GameState.Player) : null;
                        Plugin.Log("handtap at x" + px.ToString("0.0") + " coins=" + GameState.Coins + " selected=" + (selH != null ? selH.name : "none"));
                    }
                    c.Pay = _pressFrames-- > 0;
                    if (_pressFrames < -3) done = true;
                    break;
                }
                case "tap":    // drop N coins (short taps)
                {
                    c.Dir = 0; c.Sprint = false;
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                    // press for 2 frames, release, pause 0.4s game time between coins
                    _n++;
                    float elapsed = Time.time - _t0;
                    int coinIdx = (int)(elapsed / 0.4f);
                    if (coinIdx >= count) { c.Pay = false; done = true; break; }
                    if (coinIdx != _lastCoinIdx) { _lastCoinIdx = coinIdx; _pressFrames = 1; }  /* exactly one frame: longer counts as "hold" at high speed */
                    c.Pay = _pressFrames-- > 0;
                    break;
                }
                case "shot":
                    Application.CaptureScreenshot(Path.Combine(Plugin.Dir, "shot.png")); done = true; break;
                case "slow":   // slow N: cap the game speed at 3x for N game seconds (chest triggers)
                    pl.Brain.SlowUntil = Time.unscaledTime + F(parts[1]) / 3f; done = true; break;
                case "wait":
                    c.Dir = 0; c.Sprint = false; c.Pay = false;
                    if (Time.time - _t0 >= F(parts[1]) || Time.unscaledTime - _u0 >= F(parts[1])) done = true;   /* also ends if game time stands still */
                    break;
                default:
                    Plugin.Log("Unknown command: " + _cur); done = true; break;
            }
            if (done) { c.Pay = false; _cur = null; }
        }
    }
}
