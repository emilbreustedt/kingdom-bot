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
        public float Speed = 16f;        // F10: game speed while playing normally
        public readonly Controller Ctl = new Controller();
        public readonly CommandQueue Queue = new CommandQueue();
        public Brain Brain;

        float _nextDump, _nextCmdPoll, _nextGuard, _nextIntroPoke;
        string _status = "";

        void Awake()
        {
            Instance = this;
            Dir = Path.Combine(Paths.BepInExRootPath, "KingdomBot");
            Directory.CreateDirectory(Dir);
            Brain = new Brain(this);
            // user settings: BepInEx/config/emil.kingdombot.cfg
            Brain.ConfigMode = Config.Bind("General", "Mode", "auto",
                "auto = pick the run type from the achievements still missing on Steam; or master | fire3 | pacifist").Value;
            Speed = Config.Bind("General", "Speed", 16f, "Game speed while the bot plays (1-16). F10 cycles at runtime.").Value;
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
            if (Input.GetKeyDown(KeyCode.F10)) { Speed = Speed >= 16f ? 1f : Speed * 2f; Log("Speed: " + Speed); }
            ApplySpeed();

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
            float want = Queue.Positioning ? Mathf.Min(Speed, 2f) : Speed;   // precise positioning for payments
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
                    try { if (Managers.saves != null && GameState.Player != null && Managers.game != null && (int)Managers.game.gameState == 2) Managers.saves.Save("AUTO"); Log("Saved run (day " + GameState.Day + ")"); }
                    catch (Exception e) { Log("save failed: " + e.Message); }
                    Application.Quit(); continue;
                }
                if (l == "newgame") { try { Queue.Clear(); Ctl.Reset(); Acc.Call(Managers.game, "Restart"); Log("New game requested"); } catch (Exception e) { Log("newgame failed: " + e.Message); } continue; }
                if (l.StartsWith("mode ")) { Brain.ConfigMode = l.Substring(5).Trim(); Brain.Mode = Brain.ConfigMode == "auto" ? Campaign.PickMode(Brain.PacifistEnabled) : Brain.ConfigMode; Log("Mode: " + Brain.ConfigMode + " -> " + Brain.Mode); continue; }
                if (l == "achievements") { Log(Campaign.Summary()); continue; }
                if (l == "debug") { DebugDump(); continue; }
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
                    Log("KNIGHT x" + kn.transform.position.x.ToString("0.0") + " side=" + kn.side + " state=" + cur + " archers=" + arCount + "/" + kn.maxArchers + " canCharge=" + cc);
                }
                foreach (var b in UnityEngine.Object.FindObjectsOfType<PayableBorder>())
                    Log("BORDER x" + b.transform.position.x.ToString("0.0") + " canPay=" + b.CanPay() + " range=" + b.knightRange);
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
                _cur = _q.Dequeue(); _t0 = Time.time; _u0 = Time.unscaledTime; _n = 0; _started = false; _lastCoinIdx = -1; _pressFrames = 0;
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
                    float tol = parts.Length > 2 ? F(parts[2]) : 0.4f;
                    tol = Mathf.Max(tol, 0.08f * Time.timeScale);   // larger steps per frame at high speed
                    float dx = x - px;
                    c.Pay = false;
                    if (Mathf.Abs(dx) <= tol) { c.Dir = 0; c.Sprint = false; done = true; break; }
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
                        // only press once the intended payable is selected (selection updates every frame)
                        // press only if the target can really be paid now - pressing on an unpayable target drops a coin
                        bool payable = false; try { payable = sel != null && sel.CanPay() && GameState.Coins > 0; } catch { }
                        if (sel != null && sel.GetInstanceID() == id && payable) { _started = true; _t0 = Time.time; c.Dir = 0; c.Pay = true; }
                        else if (Time.time - _t0 > 4f) { Plugin.Log("payid: target not selected (" + (sel != null ? sel.name : "none") + ")"); done = true; }
                        else if (parts.Length > 2)
                        {
                            // creep towards the target until its trigger selects it (we may have stopped just outside at high speed)
                            float tx = F(parts[2]);
                            float dx = tx - px;
                            if (Mathf.Abs(dx) > 0.05f) c.Dir = dx > 0 ? 1 : -1;
                            // step-walk: move every other frame to avoid overshooting narrow triggers
                            if ((Time.frameCount & 1) == 1) c.Dir = 0;
                        }
                        break;
                    }
                    int ps = Acc.PayState(p);
                    // never release in the "Holding" phase (state 1): releasing there drops a coin on the ground
                    if (ps == 1 && Time.time - _t0 < 6f) { c.Pay = true; break; }
                    if (sel != null && sel.GetInstanceID() != id && ps != 3) { Plugin.Log("payid: wrong selection " + sel.name); c.Pay = false; done = true; break; }
                    if (sel == null && Time.time - _t0 > 0.6f && ps != 3) { c.Pay = false; done = true; break; }
                    if (ps == 3 || (ps == 0 && Time.time - _t0 > 1f) || Time.time - _t0 > 12f) { c.Pay = false; done = true; }
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
                    if (Mathf.Abs(dx) > 0.2f) { c.Dir = dx > 0 ? 1 : -1; if ((Time.frameCount & 1) == 1) c.Dir = 0; break; }
                    c.Dir = 0;
                    if (!_started) { _started = true; _pressFrames = 1; }
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
