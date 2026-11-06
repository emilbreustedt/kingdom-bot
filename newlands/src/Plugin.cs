using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace KingdomBot.NewLands
{
    /// <summary>
    /// Kingdom: New Lands port of KingdomBot (scaffold). Same architecture as the Classic plugin:
    /// input hook on Player.IControllable.ReceiveInput, command queue, JSON state export, speed control.
    /// </summary>
    [BepInPlugin("emil.kingdombot.newlands", "KingdomBot New Lands", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static string Dir;

        public bool BotActive, AutoBrain;
        public float Speed = 16f;
        public readonly Controller Ctl = new Controller();
        public readonly CommandQueue Queue = new CommandQueue();
        public Brain Brain;
        float _nextDump, _nextCmdPoll, _nextGuard, _nextIntroPoke, _appliedSpeed = 1f;
        string _status = "";

        void Awake()
        {
            Instance = this;
            Dir = Path.Combine(Paths.BepInExRootPath, "KingdomBot");
            Directory.CreateDirectory(Dir);
            Brain = new Brain(this);
            Speed = Config.Bind("General", "Speed", 16f, "Game speed while the bot plays (1-16). F10 cycles at runtime.").Value;
            BotActive = AutoBrain = Config.Bind("General", "AutoStart", true, "Bot takes over as soon as the game runs (F8/F9 toggle).").Value;
            new Harmony("emil.kingdombot.newlands").PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("KingdomBot New Lands loaded. F8 = bot on/off, F9 = strategy on/off, F10 = speed");
        }

        public static void Log(string s) { Instance.Logger.LogInfo(s); }

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F8)) { BotActive = !BotActive; if (!BotActive) { Queue.Clear(); Ctl.Reset(); } }
            if (Input.GetKeyDown(KeyCode.F9)) AutoBrain = !AutoBrain;
            if (Input.GetKeyDown(KeyCode.F10)) Speed = Speed >= 16f ? 1f : Speed * 2f;
            ApplySpeed();
            if (Time.unscaledTime >= _nextCmdPoll) { _nextCmdPoll = Time.unscaledTime + 0.5f; PollCommandFile(); }

            var gm = Managers.game;
            if (gm != null && (int)gm.state == 1 && BotActive && AutoBrain)   // intro
            {
                if (!Queue.Idle) Queue.Clear();
                Ctl.Reset();
                if (Time.unscaledTime >= _nextIntroPoke)
                {
                    _nextIntroPoke = Time.unscaledTime + 1f;
                    try { Acc.Call(gm, "AbortIntroAndTutorial"); Log("Intro + tutorial skipped"); }
                    catch (Exception e) { Log("intro skip failed: " + e.Message); }
                }
            }

            if (GameState.Player != null && BotActive)
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

            if (Time.unscaledTime >= _nextDump)
            {
                _nextDump = Time.unscaledTime + 1f;
                try { StateDump.Write(Path.Combine(Dir, "state.json"), this); } catch (Exception e) { Log("Dump error: " + e.Message); }
            }
        }

        void ApplySpeed()
        {
            var g = Managers.game;
            bool normal = g != null && (int)g.state == 2 && (int)g.menuState == 0;
            if (!normal) { _appliedSpeed = 1f; return; }
            float want = Queue.Positioning ? Mathf.Min(Speed, 2f) : Speed;
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
                if (l == "clear") { Queue.Clear(); Ctl.Reset(); continue; }
                if (l.StartsWith("speed ")) { Speed = float.Parse(l.Substring(6), System.Globalization.CultureInfo.InvariantCulture); continue; }
                if (l == "newgame") { Queue.Clear(); Ctl.Reset(); try { Managers.game.Restart(); } catch (Exception e) { Log("newgame failed: " + e.Message); } continue; }
                if (l == "savequit") { Application.Quit(); continue; }   // New Lands saves on quit (OnApplicationQuit) - verify!
                Queue.Enqueue(l);
            }
        }

        public void DrawGUI()
        {
            if (!BotActive && !AutoBrain) { GUI.Label(new Rect(10, 10, 600, 22), "KingdomBot: off (F8)"); return; }
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, 10, 1400, 22), "KingdomBot NL " + (BotActive ? "ACTIVE" : "paused") + " " + Speed + "x  " + _status);
        }
    }

    [HarmonyPatch(typeof(Managers), "Start")]
    static class Patch_ManagersStart { static void Postfix() { Runner.Ensure(); } }

    [HarmonyPatch(typeof(Game), "Update")]
    static class Patch_GameUpdate { static void Postfix() { Runner.Ensure(); } }

    public class Runner : MonoBehaviour
    {
        static Runner _inst;
        public static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("KingdomBotRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _inst = go.AddComponent<Runner>();
        }
        void Update()
        {
            if (!Application.runInBackground) Application.runInBackground = true;
            try { Plugin.Instance.Tick(); } catch (Exception e) { Plugin.Log("Tick error: " + e); }
        }
        void OnGUI() { Plugin.Instance.DrawGUI(); }
    }

    /// <summary>Low-level input fed into Player each frame (same as Classic).</summary>
    public class Controller
    {
        public int Dir; public bool Sprint, Pay;
        bool _payPrev, _sprintPrev;
        public void Reset() { Dir = 0; Sprint = false; Pay = false; }
        public void Apply(Player p)
        {
            bool payDown = Pay && !_payPrev, sprintStart = Sprint && !_sprintPrev, sprintStop = !Sprint && _sprintPrev;
            Acc.Call(p, "UpdateActionState", Dir, sprintStart, sprintStop);
            Acc.Call(p, "UpdatePayState", Pay, payDown);
            _payPrev = Pay; _sprintPrev = Sprint;
        }
    }

    // New Lands implements the input as an explicit interface method -> patch it by name
    [HarmonyPatch(typeof(Player), "IControllable.ReceiveInput")]
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
        public static int Coins { get { return Player.coins; } }
        public static int Day { get { return Managers.kingdom != null ? Managers.kingdom.day : -1; } }
        public static float Hour { get { return Managers.director != null ? Managers.director.currentTime : -1f; } }
    }

    /// <summary>Command queue (same commands as Classic). Pay positions use Payable.PlayerPayPoint().</summary>
    public class CommandQueue
    {
        readonly Queue<string> _q = new Queue<string>();
        string _cur; float _t0, _u0; bool _started; int _lastCoinIdx = -1, _pressFrames;
        public bool Idle { get { return _cur == null && _q.Count == 0; } }
        public bool Positioning { get { return _cur != null && _cur.StartsWith("payid") && !_started; } }
        public void Clear() { _q.Clear(); _cur = null; }
        public void Enqueue(string c) { _q.Enqueue(c); }
        public string Describe() { return (_cur ?? "-") + (_q.Count > 0 ? " (+" + _q.Count + ")" : ""); }
        static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

        public void Tick(Plugin pl)
        {
            var c = pl.Ctl;
            if (_cur == null)
            {
                if (_q.Count == 0) { c.Reset(); return; }
                _cur = _q.Dequeue(); _t0 = Time.time; _u0 = Time.unscaledTime; _started = false; _lastCoinIdx = -1; _pressFrames = 0;
            }
            var parts = _cur.Split(' ');
            var p = GameState.Player;
            float px = GameState.PX;
            bool done = false;
            switch (parts[0])
            {
                case "goto":
                case "walkto":
                {
                    float x = F(parts[1]);
                    float tol = Mathf.Max(parts.Length > 2 ? F(parts[2]) : 0.4f, 0.08f * Time.timeScale);
                    float dx = x - px; c.Pay = false;
                    if (Mathf.Abs(dx) <= tol) { c.Dir = 0; c.Sprint = false; done = true; break; }
                    c.Dir = dx > 0 ? 1 : -1;
                    c.Sprint = parts[0] == "goto" && pl.Brain.MaySprint && Mathf.Abs(dx) > 6f;
                    if (Time.time - _t0 > (parts.Length > 3 ? F(parts[3]) : 90f)) { c.Dir = 0; c.Sprint = false; done = true; }
                    break;
                }
                case "payid":
                {
                    c.Dir = 0; c.Sprint = false;
                    int id = int.Parse(parts[1]);
                    var sel = p.selectedPayable;
                    int ps = Acc.GetInt(p, "_payState");
                    if (!_started)
                    {
                        if (sel != null && sel.GetInstanceID() == id) { _started = true; _t0 = Time.time; c.Pay = true; }
                        else if (Time.time - _t0 > 4f) { Plugin.Log("payid: target not selected (" + (sel != null ? sel.name : "none") + ")"); done = true; }
                        else if (parts.Length > 2)
                        {
                            float dx = F(parts[2]) - px;
                            if (Mathf.Abs(dx) > 0.05f) c.Dir = dx > 0 ? 1 : -1;
                            if ((Time.frameCount & 1) == 1) c.Dir = 0;
                        }
                        break;
                    }
                    if (sel != null && sel.GetInstanceID() != id && ps != 3) { c.Pay = false; done = true; break; }
                    if (sel == null && Time.time - _t0 > 0.6f && ps != 3) { c.Pay = false; done = true; break; }
                    if (ps == 3 || (ps == 0 && Time.time - _t0 > 1f) || Time.time - _t0 > 12f) { c.Pay = false; done = true; }
                    break;
                }
                case "tap":
                {
                    c.Dir = 0; c.Sprint = false;
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                    int coinIdx = (int)((Time.time - _t0) / 0.4f);
                    if (coinIdx >= count) { c.Pay = false; done = true; break; }
                    // New Lands selects the CLOSEST payable, so a short pay-tap usually hits a tree/wall instead of dropping a coin.
                    // Drop the coin the way the game's own short tap does (Game.ShortTap -> Player.TryDropCoin).
                    c.Pay = false;
                    if (coinIdx != _lastCoinIdx) { _lastCoinIdx = coinIdx; try { p.TryDropCoin(); } catch (Exception e) { Plugin.Log("TryDropCoin failed: " + e.Message); } }
                    break;
                }
                case "wait":
                    c.Reset();
                    if (Time.time - _t0 >= F(parts[1]) || Time.unscaledTime - _u0 >= F(parts[1])) done = true;
                    break;
                default:
                    Plugin.Log("Unknown command: " + _cur); done = true; break;
            }
            if (done) { c.Pay = false; _cur = null; }
        }
    }
}
