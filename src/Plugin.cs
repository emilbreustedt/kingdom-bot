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

        float _nextDump, _nextCmdPoll, _nextGuard;
        string _status = "";

        void Awake()
        {
            Instance = this;
            Dir = Path.Combine(Paths.BepInExRootPath, "KingdomBot");
            Directory.CreateDirectory(Dir);
            Brain = new Brain(this);
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
            if (Mathf.Approximately(Time.timeScale, 1f) || Mathf.Approximately(Time.timeScale, _appliedSpeed))
            {
                Time.timeScale = Speed;
                _appliedSpeed = Speed;
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
                if (l == "newgame") { try { Acc.Call(Managers.game, "Restart"); Log("New game requested"); } catch (Exception e) { Log("newgame failed: " + e.Message); } continue; }
                if (l.StartsWith("mode ")) { Brain.Mode = l.Substring(5).Trim(); Log("Mode: " + Brain.Mode); continue; }
                if (l == "clear") { Queue.Clear(); Ctl.Reset(); continue; }
                if (l.StartsWith("speed ")) { Speed = float.Parse(l.Substring(6), System.Globalization.CultureInfo.InvariantCulture); continue; }
                Queue.Enqueue(l);
            }
        }

        public void DrawGUI()
        {
            if (!BotActive && !AutoBrain) { GUI.Label(new Rect(10, 10, 600, 22), "KingdomBot: aus (F8)"); return; }
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, 10, 1200, 22), "KingdomBot " + (BotActive ? "AKTIV" : "pausiert") + (AutoBrain ? " [AUTO]" : "") + " " + Speed + "x  Versuch " + (Brain.Runs + 1) + "  " + _status);
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
        string _cur; float _t0; int _n; bool _started;
        int _lastCoinIdx = -1, _pressFrames;

        public bool Idle { get { return _cur == null && _q.Count == 0; } }
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
                _cur = _q.Dequeue(); _t0 = Time.time; _n = 0; _started = false; _lastCoinIdx = -1; _pressFrames = 0;
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
                        if (sel != null && sel.GetInstanceID() == id) { _started = true; _t0 = Time.time; c.Dir = 0; c.Pay = true; }
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
                    if (sel != null && sel.GetInstanceID() != id && ps != 3) { Plugin.Log("payid: wrong selection " + sel.name); c.Pay = false; done = true; break; }
                    if (sel == null && Time.time - _t0 > 0.6f && ps != 3) { c.Pay = false; done = true; break; }
                    if (ps == 3 || (ps == 0 && Time.time - _t0 > 1f) || Time.time - _t0 > 12f) { c.Pay = false; done = true; }
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
                    if (Time.time - _t0 >= F(parts[1])) done = true;
                    break;
                default:
                    Plugin.Log("Unknown command: " + _cur); done = true; break;
            }
            if (done) { c.Pay = false; _cur = null; }
        }
    }
}
