// Manual Combat for Meitou. GPLv3 or later.
// Attack and block on command, Mount & Blade style: the attack button swings the way you hold A, D, W or S; the block button raises a guard,
// and a block pressed just before the swing lands is a parry that staggers the attacker. Made to sit beside PZA's WASD & Controller Support,
// but works without it (it acts on the first selected character).
using HarmonyLib;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace ManualCombat;

public sealed class ManualCombatMod : IMod
{
    static ModContext? s_ctx;

    /// <summary>The player's settings (the page writes them; the world copies them each tick).</summary>
    public static ManualCombatSettings Settings { get; } = new();

    public void Initialize(ModContext c)
    {
        s_ctx = c;

        var missing = CombatPatches.Resolve();
        if (missing.Count > 0)
        {
            // A Meitou that changed the combat code this mod reaches into: stay off rather than break fights.
            c.LogError("turned OFF: this Meitou version changed combat code the mod needs. Missing: " + string.Join(", ", missing));
            return;
        }

        c.AddSettings(Settings);
        InputWatch.Register(c);
        c.AddSystem("manual-combat", ctx => ctx.Find<CombatSystem>() is { } combat
                ? new ManualCombatSystem(combat)
                : throw new InvalidOperationException("no combat system in this world"),
            SystemAnchor.AfterPart(StandardParts.Combat));

        // The guard shown at once while block is held (an animation only; the real block takes over when a swing comes).
        c.AddCombatMove((world, id, tick) => ManualCombatSystem.For(world)?.Moves(world, id, tick));

        foreach (string line in CombatPatches.Apply(c.Harmony)) c.Log(line);
        foreach (string line in InputWatch.Patch(c.Harmony)) c.Log(line);
        c.Log(FightHold.Patch(c.Harmony));
        c.Log(Hud.Patch(c.Harmony));
        c.Log("ready: in a fight your selected character attacks with left click and blocks with right click, aimed by flicking the mouse; "
            + "mouse 4 dodges, mouse 5 switches target, W A S D step, U leaves the fight, O turns manual combat off/on");
    }

    static readonly System.Collections.Concurrent.ConcurrentQueue<string> s_notices = new();

    /// <summary>Queues a line for the game's message panel (any thread); shown on the interface's thread by <see cref="InputWatch"/>.</summary>
    public static void Notify(string text)
    {
        if (Settings.Messages && s_notices.Count < 8) s_notices.Enqueue(text);
    }

    /// <summary>The next queued message, or null.</summary>
    public static string? NextNotice() => s_notices.TryDequeue(out var text) ? text : null;

    public static void Log(string line)
    {
        if (s_ctx is { } c) c.Log(line);
        else Console.WriteLine($"mod       manualcombat: {line}");
    }

    static long s_lastErrorTick = long.MinValue;

    /// <summary>A patch's failure: logged (at most about once a second) and never passed to the game.</summary>
    public static void LogException(string where, Exception e)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastErrorTick) < 1000) return;
        Interlocked.Exchange(ref s_lastErrorTick, now);
        string line = $"{where} failed: {e}";
        if (s_ctx is { } c) c.LogError(line);
        else Console.Error.WriteLine($"mod       manualcombat: {line}");
    }
}
