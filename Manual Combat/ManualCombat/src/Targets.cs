// Manual Combat for Meitou. GPLv3 or later.
// Choosing the hand-fought character's target: the target button (the enemy nearest the camera's aim) and the auto-retarget (an enemy in reach
// in front, when the target is out of reach). Only enemies count: someone fighting the character or one of its faction, or someone its faction
// fights, so a press never turns it on a bystander. Runs in the serial Schedule step, from the last finished tick's fight states.
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace ManualCombat;

public static class Targets
{
    /// <summary>How far the target button looks (world units; a melee reach is about 20 to 40).</summary>
    const float MaxRange = 80, MaxHeight = 20;
    /// <summary>The widest angle off the facing an auto-retarget takes (degrees).</summary>
    public const float ReachCone = 75;
    /// <summary>How near the crosshair an enemy must be for an attack to turn on it (degrees off the camera's aim).</summary>
    public const float CrosshairCone = 35;

    /// <summary>CombatTuning.Gap (internal): the gap a swing is chosen by.</summary>
    static MethodInfo? s_gap;

    public static string? Resolve()
    {
        s_gap = AccessTools.Method(typeof(CombatTuning), "Gap");
        return s_gap is null ? "CombatTuning.Gap" : null;
    }

    /// <summary>The flat facing of a character with <paramref name="yaw"/> (characters face (sin, cos) of their yaw).</summary>
    public static Vector2 Facing(float yaw) => new(MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>The gap from <paramref name="me"/> to <paramref name="t"/> as a swing is chosen by.</summary>
    static float Gap(World world, int me, int t)
    {
        var table = world.Characters;
        var prev = table.Previous;
        return (float)s_gap!.Invoke(null, [prev[me].Position, prev[t].Position, table.Cold(me), table.Cold(t)])!;
    }

    /// <summary>The longest reach the fighter's swings had at its last choice (a sensible default before the first).</summary>
    static float Reach(in CombatSlot s) => s.Reach > 0 ? s.Reach : 25;

    /// <summary>Whether target <paramref name="t"/> is alive and within the fighter's reach.</summary>
    public static bool InReach(World world, int me, in CombatSlot s, int t)
    {
        var prev = world.Characters.Previous;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive) return false;
        return Gap(world, me, t) <= Reach(in s);
    }

    /// <summary>
    /// The enemy to turn on: nearest the <paramref name="look"/> direction (the camera's aim, or the facing), <paramref name="exclude"/> left out;
    /// with <paramref name="reachOnly"/> only one within reach and within <paramref name="cone"/> degrees of the look, the nearest. Null when there
    /// is none.
    /// </summary>
    public static int? Pick(World world, CombatSystem combat, int me, Vector2 look, int exclude, bool reachOnly, float cone = ReachCone)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var read = CombatPatches.ReadSlots(combat);
        var meCold = table.Cold(me);
        if (meCold is null || (uint)me >= (uint)read.Length) return null;
        if (look.LengthSquared() < 1e-6f) look = Facing(prev[me].Yaw);
        look = Vector2.Normalize(look);
        int faction = meCold.Faction;
        float reach = Reach(in read[me]);

        // Those the character's own side is fighting.
        var foughtByUs = new HashSet<int>();
        for (int k = 0; k < prev.Length && k < read.Length; k++)
            if (prev[k].Alive && read[k].TargetSlot >= 0 && table.Cold(k) is { } kc && kc.Faction == faction) foughtByUs.Add(read[k].TargetSlot);

        int best = -1;
        float bestScore = float.PositiveInfinity;
        var at = prev[me].Position;
        for (int c = 0; c < prev.Length && c < read.Length; c++)
        {
            if (c == me || c == exclude || !prev[c].Alive || read[c].Down) continue;
            var d = prev[c].Position - at;
            float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            if (flat > MaxRange || MathF.Abs(d.Y) > MaxHeight || flat < 1e-3f) continue;
            var cc = table.Cold(c);
            if (cc is not { Medical.Incapacitated: false, IsImprisoned: false } || !cc.CarriedBy.IsNone) continue;

            int theirs = read[c].TargetSlot;
            bool enemy = theirs == me || foughtByUs.Contains(c)
                || (uint)theirs < (uint)prev.Length && table.Cold(theirs) is { } victim && victim.Faction == faction && cc.Faction != faction;
            if (!enemy) continue;

            float angle = MathF.Acos(Math.Clamp(Vector2.Dot(look, new Vector2(d.X, d.Z) / flat), -1, 1)) * (180 / MathF.PI);
            float score;
            if (reachOnly)
            {
                if (angle > cone) continue;
                float gap = Gap(world, me, c);
                if (gap > reach) continue;
                score = gap + angle * 0.2f;
            }
            else score = angle + flat * 0.25f;   // the aim first; distance breaks near ties

            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best >= 0 ? best : null;
    }
}
