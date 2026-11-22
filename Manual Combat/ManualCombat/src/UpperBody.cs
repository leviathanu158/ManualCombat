// Manual Combat for Meitou. GPLv3 or later.
// Walking while holding a swing: the held swing plays on the upper body only, over the legs' fight walk, as Kenshi plays a block (its lower-body
// tracks deleted, docs/animation.md "Startup preprocessing"). Meitou plays every attack on the whole body (the swing's clip moves the legs and the
// character), so the mod adds an upper-body copy of each humanoid attack to the world's animation library:
//   - AnimationSystem's constructor (prefix; the library is built before code mods start, the systems after): a copy of each whole-body humanoid
//     technique, area upper, no root motion, named "<name> [upper]", appended to the library's list in place (no other index moves; the game's
//     tables sized by the library's count read it with bounds checks, and each character's own table is sized on its first animation).
//   - AnimationMask.Find (prefix): the copy's track mask, the original's with the below-waist bones deleted (the renderer and the clip check
//     find a layer's clip and mask by name).
//   - AnimationSystem.TechniqueOf (CombatPatches.TechniqueOfPostfix): the copy shows while ManualCombatSystem.UpperSwing (the held swing, W A S D
//     pushing); let go, the whole-body swing takes over from the same point and its own root motion plays.
// Attacks from animation mods are copied the same way (whatever techniques are loaded). Without these patches (a Meitou that changed them) the walk
// while holding is simply off: the held swing stands still, as before.
using System.Runtime.CompilerServices;
using HarmonyLib;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;

namespace ManualCombat;

public static class UpperBody
{
    /// <summary>The name suffix (and id suffix) of the upper-body copies.</summary>
    public const string Suffix = " [upper]";

    /// <summary>The below-waist bones Kenshi deletes from a block (AnimationMask's "delete below waist" set).</summary>
    static readonly string[] BelowWaist =
    [
        "Bip01", "Bip01 Pelvis", "Bip01 R Thigh", "Bip01 L Thigh", "Bip01 R Calf", "Bip01 L Calf",
        "Bip01 R Foot", "Bip01 L Foot", "Bip01 R Toe0", "Bip01 L Toe0",
    ];

    /// <summary>Per library: the copy's index for each definition index (-1: none).</summary>
    static readonly ConditionalWeakTable<AnimationLibrary, int[]> s_copies = new();

    /// <summary>The patches are in and the world's library has its copies: walking while holding can show the upper-body swing.</summary>
    public static bool Ready { get; private set; }


    static AccessTools.FieldRef<AnimationLibrary, List<AnimationDefinition>>? s_all;

    public static string Patch(Harmony h)
    {
        // The world builds its animation library before code mods start; the animation system is made after. So the copies are added to the
        // library (its definition list, in place, at the end) as the animation system is made, before any character has animated in that world.
        var system = AccessTools.Constructor(typeof(Meitou.Simulation.Movement.AnimationSystem),
            [typeof(AnimationLibrary), typeof(AnimationLengths), typeof(float), typeof(Meitou.Simulation.Combat.CombatSystem)]);
        var find = AccessTools.Method(typeof(AnimationMask), nameof(AnimationMask.Find), [typeof(GameDatabase), typeof(string)]);
        var all = AccessTools.Field(typeof(AnimationLibrary), "_all");
        if (system is null || find is null || find.ReturnType != typeof(AnimationMask) || all?.FieldType != typeof(List<AnimationDefinition>))
            return "walking while holding a swing unavailable in this Meitou version (AnimationSystem / AnimationLibrary / AnimationMask.Find changed)";
        s_all = AccessTools.FieldRefAccess<AnimationLibrary, List<AnimationDefinition>>(all);
        h.Patch(system, prefix: new HarmonyMethod(AccessTools.Method(typeof(UpperBody), nameof(SystemPrefix))));
        h.Patch(find, prefix: new HarmonyMethod(AccessTools.Method(typeof(UpperBody), nameof(FindPrefix))));
        string hands = PatchHands(h);
        return "patched AnimationSystem and AnimationMask.Find (upper-body swings: walking while holding a swing); " + hands;
    }

