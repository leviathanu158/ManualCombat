// Manual Combat for MeitouClient. GPLv3 or later (see LICENSE).
//
// The test channel: lets a test script press the mod's controls, so the mod can be tested through Meitou's agent channel (which gives orders and
// clicks, but cannot flick the mouse or hold the fight buttons the way a player does). Off for players: it runs only when the environment variable
// MANUALCOMBAT_TEST_DIR names a folder (the test start script sets it). The script writes command lines to <dir>\mc.cmd; each control tick the mod
// reads and deletes that file, presses what it says, and appends one result line per command to <dir>\mc.out.
//
// Commands (one per line):
//   aim <overhead|left|right|thrust>   the attack direction, as a mouse flick would set it (held until changed)
//   attack [direction]                 a left click (a swing; with the crossbow out, a shot along the crosshair)
//   attack hold [direction] | release  left click held down (hold to ready) / let go
//   block on [direction] | block off   right click held / let go
//   dodge | target | swap | leave     Mouse 4, Mouse 5, X, U
//   reload [on] | reload off           the crossbow reload held / let go (left click on an empty crossbow, Mouse 4 out of fights)
//   report                             the manual combat state, one line
// Presses from the channel count as live even while the game is paused, so a test can pause and step the game tick by tick (`step <n>` in the agent
// channel) to time parries, chambers and combos exactly.

using System.Numerics;
using Meitou.Simulation;

namespace ManualCombat;

public static partial class InputWatch
{
    /// <summary>The aim a test set (Dir.None: the mouse's).</summary>
    static int s_testAim;

    internal static int TestAim => s_testAim;

    internal static class TestChannel
    {
        static readonly string? s_dir = Environment.GetEnvironmentVariable("MANUALCOMBAT_TEST_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
        static bool s_told;

        /// <summary>The test channel is on (test runs only): the mod's detail lines go to log.txt whatever the Log details setting says.</summary>
        internal static bool On => s_dir is not null;

        public static void Poll()
        {
            if (s_dir is null) return;
            if (!s_told)
            {
                s_told = true;
                ManualCombatMod.Log($"test channel on: reading {Path.Combine(s_dir, "mc.cmd")}");
            }
            string cmd = Path.Combine(s_dir, "mc.cmd");
            if (!File.Exists(cmd)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(cmd);
                File.Delete(cmd);
            }
            catch (IOException)
            {
                return;   // still being written: next tick
            }
            var results = new List<string>();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string result;
                try
                {
                    result = Run(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception e)
                {
                    result = "error: " + e.Message;
                }
                results.Add($"{s_ctx.Tick} {line} -> {result}");
                if (!line.StartsWith("report", StringComparison.OrdinalIgnoreCase)) ManualCombatMod.Log($"test: {line} -> {result}");   // reports: mc.out only
            }
            try
            {
                File.AppendAllLines(Path.Combine(s_dir, "mc.out"), results);
            }
            catch (IOException)
            {
            }
        }

        static int Direction(string word) => word.ToLowerInvariant() switch
        {
            "overhead" or "up" => Dir.Overhead,
            "left" => Dir.Left,
            "right" => Dir.Right,
            "thrust" or "down" => Dir.Thrust,
            _ => throw new ArgumentException($"unknown direction '{word}' (overhead, left, right, thrust)"),
        };

        static string Run(string[] a)
        {
            string verb = a[0].ToLowerInvariant();
            if (verb == "report") return Report();
            if (verb == "aim")
            {
                s_testAim = a.Length > 1 ? Direction(a[1]) : Dir.None;
                return "ok, aim " + Dir.Name(s_testAim);
            }
            if (!Active) return "refused: manual combat is not active (WASD off, or turned off with O)";
            if (s_controlled.IsNone) return "refused: no character under manual combat (select one)";
            var who = s_controlled;
            long tick = s_ctx.Tick;
            switch (verb)
            {
                case "move":
                {
                    // W A S D as the footwork sends them: camera-relative (forward = away from the camera), "off" to stand.
                    var forward = CameraForward();
                    var side = new Vector2(-forward.Y, forward.X);
                    string way = a.Length > 1 ? a[1].ToLowerInvariant() : "off";
                    var want = way switch
                    {
                        "forward" or "w" => forward,
                        "back" or "s" => -forward,
                        "left" or "a" => -side,
                        "right" or "d" => side,
                        _ => Vector2.Zero,
                    };
                    Send(new ManualMove(who, want) { Tick = tick });
                    return "ok, move " + way;
                }
                case "reaim":
                {
                    int dir = Direction(a.Length > 1 ? a[1] : "");
                    s_testAim = dir;
                    Send(new ManualAttackAim(who, dir) { Tick = tick });
                    return "ok, held attack aimed " + Dir.Name(dir);
                }
                case "release":
                    Send(new ManualAttackRelease(who) { Tick = tick });
                    return "ok, attack let go";
                case "attack" when a.Length > 1 && a[1].ToLowerInvariant() == "hold":
                {
                    int dir = a.Length > 2 ? Direction(a[2]) : AimNow;
                    Send(new ManualAttack(who, dir, CameraForward()) { Tick = tick, Held = true });
                    return "ok, attack held " + Dir.Name(dir);
                }
                case "attack":
                {
                    int dir = a.Length > 1 ? Direction(a[1]) : AimNow;
                    if (Hud.Ranged && CrosshairRay() is { } ray)
                    {
                        Send(new ManualShot(who, ray.Origin, ray.Direction) { Tick = tick });
                        return "ok, shot along the crosshair";
                    }
                    Send(new ManualAttack(who, dir, CameraForward()) { Tick = tick });
                    return "ok, attack " + Dir.Name(dir);
                }
                case "block":
                {
                    bool on = a.Length < 2 || a[1].ToLowerInvariant() != "off";
                    int dir = on && a.Length > 2 ? Direction(a[2]) : AimNow;
                    Send(new ManualBlock(who, on, dir) { Tick = tick });
                    s_blockSent = on;
                    // Held as the mouse button holds it, until "block off": marked as the block KEY, the next control tick would see that key up
                    // and let go at once.
                    s_blockByMouse = on;
                    s_blockSentDir = dir;
                    return on ? "ok, block held " + Dir.Name(dir) : "ok, block let go";
                }
                case "dodge":
                    Send(new ManualDodge(who) { Tick = tick });
                    return "ok";
                case "target":
                    Send(new ManualTarget(who, CameraForward()) { Tick = tick });
                    return "ok";
                case "swap":
                    Send(new ManualSwap(who) { Tick = tick });
                    return "ok";
                case "reload":
                {
                    bool on = a.Length < 2 || a[1].ToLowerInvariant() != "off";
                    Send(new ManualReload(who, on) { Tick = tick });
                    return on ? "ok, reload held" : "ok, reload let go";
                }
                case "leave":
                    if (s_blockSent) Send(new ManualBlock(who, false, AimNow) { Tick = tick });
                    s_blockSent = false;
                    Send(new ManualLeave(who) { Tick = tick });
                    return "ok";
                default:
                    return $"unknown command '{verb}' (aim, attack [hold], release, block, dodge, target, swap, reload, leave, report)";
            }
        }

        static string Report()
        {
            string head = $"active {(Active ? "yes" : "no")}, wasd {(WasdOn ? "on" : "off")}, manual {(Enabled ? "on" : "off")}, "
                + $"character {(s_controlled.IsNone ? "none" : s_controlled.ToString())}, in fight {(Hud.InFight ? "yes" : "no")}, aim {Dir.Name(AimNow)}";
            return ManualCombatSystem.Last is { } mc ? head + "; " + mc.TestReport() : head + "; no system yet";
        }
    }
}