    // The crossbow in hand while it is reloaded out of a fight (Kenshi carries it on the back there). Only what is drawn: the published snapshot's
    // "weapon drawn" and "ranged drawn" (the renderer's hand hold), never the world's own drawn weapon, so nothing of the game changes.
    static Action<Meitou.Simulation.CharacterSnapshot, bool>? s_setDrawn, s_setRanged;

    static string PatchHands(Harmony h)
    {
        var fill = AccessTools.Method(typeof(Meitou.Simulation.World), "FillIdentity");
        var drawn = AccessTools.PropertySetter(typeof(Meitou.Simulation.CharacterSnapshot), "WeaponDrawn");
        var ranged = AccessTools.PropertySetter(typeof(Meitou.Simulation.CharacterSnapshot), "RangedDrawn");
        if (fill is null || drawn is null || ranged is null || !fill.GetParameters().Any(p => p.Name == "i"))
            return "the crossbow in hand for a reload out of fights unavailable in this Meitou version (World.FillIdentity)";
        s_setDrawn = drawn.CreateDelegate<Action<Meitou.Simulation.CharacterSnapshot, bool>>();
        s_setRanged = ranged.CreateDelegate<Action<Meitou.Simulation.CharacterSnapshot, bool>>();
        h.Patch(fill, postfix: new HarmonyMethod(AccessTools.Method(typeof(UpperBody), nameof(FillIdentityPostfix))));
        return "patched World.FillIdentity (the crossbow in hand for a reload out of fights)";
    }

    static void FillIdentityPostfix(Meitou.Simulation.CharacterSnapshot s, int i)
    {
        try
        {
            if (ManualCombatSystem.Last is not { } mc || i != mc.Slot || mc.OutOfFightReload < 0) return;
            s_setDrawn!(s, true);
            s_setRanged!(s, true);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("World.FillIdentity postfix", e);
        }
    }

    /// <summary>
    /// A whole-body humanoid attack (a technique), or a crossbow pose (the RANGED records: aiming, the shot, the reloads the crossbows name, played by
    /// the combat system's timing): those get an upper-body copy.
    /// </summary>
    static bool IsCopyable(AnimationDefinition d) =>
        (d.Technique || d.Category == AnimationLibrary.RangedCategory) && d.Area == AnimationArea.All && d.Category != AnimationLibrary.AnimalCategory
        && d.Clip.Length > 0 && !d.Name.EndsWith(Suffix);

    /// <summary>The animation system is being made with <paramref name="library"/>: its upper-body copies are added (once per library).</summary>
    static void SystemPrefix(AnimationLibrary library)
    {
        try
        {
            if (library is null || s_copies.TryGetValue(library, out _)) return;
            var list = s_all!(library);
            int count = list.Count;
            var map = new int[count];
            Array.Fill(map, -1);
            for (int i = 0; i < count; i++)
            {
                var d = list[i];
                if (!IsCopyable(d)) continue;
                map[i] = list.Count;
                list.Add(d with { Id = d.Id + Suffix, Name = d.Name + Suffix, Area = AnimationArea.Upper, Relocates = false });
            }
            s_copies.AddOrUpdate(library, map);
            int copies = list.Count - count;
            if (copies > 0) Ready = true;
            ManualCombatMod.Log($"upper-body copies: {copies} attack and crossbow animations (walking while holding a swing, aiming or reloading)");
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("AnimationSystem prefix (upper-body swings)", e);
        }
    }

    /// <summary>The upper-body copy of definition <paramref name="index"/> in <paramref name="library"/>, or -1.</summary>
    public static int CopyOf(AnimationLibrary library, int index) =>
        s_copies.TryGetValue(library, out var map) && (uint)index < (uint)map.Length ? map[index] : -1;

    /// <summary>A copy's track mask: the original's, on the upper body, with the below-waist bones deleted and no root motion.</summary>
    static bool FindPrefix(GameDatabase? db, string name, ref AnimationMask __result)
    {
        if (name is null || !name.EndsWith(Suffix, StringComparison.Ordinal)) return true;
        try
        {
            var original = AnimationMask.Find(db, name[..^Suffix.Length]);
            var deleted = new HashSet<string>(original.DeletedBones, StringComparer.Ordinal);
            deleted.UnionWith(BelowWaist);
            __result = original with { RecordName = name, Layer = "upper", DeletedBones = deleted, Relocates = false };
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("AnimationMask.Find prefix (upper-body swings)", e);
            return true;
        }
    }
}
