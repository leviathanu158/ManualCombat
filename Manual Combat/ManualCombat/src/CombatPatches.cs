// Manual Combat for Meitou. GPLv3 or later.
// The Harmony patches on Meitou's combat code (src/Meitou.Simulation/Combat, read at commit 802c1ca, 0.22.2-nightly.20261121). Every
// non-public member is looked up once by name; if one is missing (a newer Meitou changed it) the mod turns itself off with a log line naming
// it, and the game plays as usual. Each patch only changes anything for the one hand-fought character (ManualCombatSystem.Controls).
//
// What each patch does:
//   StartAttack (prefix)   the hand-fought character starts a swing only when the player asked for one (no swinging on its own)
//   ChooseAttack (prefix)  ...and it is a swing in the direction the player held, when the weapon has one
//   ChooseDefence (prefix) it blocks only while the block button is held, with the guard the player holds (or any guard on easy blocking);
//                          Kenshi's block roll still applies, more generous (ManualCombatSystem.BlockHolds); a parry press always holds
//   StartDefence (postfix) a block raised late still counts if it is up when the blow lands (as Meitou's "Late dodges and blocks" option)
//   ApplyBlocked (postfix) a block pressed within the parry window staggers the attacker
//   HasInitiative (postfix) "Skip the initiative roll": the player's press is not lost to the dice
//   CombatSteering.Velocity (postfix) W A S D footwork in fights
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace ManualCombat;

public static class CombatPatches
{
    static AccessTools.FieldRef<CombatSystem, CombatSlot[]> s_write = null!;
    static AccessTools.FieldRef<CombatSystem, CombatSlot[]> s_read = null!;
    static AccessTools.FieldRef<CombatSystem, CombatTechnique[]> s_techniques = null!;

    static MethodInfo s_startAttack = null!, s_chooseAttack = null!, s_chooseDefence = null!, s_startDefence = null!, s_applyBlocked = null!;
    /// <summary>CombatSystem.HasInitiative: the dice roll between two fighters for who may swing.</summary>
    static MethodInfo s_hasInitiative = null!;
    /// <summary>CombatSteering.Velocity: a fighter's footwork velocity each tick (the fight's own movement, no move order).</summary>
    static MethodInfo s_velocity = null!;
    /// <summary>PursuitSystem.Steer: hands a fighter to the fight steering (melee only: a crossbowman stands to aim and reload).</summary>
    static MethodInfo s_steer = null!;
    /// <summary>CombatSystem.LoadedKind: the ammunition a finished reload loads (the reload out of fights).</summary>
    static MethodInfo s_loadedKind = null!;
    /// <summary>ItemUseObservers.Report (internal, optional): other mods hear a bolt used up by the reload out of fights.</summary>
    static MethodInfo? s_reportUse;
    /// <summary>CombatSystem.MayJoin / HasSlot: the limits on how many fighters may close in on, and swing at, one target.</summary>
    static MethodInfo s_mayJoin = null!, s_hasSlot = null!;
    /// <summary>CombatSystem.ReactionEnds: a block or dodge ends once no swing is coming (a dodge with none coming is kept to its end).</summary>
    static MethodInfo s_reactionEnds = null!;
    /// <summary>The crossbow's steps: Aim (fires once steady), Reload, ImmediateThreat (melee instead), ChooseMode; MeleeDrawn (the weapon drawn).</summary>
    static MethodInfo s_aim = null!, s_reload = null!, s_threat = null!, s_chooseMode = null!, s_meleeDrawn = null!;
    /// <summary>Crosshair shots: the effective stat (the spread), the shot itself (the bolt it adds is turned to the crosshair), the line of fire.</summary>
    static MethodInfo s_eff = null!, s_applyFire = null!, s_lineOfFire = null!;
    static FieldInfo s_flight = null!, s_flightList = null!, s_pShooter = null!, s_pTarget = null!, s_pId = null!, s_pPosition = null!, s_pDirection = null!;
    /// <summary>CombatSystem.MostAdvanced / BlowSide (the swing coming at a fighter, and its side), Ticks / PlayRate (a move's length).</summary>
    static MethodInfo s_mostAdvanced = null!, s_blowSide = null!, s_ticks = null!, s_playRate = null!;
    /// <summary>CombatSystem.MovesCategory(Fighter, int): the weapon category whose moves a fighter uses.</summary>
    static MethodInfo s_movesCategory = null!;
    /// <summary>CombatSystem.Stagger(World, ref CombatSlot, int, CharacterCold, bool, int, ulong, bool): a hit reaction.</summary>
    static MethodInfo s_stagger = null!;
    /// <summary>TechniqueChooser.Fits(CombatTechnique, int): whether a technique belongs to a weapon category.</summary>
    static MethodInfo s_fits = null!;
    /// <summary>The private CombatSystem.IncomingBlow record struct's Attacker, Seq, Blow and Lands.</summary>
    static PropertyInfo s_blowAttacker = null!, s_blowSeq = null!, s_blowBlow = null!, s_blowLands = null!;
    /// <summary>Kenshi's block chance, as CombatSystem.ChooseDefence works it out: Trained, IndoorsMod, Guarding and the combat constants.</summary>
    static MethodInfo s_trained = null!, s_indoorsMod = null!, s_guarding = null!;
    static FieldInfo s_combatConstants = null!;
    /// <summary>Where the IncomingBlow argument sits in ChooseDefence and StartDefence.</summary>
    static int s_chooseDefenceSwing, s_startDefenceSwing;

    /// <summary>The direction the hand-fought character's current swing should take (0: none asked), for ChooseAttack on this thread.</summary>
    [ThreadStatic] static int t_wantDir;
    /// <summary>Set when StartAttack ran for a player's request on this thread (the postfix logs what came of it).</summary>
    [ThreadStatic] static bool t_asked;
    /// <summary>Set when the asked side has a swing but none reaches from here (the request waits; the character steps in).</summary>
    [ThreadStatic] static bool t_outOfReach;
    /// <summary>"Swing even out of reach" for the request being started on this thread.</summary>
    [ThreadStatic] static bool t_swingAnyway;
    /// <summary>The weapon had no swing of the asked side (another side's played).</summary>
    [ThreadStatic] static bool t_noSide;
    /// <summary>Set while ChooseAttack is being asked again with a filtered list (the prefix must not run on itself).</summary>
    [ThreadStatic] static bool t_filtering;

    static readonly ConditionalWeakTable<IReadOnlyList<CombatTechnique>, CombatTechnique[]?[]> s_filtered = new();
    static readonly object s_filterLock = new();

    /// <summary>Looks everything up; returns what is missing (empty: all there).</summary>
    public static List<string> Resolve()
    {
        var missing = new List<string>();
        var cs = typeof(CombatSystem);

        Field(ref s_write, "_write", missing);
        Field(ref s_read, "_read", missing);
        if (AccessTools.Field(cs, "_techniques") is { } t && t.FieldType == typeof(CombatTechnique[]))
            s_techniques = AccessTools.FieldRefAccess<CombatSystem, CombatTechnique[]>("_techniques");
        else missing.Add("field CombatSystem._techniques");

        s_startAttack = Need(AccessTools.Method(cs, "StartAttack"), "CombatSystem.StartAttack", missing);
        s_chooseDefence = Need(AccessTools.Method(cs, "ChooseDefence"), "CombatSystem.ChooseDefence", missing);
        s_startDefence = Need(AccessTools.Method(cs, "StartDefence"), "CombatSystem.StartDefence", missing);
        s_applyBlocked = Need(AccessTools.Method(cs, "ApplyBlocked"), "CombatSystem.ApplyBlocked", missing);
        s_hasInitiative = Need(AccessTools.Method(cs, "HasInitiative"), "CombatSystem.HasInitiative", missing);
        s_velocity = Need(AccessTools.Method(typeof(CombatSteering), "Velocity"), "CombatSteering.Velocity", missing);
        s_steer = Need(AccessTools.Method(typeof(PursuitSystem), "Steer"), "PursuitSystem.Steer", missing);
        s_loadedKind = Need(AccessTools.Method(cs, "LoadedKind", [typeof(CharacterCold), typeof(CrossbowInstance)]), "CombatSystem.LoadedKind", missing);
        if (AccessTools.TypeByName("Meitou.Simulation.Items.ItemUseObservers") is { } observers)
            s_reportUse = AccessTools.Method(observers, "Report") is { } report && report.GetParameters().Length == 7 ? report : null;
        s_mayJoin = Need(AccessTools.Method(cs, "MayJoin"), "CombatSystem.MayJoin", missing);
        s_hasSlot = Need(AccessTools.Method(cs, "HasSlot"), "CombatSystem.HasSlot", missing);
        s_reactionEnds = Need(AccessTools.Method(cs, "ReactionEnds"), "CombatSystem.ReactionEnds", missing);
        s_aim = Need(AccessTools.Method(cs, "Aim"), "CombatSystem.Aim", missing);
        s_reload = Need(AccessTools.Method(cs, "Reload"), "CombatSystem.Reload", missing);
        s_threat = Need(AccessTools.Method(cs, "ImmediateThreat"), "CombatSystem.ImmediateThreat", missing);
        s_chooseMode = Need(AccessTools.Method(cs, "ChooseMode"), "CombatSystem.ChooseMode", missing);
        s_meleeDrawn = Need(AccessTools.Method(cs, "MeleeDrawn", [typeof(Fighter)]), "CombatSystem.MeleeDrawn", missing);
        s_eff = Need(AccessTools.Method(cs, "Eff", [typeof(CharacterCold), typeof(Meitou.Data.Gameplay.Bodies.StatsEnumerated)]), "CombatSystem.Eff", missing);
        s_applyFire = Need(AccessTools.Method(cs, "ApplyFire"), "CombatSystem.ApplyFire", missing);
        s_lineOfFire = Need(AccessTools.Method(cs, "LineOfFire"), "CombatSystem.LineOfFire", missing);
        s_flight = Need(AccessTools.Field(cs, "_projectiles"), "CombatSystem._projectiles", missing);
        if (s_flight is not null)
            s_flightList = Need(AccessTools.Field(s_flight.FieldType, "_projectiles"), "ProjectileFlight._projectiles", missing);
        var projectile = AccessTools.TypeByName("Meitou.Simulation.Combat.Projectile");
        if (projectile is null) missing.Add("Projectile");
        else
        {
            s_pShooter = Need(AccessTools.Field(projectile, "Shooter"), "Projectile.Shooter", missing);
            s_pTarget = Need(AccessTools.Field(projectile, "Target"), "Projectile.Target", missing);
            s_pId = Need(AccessTools.Field(projectile, "Id"), "Projectile.Id", missing);
            s_pPosition = Need(AccessTools.Field(projectile, "Position"), "Projectile.Position", missing);
            s_pDirection = Need(AccessTools.Field(projectile, "Direction"), "Projectile.Direction", missing);
        }
        s_mostAdvanced = Need(AccessTools.Method(cs, "MostAdvanced"), "CombatSystem.MostAdvanced", missing);
        s_blowSide = Need(AccessTools.Method(cs, "BlowSide"), "CombatSystem.BlowSide", missing);
        s_ticks = Need(AccessTools.Method(cs, "Ticks"), "CombatSystem.Ticks", missing);
        s_playRate = Need(AccessTools.Method(cs, "PlayRate"), "CombatSystem.PlayRate", missing);
        if (Targets.Resolve() is { } gone) missing.Add(gone);
        s_movesCategory = Need(AccessTools.Method(cs, "MovesCategory", [typeof(Fighter), typeof(int)]), "CombatSystem.MovesCategory", missing);
        s_stagger = Need(AccessTools.Method(cs, "Stagger"), "CombatSystem.Stagger", missing);
        s_chooseAttack = Need(AccessTools.Method(typeof(TechniqueChooser), "ChooseAttack"), "TechniqueChooser.ChooseAttack", missing);
        s_fits = Need(AccessTools.Method(typeof(TechniqueChooser), "Fits", [typeof(CombatTechnique), typeof(int)]), "TechniqueChooser.Fits", missing);
        s_trained = Need(AccessTools.Method(cs, "Trained", [typeof(CharacterCold), typeof(Meitou.Data.Gameplay.Bodies.StatsEnumerated)]),
            "CombatSystem.Trained", missing);
        s_indoorsMod = Need(AccessTools.Method(cs, "IndoorsMod", [typeof(World), typeof(int), typeof(Fighter)]), "CombatSystem.IndoorsMod", missing);
        s_guarding = Need(AccessTools.Method(cs, "Guarding", [typeof(CharacterCold)]), "CombatSystem.Guarding", missing);
        s_combatConstants = Need(AccessTools.Field(cs, "_combat"), "CombatSystem._combat", missing);
        if (s_combatConstants is not null && s_combatConstants.FieldType != typeof(CombatConstants)) missing.Add("CombatSystem._combat as CombatConstants");

        var blowType = AccessTools.Inner(cs, "IncomingBlow");
        if (blowType is null) missing.Add("type CombatSystem.IncomingBlow");
        else
        {
            s_blowAttacker = Need(AccessTools.Property(blowType, "Attacker"), "IncomingBlow.Attacker", missing);
            s_blowSeq = Need(AccessTools.Property(blowType, "Seq"), "IncomingBlow.Seq", missing);
            s_blowBlow = Need(AccessTools.Property(blowType, "Blow"), "IncomingBlow.Blow", missing);
            s_blowLands = Need(AccessTools.Property(blowType, "Lands"), "IncomingBlow.Lands", missing);
        }

        // The parameters the patches read by name: a renamed one would silently feed the patch the wrong value.
        Params(s_startAttack, missing, "world", "i", "s", "now");
        Params(s_chooseDefence, missing, "world", "i", "cold", "stumbling", "swing");
        Params(s_startDefence, missing, "world", "i", "s", "swing");
        Params(s_applyBlocked, missing, "world", "a", "d", "ac", "now");
        Params(s_hasInitiative, missing, "world", "i", "s", "t", "now");
        Params(s_velocity, missing, "world", "i", "cold");
        Params(s_steer, missing, "world", "i", "n", "cold", "st", "holding");
        if (s_velocity is not null && !s_velocity.GetParameters().Any(p => p.Name == "face" && p.IsOut)) missing.Add("CombatSteering.Velocity's out face");
        Params(s_mayJoin, missing, "world", "i");
        Params(s_hasSlot, missing, "world", "i");
        Params(s_reactionEnds, missing, "world", "i", "s", "now", "wait");
        Params(s_aim, missing, "world", "i", "s", "dt");
        Params(s_reload, missing, "i", "s");
        Params(s_threat, missing, "world", "self");
        Params(s_chooseMode, missing, "world", "i", "s", "cold");
        Params(s_applyFire, missing, "world", "e");
        Params(s_lineOfFire, missing, "world", "self");
        Params(s_stagger, missing, "world", "dSlot", "d", "dc", "heavy", "now");
        Params(s_playRate, missing, "world", "slot", "attack");
        Params(s_chooseAttack, missing, "all", "q", "roll", "maxReach");
        s_chooseDefenceSwing = IndexOf(s_chooseDefence, "swing");
        s_startDefenceSwing = IndexOf(s_startDefence, "swing");
        if (s_stagger is not null && s_stagger.GetParameters().Length != 8) missing.Add("CombatSystem.Stagger with 8 parameters");
        if (s_mostAdvanced is not null && s_mostAdvanced.GetParameters().Length != 5) missing.Add("CombatSystem.MostAdvanced with 5 parameters");
        if (s_blowSide is not null && s_blowSide.GetParameters().Length != 3) missing.Add("CombatSystem.BlowSide with 3 parameters");
        if (s_ticks is not null && s_ticks.GetParameters().Length != 4) missing.Add("CombatSystem.Ticks with 4 parameters");
        if (s_playRate is not null && s_playRate.GetParameters().Length != 5) missing.Add("CombatSystem.PlayRate with 5 parameters");
        return missing;
    }

    /// <summary>Applies all the patches or none; returns one line per patch.</summary>
    public static List<string> Apply(Harmony h)
    {
        var lines = new List<string>();
        try
        {
            h.Patch(s_startAttack, prefix: Hook(nameof(StartAttackPrefix)), postfix: Hook(nameof(StartAttackPostfix)));
            lines.Add("patched CombatSystem.StartAttack");
            h.Patch(s_chooseAttack, prefix: Hook(nameof(ChooseAttackPrefix)));
            lines.Add("patched TechniqueChooser.ChooseAttack");
            h.Patch(s_chooseDefence, prefix: Hook(nameof(ChooseDefencePrefix)), postfix: Hook(nameof(ChooseDefencePostfix)));
            lines.Add("patched CombatSystem.ChooseDefence");
            h.Patch(s_startDefence, postfix: Hook(nameof(StartDefencePostfix)));
            lines.Add("patched CombatSystem.StartDefence");
            h.Patch(s_applyBlocked, postfix: Hook(nameof(ApplyBlockedPostfix)));
            lines.Add("patched CombatSystem.ApplyBlocked");
            h.Patch(s_hasInitiative, postfix: Hook(nameof(HasInitiativePostfix)));
            lines.Add("patched CombatSystem.HasInitiative");
            h.Patch(s_velocity, prefix: Hook(nameof(VelocityPrefix)), postfix: Hook(nameof(VelocityPostfix)));
            lines.Add("patched CombatSteering.Velocity (W A S D footwork)");
            h.Patch(s_steer, postfix: Hook(nameof(SteerPostfix)));
            lines.Add("patched PursuitSystem.Steer (footwork with the crossbow out)");
            // Optional (only a look): without it the crossbow footwork slides the character in its aim pose.
            if (AccessTools.Method(typeof(Meitou.Simulation.Movement.AnimationSystem), "TechniqueOf") is { } techniqueOf
                && techniqueOf.ReturnType == typeof(ValueTuple<int, float>) && techniqueOf.GetParameters().Any(p => p.Name == "id"))
            {
                h.Patch(techniqueOf, postfix: Hook(nameof(TechniqueOfPostfix)));
                lines.Add("patched AnimationSystem.TechniqueOf (walking with the crossbow out)");
            }
            else lines.Add("walking animation with the crossbow out unavailable in this Meitou version (AnimationSystem.TechniqueOf)");
            h.Patch(s_mayJoin, postfix: Hook(nameof(GatePostfix)));
            h.Patch(s_hasSlot, postfix: Hook(nameof(GatePostfix)));
            lines.Add("patched CombatSystem.MayJoin / HasSlot (swing anytime)");
            h.Patch(s_reactionEnds, prefix: Hook(nameof(ReactionEndsPrefix)));
            lines.Add("patched CombatSystem.ReactionEnds (dodge anytime, held guard)");
            h.Patch(s_playRate, postfix: Hook(nameof(PlayRatePostfix)));
            lines.Add("patched CombatSystem.PlayRate (combos)");
            h.Patch(s_aim, prefix: Hook(nameof(AimPrefix)));
            h.Patch(s_reload, prefix: Hook(nameof(ReloadPrefix)));
            h.Patch(s_threat, prefix: Hook(nameof(ThreatPrefix)));
            h.Patch(s_chooseMode, postfix: Hook(nameof(ChooseModePostfix)));
            lines.Add("patched CombatSystem.Aim / Reload / ImmediateThreat / ChooseMode (manual crossbow, swap)");
            h.Patch(s_applyFire, prefix: Hook(nameof(ApplyFirePrefix)), postfix: Hook(nameof(ApplyFirePostfix)));
            h.Patch(s_lineOfFire, prefix: Hook(nameof(LineOfFirePrefix)));
            lines.Add("patched CombatSystem.ApplyFire / LineOfFire (shots at the crosshair)");
            h.Patch(s_stagger, prefix: Hook(nameof(StaggerPrefix)), postfix: Hook(nameof(StaggerPostfix)));
            lines.Add("patched CombatSystem.Stagger (hyper armor)");
        }
        catch
        {
            h.UnpatchAll(h.Id);
            throw;
        }
        return lines;
    }

    /// <summary>The fight states the tick writes (for ManualCombatSystem.Schedule).</summary>
    public static CombatSlot[] WriteSlots(CombatSystem combat) => s_write(combat);

    /// <summary>Whether technique <paramref name="tech"/> is a block or dodge (the guard the block button raises).</summary>
    public static bool IsGuard(CombatSystem combat, int tech)
    {
        var all = s_techniques(combat);
        return (uint)tech < (uint)all.Length && (all[tech].IsBlock || all[tech].IsDodge);
    }

    /// <summary>
    /// The block the fighter's weapon class has for <paramref name="side"/> (a Kenshi blow code), picked from the data by direction as the game
    /// picks a block, so the blocks animation mods add count; null when the class has none for that side (or its block is a dodge).
    /// </summary>
    public static CombatTechnique? GuardTechnique(CombatSystem combat, Fighter fighter, bool prone, int side)
    {
        int pick = GuardIndex(combat, fighter, prone, side);
        return pick >= 0 ? s_techniques(combat)[pick] : null;
    }

    /// <summary><see cref="GuardTechnique"/>'s index in the combat system's techniques, or -1.</summary>
    public static int GuardIndex(CombatSystem combat, Fighter fighter, bool prone, int side)
    {
        if (!fighter.CanBlock) return -1;
        var all = s_techniques(combat);
        int category = (int)s_movesCategory.Invoke(combat, [fighter, fighter.TechniqueCategory])!;
        int pick = TechniqueChooser.ChooseReaction(all, category, prone, canBlock: true, side,
            blockChancePercent: 100, dodgeChancePercent: 0, chanceRoll: 0, pickRoll: 0.5f);
        return pick >= 0 && all[pick] is { IsBlock: true, IsDodge: false } ? pick : -1;
    }

    /// <summary>Whether technique <paramref name="tech"/> is a block (not a dodge).</summary>
    public static bool IsBlock(CombatSystem combat, int tech) => Technique(combat, tech) is { IsBlock: true, IsDodge: false };

    #region Footwork

    /// <summary>
    /// W A S D footwork: in its fight footwork (Kenshi's own fight movement, which keeps the fighter facing its enemy and never ends the fight) the
    /// hand-fought character steps the way the player pushes, at the footwork speed. With no key held it stands still, or keeps the game's own
    /// footwork ("Close in by itself"). A swing roots it in place as in the game (Kenshi moves a swinging fighter by the clip alone).
    /// </summary>
    static void VelocityPostfix(World world, int i, CharacterCold cold, ref Vector2 __result)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Footwork || !mc.Controls(world, i)) return;

            float speed = mc.FootworkSpeed * (cold.Race?.CombatMoveSpeedMult ?? 1) * WalkShare(mc, i);   // slower while holding a swing or with the crossbow out
            var v = mc.MoveIntent;
            if (v == Vector2.Zero)
            {
                // An aimed swing waiting for reach: step straight in at the target until it starts.
                if (mc.SteppingIn(world.Tick) && StepInDirection(mc.Combat, world, i) is { } toward)
                {
                    __result = toward * speed;
                    return;
                }
                if (!mc.AutoFootwork) __result = Vector2.Zero;
                return;
            }
            __result = v * speed;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("CombatSteering.Velocity postfix", e);
        }
    }

    /// <summary>Per library, the reload clip of each crossbow's reload record id ("" for the default), as Meitou's AnimationSystem picks it.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Meitou.Data.Gameplay.AnimationLibrary,
        System.Collections.Concurrent.ConcurrentDictionary<string, int>> s_reloadClips = new();

    static int ReloadClip(Meitou.Data.Gameplay.AnimationLibrary library, string? reloadAnimId)
    {
        var clips = s_reloadClips.GetValue(library, _ => new());
        return clips.GetOrAdd(reloadAnimId ?? "", id =>
        {
            int k = id.Length > 0 ? library.IndexOfRecord(id) : -1;
            return k >= 0 ? k : library.IndexOf(Meitou.Data.Gameplay.AnimationLibrary.RangedReloadRecord);
        });
    }

    /// <summary>The footwork speed share: slower while holding a swing (walking readied) or with the crossbow out (aiming or reloading).</summary>
    static float WalkShare(ManualCombatSystem mc, int i)
    {
        if (mc.UpperSwing) return mc.ReadiedWalkSpeed;
        var read = s_read(mc.Combat);
        if ((uint)i >= (uint)read.Length || !read[i].Ranged) return 1;
        return read[i].Loaded <= 0 && mc.ReloadGoes ? mc.ReloadWalkSpeed : mc.CrossbowWalkSpeed;   // reloading: slowest; aiming and firing: slow
    }

    /// <summary>The movement flags of a path held or asked for (PursuitSystem's PathFlags).</summary>
    const ushort AnyPath = (ushort)Meitou.Simulation.Movement.MoveFlags.AnyPath;

    /// <summary>
    /// Footwork with the crossbow out: Meitou steers no crossbowman (it stands to aim and reload, walking a path only to close in). The hand-fought
    /// character with its crossbow out is handed to the fight steering all the same, so W A S D step it while it aims and reloads; with no key held
    /// it stands (<see cref="VelocityPrefix"/>). Not while down, staggered (a stumble holds it still, as Steer's own) or on Hold.
    /// </summary>
    static void SteerPostfix(World world, int i, ref CharacterHot n, CharacterCold cold, in CombatSlot st, bool holding, ref bool __result)
    {
        try
        {
            if (__result || st.Down || holding || cold.Steer is not { } steer) return;
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Footwork || !mc.Controls(world, i) || !(st.Ranged || mc.UpperSwing)) return;   // the crossbow out, or walking while holding a swing

            // No path chase while the keys steer: the path is dropped and the pursuit step ends here (true).
            if ((n.Flags & AnyPath) != 0)
            {
                n.Flags = (ushort)(n.Flags & ~AnyPath);
                n.PathCursor = 0;
                cold.PathRequest = 0;
            }
            __result = true;
            if (world.Tick < st.StunUntil) return;
            steer.Active = true;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("PursuitSystem.Steer postfix", e);
        }
    }

    /// <summary>
    /// The crossbow's footwork (<see cref="SteerPostfix"/>): the step the keys push, or standing still, facing the target. Meitou's own footwork (its
    /// melee bands) is not asked.
    /// </summary>
    static bool VelocityPrefix(World world, int i, CharacterCold cold, out Vector2 face, ref Vector2 __result)
    {
        face = Vector2.Zero;
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Footwork || !mc.Controls(world, i)) return true;
            var read = s_read(mc.Combat);
            if ((uint)i >= (uint)read.Length || !read[i].Ranged && !mc.UpperSwing) return true;

            __result = mc.MoveIntent * mc.FootworkSpeed * (cold.Race?.CombatMoveSpeedMult ?? 1) * WalkShare(mc, i);
            face = StepInDirection(mc.Combat, world, i) ?? Vector2.Zero;
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("CombatSteering.Velocity prefix", e);
            return true;
        }
    }

    /// <summary>
    /// Walking with the crossbow out: the crossbow's aim and reload poses are whole-body clips, which leave the legs no walk, so a character stepping
    /// with W A S D slid along in its aim pose. While the keys push, the pose is left out and the fight's own walk plays; it comes back when the
    /// character stands (a reload keeps counting meanwhile). From the simulation's own state (the footwork wanted), so it is the same everywhere.
    /// </summary>
    static void TechniqueOfPostfix(Meitou.Simulation.Movement.AnimationSystem __instance, World world, Meitou.Simulation.CharacterId id,
        ref ValueTuple<int, float> __result)
    {
        try
        {
            // The reload out of a fight (Meitou plays crossbow poses only in fights): the crossbow's own reload clip at the reload's progress, its
            // upper-body copy while walking.
            if (__result.Item1 < 0)
            {
                var me = ManualCombatSystem.For(world);
                if (me is null || me.OutOfFightReload < 0 || !me.Controls(world, id.Slot)) return;
                int clip = ReloadClip(__instance.Library, world.Characters.Cold(id.Slot)?.Fighter?.Crossbow?.Data.ReloadAnimId);
                if (clip < 0) return;
                int walking = me.MoveIntent != Vector2.Zero ? UpperBody.CopyOf(__instance.Library, clip) : -1;
                __result = (walking >= 0 ? walking : clip, me.OutOfFightReload);
                return;
            }
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Footwork || mc.MoveIntent == Vector2.Zero || !mc.Controls(world, id.Slot)) return;

            // Walking while holding a swing (UpperBody): the swing's upper-body copy, at the same point, over the legs' walk.
            if (mc.UpperSwing)
            {
                int copy = UpperBody.CopyOf(__instance.Library, __result.Item1);
                if (copy >= 0) __result = (copy, __result.Item2);
                return;
            }

            var read = s_read(mc.Combat);
            if ((uint)id.Slot >= (uint)read.Length) return;
            ref readonly var s = ref read[id.Slot];
            if (!s.Ranged || s.AttackTech >= 0 || s.ReactTech >= 0) return;   // only the aim or reload pose, never a swing, block or dodge
            // The pose's upper-body copy over the legs' walk (UpperBody); a pose already on the upper body stays; with neither, the pose is left out.
            int pose = UpperBody.CopyOf(__instance.Library, __result.Item1);
            if (pose >= 0) __result = (pose, __result.Item2);
            else if ((uint)__result.Item1 >= (uint)__instance.Library.Definitions.Count
                || __instance.Library.Definitions[__result.Item1].Area == Meitou.Data.Gameplay.AnimationArea.All) __result = (-1, 0);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("AnimationSystem.TechniqueOf postfix", e);
        }
    }

    /// <summary>
    /// A reload's end as Meitou's (CombatSystem.Reload, ApplyReload), for the reload out of fights: the crossbow holds its shots, of the kind the
    /// reload loads, and one bolt of it is taken from the inventory (other mods hear of it).
    /// </summary>
    public static void FinishReload(World world, CombatSystem combat, int slot, ref CombatSlot s, CharacterCold cold, CrossbowInstance x)
    {
        s.ReloadLeft = 0;
        s.Loaded = x.NumShots;
        s.LoadedKind = (AmmoData?)s_loadedKind.Invoke(null, [cold, x]);
        cold.CrossbowShots = s.Loaded;
        if (x.InfiniteAmmo) return;
        string ammo = s.LoadedKind?.StringId ?? x.Data.AmmoId;
        if (!Ammunition.UseOne(cold.Inventory, ammo, out bool unitGone)) return;
        if (s_reportUse is { } report && Enum.TryParse(report.GetParameters()[1].ParameterType, "Ammunition", out object? kind))
            report.Invoke(null, [world, kind, ammo, world.Characters.IdOf(slot), null, 1f, unitGone]);
    }

    /// <summary>The flat direction from fighter <paramref name="i"/> to its target, or null.</summary>
    static Vector2? StepInDirection(CombatSystem combat, World world, int i)
    {
        var read = s_read(combat);
        if ((uint)i >= (uint)read.Length) return null;
        int t = read[i].TargetSlot;
        var prev = world.Characters.Previous;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive) return null;
        var d = prev[t].Position - prev[i].Position;
        var flat = new Vector2(d.X, d.Z);
        return flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : null;
    }

    #endregion

    /// <summary>The fight states of the last finished tick.</summary>
    public static CombatSlot[] ReadSlots(CombatSystem combat) => s_read(combat);

    /// <summary>Technique <paramref name="index"/> of the combat system, or null.</summary>
    public static CombatTechnique? Technique(CombatSystem combat, int index)
    {
        var all = s_techniques(combat);
        return (uint)index < (uint)all.Length ? all[index] : null;
    }

    /// <summary>Whether technique <paramref name="tech"/> is a dodge.</summary>
    public static bool IsDodge(CombatSystem combat, int tech) => Technique(combat, tech) is { IsDodge: true };

    /// <summary>
    /// The dodge the fighter's weapon class has (the game's dodge pick, as for a fighter that cannot block), else an unarmed one (Martial Arts'
    /// dodges); from the data, so animation mods' dodges count. Null when there is none.
    /// </summary>
    public static (int Index, CombatTechnique Technique)? DodgeTechnique(CombatSystem combat, Fighter fighter, bool prone, float roll = 0.5f)
    {
        var all = s_techniques(combat);
        int category = (int)s_movesCategory.Invoke(combat, [fighter, fighter.TechniqueCategory])!;
        // Kenshi's "Dodge back fall" and "Dodge back fly" throw the dodger down: left out while another dodge serves (setting).
        CombatTechnique[] pool = ManualCombatMod.Settings.NoFallingDodges ? Upright(all) : all;
        int pick = Dodge(pool, category, prone, roll);
        if (pick < 0 && category != (int)WeaponCategory.Unarmed) pick = Dodge(pool, (int)WeaponCategory.Unarmed, prone, roll);
        if (pick < 0 && !ReferenceEquals(pool, all))
        {
            pick = Dodge(all, category, prone, roll);
            if (pick < 0 && category != (int)WeaponCategory.Unarmed) pick = Dodge(all, (int)WeaponCategory.Unarmed, prone, roll);
        }
        return pick >= 0 ? (pick, all[pick]) : null;

        static int Dodge(CombatTechnique[] all, int category, bool prone, float roll) =>
            TechniqueChooser.ChooseReaction(all, category, prone, canBlock: false, 0, blockChancePercent: 0, dodgeChancePercent: 100,
                chanceRoll: 0, pickRoll: roll);
    }

    static readonly ConditionalWeakTable<CombatTechnique[], CombatTechnique[]> s_upright = new();

    /// <summary>The techniques with the falling dodges (a dodge named "... fall" or "... fly") weighted 0, cached per list.</summary>
    static CombatTechnique[] Upright(CombatTechnique[] all) => s_upright.GetValue(all, list =>
        [.. list.Select(t => t.IsDodge && (t.Name.Contains("fall", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains(" fly", StringComparison.OrdinalIgnoreCase)) ? t with { Chance = 0 } : t)]);

    /// <summary>How many ticks <paramref name="tech"/> plays for the fighter as a defence (the combat system's own clip length and play rate).</summary>
    public static int ClipTicks(CombatSystem combat, World world, int slot, CharacterCold cold, CombatTechnique tech)
    {
        float rate = (float)s_playRate.Invoke(combat, [world, slot, cold, tech, false])!;
        return (int)s_ticks.Invoke(combat, [tech, rate, world.TickSeconds, cold])!;
    }

    /// <summary>How many enemies are fighting the fighter in <paramref name="slot"/> (their target, alive, up, within 50 units).</summary>
    public static int SwingingAt(CombatSystem combat, World world, int slot)
    {
        var read = s_read(combat);
        var prev = world.Characters.Previous;
        if ((uint)slot >= (uint)prev.Length) return 0;
        int generation = prev[slot].Generation, count = 0;
        var at = prev[slot].Position;
        for (int c = 0; c < prev.Length && c < read.Length; c++)
        {
            if (c == slot || !prev[c].Alive || read[c].Down || read[c].TargetSlot != slot || read[c].TargetGeneration != generation) continue;
            if (System.Numerics.Vector3.DistanceSquared(prev[c].Position, at) > 50 * 50) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// A dodge the player started (ManualCombatSystem: a real reaction, swing coming or not) lasts its clip: Kenshi ends a reaction as soon as no
    /// swing is coming at the fighter, which would cut a dodge in the open short at once.
    /// </summary>
    static bool ReactionEndsPrefix(CombatSystem __instance, World world, int i, ref CombatSlot s, int now, ref bool wait, ref bool __result)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, i) || s.ReactTech < 0) return true;

            // A guard held with the block button stays up, swing or no swing, until it is let go.
            if (mc.BlockHeld && IsBlock(__instance, s.ReactTech))
            {
                wait = false;
                __result = false;
                return false;
            }

            if (s.ReactTech != mc.DodgeShown || !mc.Dodging(now)) return true;
            wait = false;
            __result = now - s.ReactStart > HitOutcomes.DodgeTo * s.ReactTicks;
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ReactionEnds prefix", e);
            return true;
        }
    }

    /// <summary>
    /// The swing furthest on at fighter <paramref name="slot"/>, the blow landing this tick included (Kenshi's "landing" scan; the plain scan drops a
    /// swing on its landing tick): its attacker, attack sequence, blow, landing tick and side (a Kenshi blow code). Null with none.
    /// </summary>
    public static (int Attacker, int Seq, int Blow, int Lands, int Side)? Incoming(CombatSystem combat, World world, int slot, int now)
    {
        object swing = s_mostAdvanced.Invoke(combat, [world, slot, now, -1, true])!;
        int attacker = (int)s_blowAttacker.GetValue(swing)!;
        if (attacker < 0) return null;
        return (attacker, (int)s_blowSeq.GetValue(swing)!, (int)s_blowBlow.GetValue(swing)!, (int)s_blowLands.GetValue(swing)!,
            (int)s_blowSide.Invoke(combat, [world, slot, swing])!);
    }

    /// <summary>
    /// Kenshi's own block chance in per cent for fighter <paramref name="i"/> against <paramref name="attacker"/>'s swing, worked out as
    /// CombatSystem.ChooseDefence does (melee defence with the weapon's defence mod, gear bonus, indoors and guarding, against the attacker's attack
    /// skill; the fist factor against an unarmed person). For a fighter that cannot block (fists): its dodge chance from Martial Arts instead.
    /// -1 when there is none to work out.
    /// </summary>
    public static float KenshiBlockChance(CombatSystem combat, World world, int i, int attacker)
    {
        var table = world.Characters;
        if (table.Cold(i) is not { Fighter: { } fighter, Medical: { } medical } cold) return -1;
        var read = s_read(combat);
        if ((uint)attacker >= (uint)read.Length) return -1;
        bool guarding = (bool)s_guarding.Invoke(null, [cold])!;
        var constants = (CombatConstants)s_combatConstants.GetValue(combat)!;

        // A fighter that cannot block dodges: Kenshi's dodge chance from Martial Arts, slowed by load and bad legs (ChooseDefence's own formula).
        if (!fighter.CanBlock)
        {
            float effective = DefenceFormulas.EffectiveDefence(Stat(cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.MartialArts),
                fighter.EncumbranceFactor, Meitou.Simulation.Bodies.Speed.LegFactor(medical), fighter.DefenceBonus, guarding);
            return DefenceFormulas.BlockChance(effective, read[attacker].AttackSkill, constants, 0, 95);
        }
        if (fighter.Weapon is not { } weapon) return -1;
        float meleeDefence = (float)s_trained.Invoke(null, [cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.MeleeDefence])!;
        int indoors = (int)s_indoorsMod.Invoke(null, [world, i, fighter])!;
        float d = DefenceFormulas.Defence(meleeDefence, weapon.Stats.DefenceMod, true, fighter.DefenceBonus + indoors, guarding);
        float block = DefenceFormulas.BlockChance(d, read[attacker].AttackSkill, constants);
        if (table.Cold(attacker)?.Fighter is { Unarmed: true, IsAnimal: false } && cold.Stats is { } stats)
            block *= DefenceFormulas.FistBlockFactor(stats[Meitou.Data.Gameplay.Bodies.StatsEnumerated.MartialArts]);
        return block;
    }

    /// <summary>A block of the fighter's weapon class for any side but <paramref name="side"/> (a guard the blow beats, as Kenshi's failed roll raises), or -1.</summary>
    public static int BeatenGuard(CombatSystem combat, Fighter fighter, bool prone, int side)
    {
        int first = side switch { 1 => 2, 7 => 9, 9 => 7, _ => 1 };
        int pick = GuardIndex(combat, fighter, prone, first);
        if (pick >= 0) return pick;
        foreach (int other in (ReadOnlySpan<int>)[1, 2, 3, 4, 7, 9])
            if (other != side && other != first && (pick = GuardIndex(combat, fighter, prone, other)) >= 0) return pick;
        return -1;
    }

    /// <summary>The side (a Kenshi blow code) of the swing furthest on its way at fighter <paramref name="slot"/>, 0 with none.</summary>
    public static int IncomingSide(CombatSystem combat, World world, int slot, int now) => IncomingSideFrom(combat, world, slot, now).Side;

    /// <summary>The side and attacker of the swing furthest on its way at fighter <paramref name="slot"/>; side 0 and attacker -1 with none.</summary>
    public static (int Side, int Attacker) IncomingSideFrom(CombatSystem combat, World world, int slot, int now)
    {
        object swing = s_mostAdvanced.Invoke(combat, [world, slot, now, -1, false])!;
        int attacker = (int)s_blowAttacker.GetValue(swing)!;
        if (attacker < 0) return (0, -1);
        return ((int)s_blowSide.Invoke(combat, [world, slot, swing])!, attacker);
    }

    #region Attack

    static bool StartAttackPrefix(World world, int i, ref CombatSlot s, int now)
    {
        try
        {
            t_wantDir = 0;
            t_asked = false;
            t_outOfReach = false;
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, i)) return true;

            // No request, or this one is used up or stale: the hand-fought character does not swing on its own.
            if (mc.AttackSeq < 0 || s.AttackSeq != mc.AttackSeq || now > mc.AttackUntil) return false;
            if (now < mc.FeintLockUntil) return false;   // just feinted: the next swing waits a moment

            t_wantDir = mc.AttackTag(mc.AttackDir);
            t_asked = true;
            t_swingAnyway = mc.SwingAnyway;
            return true;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("StartAttack prefix", e);
            return true;
        }
    }

    static void StartAttackPostfix(CombatSystem __instance, World world, int i, ref CombatSlot s, int now)
    {
        int wanted = t_wantDir;
        bool asked = t_asked, outOfReach = t_outOfReach, noSide = t_noSide;
        t_noSide = false;
        t_wantDir = 0;
        t_asked = false;
        t_outOfReach = false;
        t_swingAnyway = false;
        if (!asked) return;
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is not null && outOfReach) mc.StepIn(now);
            if (mc is null || s.AttackSeq == mc.AttackSeq || s.AttackTech < 0) return;   // not started this time (no reach, no opening...)

            var tech = s_techniques(__instance)[s.AttackTech];
            string blows = tech.Blows <= 1 ? Dir.Name(tech.DirectionOf(1))
                : string.Join(" then ", Enumerable.Range(1, tech.Blows).Select(b => Dir.Name(tech.DirectionOf(b))));
            mc.Detail($"swing started: '{tech.Name}', blow from {blows} (asked {Dir.Name(wanted)})");
            if (noSide) mc.NoSwingFromSide(wanted);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("StartAttack postfix", e);
        }
    }

    /// <summary>
    /// While the hand-fought character starts a swing with a direction asked, the game's own pick is made among that direction's swings only
    /// (the others weighted 0). When the weapon has a swing of that side but none reaches the target from here, no swing starts: the request
    /// waits and the character steps in (VelocityPostfix), rather than throwing a longer swing of another side. Only when the weapon has no swing
    /// of that side at all (Kenshi has no thrusts, katanas no right swing) does the game pick one of its others. A pick outside the weapon's own
    /// moves (the game throws a kick or punch when no weapon move fits) is not taken.
    /// </summary>
    static bool ChooseAttackPrefix(IReadOnlyList<CombatTechnique> all, ref AttackQuery q, float roll, ref float maxReach, ref int __result)
    {
        int want = t_wantDir;
        if (!t_asked || t_filtering || want == Dir.None && !t_swingAnyway) return true;
        try
        {
            t_filtering = true;
            foreach (int dir in want == Dir.None ? Array.Empty<int>() : Fallbacks(want))
            {
                var list = Filtered(all, dir);
                int pick = TechniqueChooser.ChooseAttack(list, in q, roll, out float reach);
                if (pick >= 0 && FitsWeapon(all[pick], q.Category))
                {
                    __result = pick;
                    maxReach = reach;
                    return false;
                }

                // Does this side have a swing at all, standing right at the target? Then it is only out of reach: swing anyway (a whiff
                // if nobody is in its zone when the blow comes), or with "Swing even out of reach" off, wait and step in.
                var close = q with { Gap = 0 };
                int near = TechniqueChooser.ChooseAttack(list, in close, roll, out float nearReach);
                if (near >= 0 && FitsWeapon(all[near], q.Category))
                {
                    __result = t_swingAnyway ? near : -1;
                    maxReach = nearReach;
                    t_outOfReach = true;
                    return false;
                }
            }

            // The weapon has no swing of the aimed side at all: any of its swings, out of reach or not when swinging anyway (a message says so).
            if (want != Dir.None) t_noSide = true;
            if (t_swingAnyway)
            {
                int any = TechniqueChooser.ChooseAttack(all, in q, roll, out float anyReach);
                if (any < 0 || !FitsWeapon(all[any], q.Category))
                {
                    var close = q with { Gap = 0 };
                    any = TechniqueChooser.ChooseAttack(all, in close, roll, out anyReach);
                }
                if (any >= 0 && FitsWeapon(all[any], q.Category))
                {
                    __result = any;
                    maxReach = anyReach;
                    return false;
                }
            }
            return true;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ChooseAttack prefix", e);
            return true;
        }
        finally
        {
            t_filtering = false;
        }
    }

    /// <summary>
    /// "Skip the initiative roll": the hand-fought character asked to swing, so it may, unless the target's own swing at it is already on its
    /// way (then the player should block). Only reached from StartAttack, which the prefix lets run only for a player's request.
    /// </summary>
    static void HasInitiativePostfix(CombatSystem __instance, World world, int i, ref CombatSlot s, int t, int now, ref bool __result)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null) return;

            // The hand-fought character holds a readied swing (hold to ready): no blow is on its way, so an enemy may take its turn (Kenshi lets
            // nobody swing at a fighter whose swing comes at it: a held swing would keep everyone off for good).
            if (!__result && mc.HeldReady && mc.Controls(world, t) && !mc.Controls(world, i))
            {
                __result = true;
                if (s.YieldTo == t) s.YieldUntil = now;
                mc.WaitOver(i);
                return;
            }

            // An enemy asking to swing at the hand-fought character (manual combat only): Kenshi's turn-taking stays (the initiative roll, the
            // loser's half-second yield), but the yield is not stretched while the player holds a block (Kenshi starts it when the winner's block
            // ends, and the player's held block never ends), and an enemy that has kept losing the roll for the longest wait swings anyway (the
            // player's character never takes its turn by itself, so a much weaker enemy would stand and stare). How many swing at the player at
            // once is Meitou's attack slots (HasSlot, asked before this).
            if (mc.EnemiesDontWait && mc.Controls(world, t) && !mc.Controls(world, i))
            {
                if (!__result)
                {
                    if (s.YieldTo == t) s.YieldUntil = Math.Min(s.YieldUntil, now + mc.YieldTicks);
                    if (!mc.WaitedTooLong(i, now)) return;
                    __result = true;
                    if (s.YieldTo == t) s.YieldUntil = now;   // the lost roll's wait is undone
                }
                mc.WaitOver(i);   // it swings now
                return;
            }

            if (__result) return;
            if (!mc.SkipInitiative || !mc.Controls(world, i)) return;

            var read = s_read(__instance);
            if ((uint)t >= (uint)read.Length) return;
            ref readonly var target = ref read[t];
            bool atMe = target.TargetSlot == i && target.TargetGeneration == world.Characters.Previous[i].Generation;
            if (atMe && target.AttackTech >= 0 && target.NextBlow > 0 && !mc.ChamberOn) return;   // its blow is coming: no free swing
            // (with chambering on, the player may swing into it: a chamber in time, a hit taken if late)

            __result = true;
            if (s.YieldTo == t) s.YieldUntil = now;   // the losing roll's wait is undone
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("HasInitiative postfix", e);
        }
    }

    /// <summary>
    /// "Swing even out of reach": the hand-fought character is never kept back by how many may close in on its target (MayJoin) or swing at it at
    /// once (HasSlot, Kenshi's attack slots). Its swings are the player's.
    /// </summary>
    static void GatePostfix(World world, int i, ref bool __result)
    {
        try
        {
            if (__result) return;
            var mc = ManualCombatSystem.For(world);
            if (mc is not null && mc.SwingAnyway && mc.Controls(world, i)) __result = true;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("attack gate postfix", e);
        }
    }

    /// <summary>Combos: the swing the player asked for plays at its combo speed (riposte, chain).</summary>
    /// <remarks>The JIT may inline this small method into StartAttack, which skips the patch: ManualCombatSystem then rescales the new swing itself
    /// (it knows from <see cref="ManualCombatSystem.RateApplied"/> whether this ran).</remarks>
    static void PlayRatePostfix(CombatSystem __instance, World world, int slot, bool attack, ref float __result)
    {
        if (!attack || !t_asked) return;
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, slot) || mc.NextSwingRate <= 1) return;
            __result *= mc.NextSwingRate;
            mc.RateApplied(s_write(__instance)[slot].AttackSeq);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("PlayRate postfix", e);
        }
    }

    /// <summary>Manual crossbow: the aim steadies as usual, but the shot waits for the fire press.</summary>
    static bool AimPrefix(World world, int i, ref CombatSlot s, float dt)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.ManualCrossbow || !mc.Controls(world, i) || mc.FireAsked(world.Tick)) return true;
            if (s.Steady > 0) s.Steady = MathF.Max(0, s.Steady - dt);
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("Aim prefix", e);
            return true;
        }
    }

    /// <summary>
    /// Manual crossbow: the reload counts on only while the player holds it and stands still (ReloadGoes); otherwise it waits where it is
    /// (ManualCombatSystem.ReloadControl keeps a third of it once reached, and drops less, when it stops early).
    /// </summary>
    static bool ReloadPrefix(CombatSystem __instance, int i, ref CombatSlot s)
    {
        try
        {
            var mc = ManualCombatSystem.ForCombat(__instance);
            if (mc is null || !mc.ManualCrossbow || i != mc.Slot || s.Generation != mc.Generation) return true;
            if (mc.ReloadGoes) return true;
            s.RangedState = (byte)RangedStates.Reloading;
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("Reload prefix", e);
            return true;
        }
    }

    /// <summary>The crossbow chosen with the swap key: an enemy close by does not make the character put it away.</summary>
    static bool ThreatPrefix(World world, int self, ref bool __result)
    {
        var mc = ManualCombatSystem.For(world);
        if (mc is null || mc.WeaponMode != 2 || !mc.Controls(world, self)) return true;
        __result = false;
        return false;
    }

    /// <summary>The melee weapon chosen with the swap key: the crossbow is put away.</summary>
    static void ChooseModePostfix(World world, int i, ref CombatSlot s, CharacterCold cold)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || mc.WeaponMode != 1 || !s.Ranged || !mc.Controls(world, i) || cold.Fighter is not { } fighter) return;
            s.Ranged = false;
            cold.RangedCombatMode = false;
            s.RangedState = 0;
            s.ClearTime = 0;
            s.Steady = 0;
            cold.DrawnWeapon = (Meitou.Data.Gameplay.WeaponKinds)s_meleeDrawn.Invoke(null, [fighter])!;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ChooseMode postfix", e);
        }
    }

    /// <summary>An effective stat of <paramref name="cold"/> (Kenshi's own reading: injuries, encumbrance...).</summary>
    public static float Stat(CharacterCold cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated stat) => (float)s_eff.Invoke(null, [cold, stat])!;

    /// <summary>Hyper armor: a stagger on the hand-fought character may be shrugged off (poise, or the grace after a stagger).</summary>
    static bool StaggerPrefix(World world, ref CombatSlot dSlot, int d, CharacterCold dc, bool heavy, int now)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, d)) return true;
            if (dSlot.TargetSlot < 0 && !dc.InCombat) return true;   // outside a fight: Kenshi's own
            // A chamber that landed: no stagger while the chambering swing goes on.
            if (now < mc.ChamberSafeUntil && dSlot.AttackTech >= 0)
            {
                mc.Detail("stagger shrugged off (the chambering swing)");
                return false;
            }
            if (!mc.HyperArmorOn) return true;
            return !mc.ResistStagger(dc, heavy, now, dSlot.AttackTech >= 0);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("Stagger prefix", e);
            return true;
        }
    }

    /// <summary>Hyper armor: a stagger that went through starts the grace once it ends, and refills poise.</summary>
    static void StaggerPostfix(World world, ref CombatSlot dSlot, int d, int now, bool __runOriginal)
    {
        if (!__runOriginal) return;
        var mc = ManualCombatSystem.For(world);
        if (mc is null || !mc.HyperArmorOn || !mc.Controls(world, d)) return;
        mc.Staggered(dSlot.StunUntil, now);
    }

    /// <summary>The crossbow's spread for <paramref name="cold"/>: its deviation, degrees, as Kenshi's shot takes it (0 without a crossbow).</summary>
    public static float CrossbowDeviation(CharacterCold cold)
    {
        if (cold is not { Fighter.Crossbow: { } x, Stats: not null, Medical: not null }) return 0;
        float skill = (float)s_eff.Invoke(null, [cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Crossbows])!;
        float perception = (float)s_eff.Invoke(null, [cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Perception])!;
        return RangedFormulas.Deviation(x.Stats.AccuracyDeviation, skill, perception);
    }

    /// <summary>Crosshair shots: the player's fire press goes whatever stands on Kenshi's line to the target (the bolt flies where it is aimed).</summary>
    static bool LineOfFirePrefix(World world, int self, ref int __result)
    {
        var mc = ManualCombatSystem.For(world);
        if (mc is null || !mc.ManualCrossbow || !mc.HasShotRay || !mc.Controls(world, self) || !mc.FireAsked(world.Tick)) return true;
        __result = 1;
        return false;
    }

    static void ApplyFirePrefix(CombatSystem __instance, out int __state)
    {
        __state = -1;
        try
        {
            if (s_flight.GetValue(__instance) is { } flight && s_flightList.GetValue(flight) is System.Collections.IList list) __state = list.Count;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ApplyFire prefix", e);
        }
    }

    /// <summary>
    /// Crosshair shots: the bolt the player's shot just added flies from the barrel to the point the crosshair ray passes at the target's
    /// distance, strayed by the reticle's spread (a roll from the world seed and the bolt's id, so every machine flies it alike).
    /// </summary>
    static void ApplyFirePostfix(CombatSystem __instance, World world, int __state)
    {
        try
        {
            if (__state < 0) return;
            var mc = ManualCombatSystem.ForCombat(__instance);
            if (mc is null || !mc.ManualCrossbow || !mc.HasShotRay || mc.Slot < 0) return;
            if (s_flight.GetValue(__instance) is not { } flight || s_flightList.GetValue(flight) is not System.Collections.IList list) return;
            if (list.Count <= __state) return;   // no bolt added

            object bolt = list[list.Count - 1]!;
            if ((int)s_pShooter.GetValue(bolt)! != mc.Slot) return;
            var barrel = (Vector3)s_pPosition.GetValue(bolt)!;
            int target = (int)s_pTarget.GetValue(bolt)!, id = (int)s_pId.GetValue(bolt)!;

            var origin = mc.ShotOrigin;
            var ray = mc.ShotDirection;
            var prev = world.Characters.Previous;
            // The bolt flies from the barrel to what is under the crosshair (a body or the ground), so the reticle shows where it goes however near:
            // the barrel sits away from the camera, and aimed at any other distance the bolt crossed the crosshair line elsewhere (parallax).
            float? hit = CrosshairHit(world, mc.Slot, origin, ray, CrosshairReach);
            float along = hit ?? ((uint)target < (uint)prev.Length ? Vector3.Dot(prev[target].Position - origin, ray) : 0);
            if (!(along > 5)) along = 500;   // nothing under the crosshair and no target ahead: a far point on the ray
            var to = origin + ray * along - barrel;
            if (to.LengthSquared() < 1e-6f) return;

            ulong r = Mix(world.Seed ^ 0x4D616E75616C4342UL ^ (ulong)(uint)id);
            float u = (r >> 40) / (float)(1UL << 24), spin = ((r >> 16) & 0xFFFFFF) / (float)(1UL << 24);
            var dir = RangedFormulas.Deviate(Vector3.Normalize(to), u * mc.ShotSpread, spin);
            s_pDirection.SetValue(bolt, dir);
            list[list.Count - 1] = bolt;
            mc.Detail($"shot at the crosshair ({(hit is { } d ? $"at {d / 10:0.0} m under it" : "nothing under it")}, spread {mc.ShotSpread:0.00} degrees, "
                + $"strayed {u * mc.ShotSpread:0.00})");
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ApplyFire postfix", e);
        }
    }

    /// <summary>How far along the crosshair line a shot looks for what it aims at (units; 10 is about a metre), and where the look starts (past the
    /// camera's boom and the shooter's own body), the ground's sampling step, and a body's height before its scale.</summary>
    const float CrosshairReach = 2500, CrosshairFrom = 5, GroundStep = 2, BodyHeight = 18;

    /// <summary>
    /// The distance along the crosshair line (from <paramref name="origin"/>, the camera, along the unit <paramref name="ray"/>) to what is under
    /// the crosshair: the first living character's body (an upright cylinder round its feet, its race's footprint radius wide and a body's height
    /// tall, scaled with it; never the shooter) or the ground, whichever comes first within <paramref name="far"/>; null with nothing (the sky).
    /// From the world's own places and ground, so every machine aims the same.
    /// </summary>
    static float? CrosshairHit(World world, int shooter, Vector3 origin, Vector3 ray, float far)
    {
        var prev = world.Characters.Previous;
        float best = far;
        bool found = false;
        float flat2 = ray.X * ray.X + ray.Z * ray.Z;
        if (flat2 > 1e-6f)
        {
            for (int c = 0; c < prev.Length; c++)
            {
                if (c == shooter || !prev[c].Alive) continue;
                var feet = prev[c].Position;
                // the point of the line nearest the body's upright axis, seen from above
                float t = ((feet.X - origin.X) * ray.X + (feet.Z - origin.Z) * ray.Z) / flat2;
                if (t < CrosshairFrom || t >= best) continue;
                var cold = world.Characters.Cold(c);
                float radius = cold?.Race?.PathfindFootprintRadius ?? 3, height = BodyHeight * (cold?.Medical?.ScaleK ?? 1);
                var p = origin + ray * t;
                float dx = p.X - feet.X, dz = p.Z - feet.Z;
                if (dx * dx + dz * dz > radius * radius || p.Y < feet.Y || p.Y > feet.Y + height) continue;
                best = t;
                found = true;
            }
        }

        // the ground, sampled along the line up to the body found
        for (float t = CrosshairFrom; t < best; t += GroundStep)
        {
            var p = origin + ray * t;
            if (p.Y > world.Walkability.TerrainHeight(p.X, p.Z)) continue;
            best = t;
            found = true;
            break;
        }
        return found ? best : null;
    }

    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static int[] Fallbacks(int want) => want switch
    {
        Dir.Overhead => [Dir.Overhead, Dir.Up],
        Dir.Thrust => [Dir.Thrust],
        _ => [want],
    };

    /// <summary>A copy of <paramref name="all"/> whose techniques of another first-blow direction have chance 0 (cached per list).</summary>
    static IReadOnlyList<CombatTechnique> Filtered(IReadOnlyList<CombatTechnique> all, int dir)
    {
        lock (s_filterLock)
        {
            var byDir = s_filtered.GetValue(all, _ => new CombatTechnique[]?[16]);
            return byDir[dir] ??= [.. all.Select(t => t.DirectionOf(1) == dir ? t : t with { Chance = 0 })];
        }
    }

    /// <summary>A pick roll that changes from press to press (which of several dodges plays), the same for the same tick.</summary>
    static float Roll(long tick) => (float)((tick * 0.6180339887) % 1.0);

    static bool FitsWeapon(CombatTechnique t, int category) =>
        category == (int)WeaponCategory.Unarmed || (bool)s_fits.Invoke(null, [t, category])!;

    #endregion

    #region Defence

    /// <summary>
    /// The hand-fought character's defence against a swing: none unless the block button is held; with it, the guard for the side the player
    /// holds (directional blocking) or for the blow's own side (easy blocking). A wrong guard is still raised, and the blow gets through. A
    /// fighter that cannot block (fists, a weapon that cannot) dodges instead. Stumble break-outs are left to the game.
    /// </summary>
    /// <summary>
    /// An enemy's defence against one of the hand-fought character's swings: its first decision stands. Kenshi rolls each decision afresh, so a
    /// swing held at the ready (or the guard let off and raised again, ManualCombatSystem) gave an enemy that lost its block roll roll after roll
    /// until one won: it blocked though its stats would not. A lost first roll now means no defence against that swing (a side switch is a new
    /// swing, a new roll; so is letting the held swing go: the strike itself gets a fresh roll). A won one changes nothing (Kenshi's own rolls from
    /// there, as in a plain fight).
    /// </summary>
    static void ChooseDefencePostfix(World world, int i, bool stumbling, object[] __args, ref int __result)
    {
        if (stumbling) return;
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || mc.Slot < 0 || i == mc.Slot) return;
            object swing = __args[s_chooseDefenceSwing];
            if ((int)s_blowAttacker.GetValue(swing)! != mc.Slot) return;
            int seq = (int)s_blowSeq.GetValue(swing)!;
            if (!mc.FirstDefence(i, mc.DefenceKey(seq), __result >= 0))
            {
                if (__result >= 0) mc.Detail($"{world.Characters.Cold(i)?.Name}: no block (its first roll against this swing was lost)");
                __result = -1;
            }
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ChooseDefence postfix", e);
        }
    }

    static bool ChooseDefencePrefix(CombatSystem __instance, World world, int i, CharacterCold cold, bool stumbling, object[] __args,
        ref int __result)
    {
        try
        {
            if (stumbling) return true;
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, i)) return true;

            // The dodge button: within its window the coming swing is met with a dodge, block held or not.
            if (mc.Dodging(world.Tick) && cold.Fighter is { } dodger
                && DodgeTechnique(__instance, dodger, cold.Medical!.Prone, Roll(world.Tick)) is { } dodge)
            {
                __result = dodge.Index;
                mc.Detail($"dodging the swing with '{dodge.Technique.Name}'");
                return false;
            }

            if (!mc.BlockHeld)
            {
                __result = -1;
                return false;
            }

            object swing = __args[s_chooseDefenceSwing];
            int attacker = (int)s_blowAttacker.GetValue(swing)!;
            int seq = (int)s_blowSeq.GetValue(swing)!;
            int blow = (int)s_blowBlow.GetValue(swing)!;
            int lands = (int)s_blowLands.GetValue(swing)!;
            int side = BlowSide(__instance, world, i, attacker, blow);
            // Blocking turns the character to the attacker: a blow from behind is met with the front guard (StartDefence follows this swing, so
            // the game turns the character to it before it lands), and the attacker becomes the target (the next Schedule).
            if (mc.TurnToBlocked)
            {
                side = mc.FrontSide(world, attacker, blow, side, lands, world.Tick);
                mc.BlockingFrom(attacker);
            }
            // A group fight (auto-block): the held guard turns to each swing's side by itself.
            int guard = mc.AutoBlockAt > 0 && mc.Attackers >= mc.AutoBlockAt ? side : mc.GuardFor(side);

            var fighter = cold.Fighter!;
            bool prone = cold.Medical!.Prone;

            // Fists (or a weapon that cannot block): the held right click dodges. Kenshi's dodge roll decides, failing less often than in Kenshi
            // (Block beaten chance); a perfect dodge (pressed within the parry window, from Martial Arts) always works and lets the next attack
            // come at once (ManualCombatSystem.PerfectDodge).
            if (!fighter.CanBlock)
            {
                bool perfect = mc.IsPerfectPress(lands);
                if (!mc.BlockHolds(world, attacker, seq, blow, lands))
                {
                    __result = -1;
                    mc.Detail("dodge beaten (the dodge roll lost)");
                    return false;
                }
                __result = DodgeTechnique(__instance, fighter, prone, Roll(world.Tick))?.Index ?? -1;
                if (perfect && __result >= 0) mc.PerfectDodge(lands);
                mc.Detail($"swing coming: {(perfect ? "perfect " : "")}dodge{(__result < 0 ? " - no dodge move" : "")}");
                return false;
            }

            // The right guard still has to win Kenshi's block roll (more generous than Kenshi's; a parry press always wins): a lost roll raises a
            // guard on another side, which the blow gets past, as Kenshi's failed roll does.
            if (guard == side && fighter.CanBlock && !mc.BlockHolds(world, attacker, seq, blow, lands))
            {
                __result = BeatenGuard(__instance, fighter, prone, side);
                mc.Detail($"swing coming from {Dir.Name(side)}: guard beaten (the block roll lost)");
                return false;
            }
            int category = (int)s_movesCategory.Invoke(__instance, [fighter, fighter.TechniqueCategory])!;
            __result = TechniqueChooser.ChooseReaction(s_techniques(__instance), category, prone, fighter.CanBlock, guard,
                blockChancePercent: 100, dodgeChancePercent: 100, chanceRoll: 0, pickRoll: 0.5f);
            mc.Detail($"swing coming from {Dir.Name(side)}: guard {Dir.Name(guard)}{(guard == side ? "" : " (wrong side)")}{(__result < 0 ? " - no guard move for this weapon" : "")}");
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ChooseDefence prefix", e);
            return true;
        }
    }

    /// <summary>The swing's own side for <paramref name="blow"/> (as it comes at a defender facing the attacker), or 0.</summary>
    internal static int SwingSide(CombatSystem combat, int attacker, int blow)
    {
        var read = s_read(combat);
        var techniques = s_techniques(combat);
        if ((uint)attacker >= (uint)read.Length) return 0;
        int tech = read[attacker].AttackTech;
        return (uint)tech < (uint)techniques.Length ? techniques[tech].DirectionOf(blow) : 0;
    }

    /// <summary>The side a blow comes from as the defender stands now (CombatSystem.BlowSide).</summary>
    internal static int BlowSide(CombatSystem combat, World world, int i, int attacker, int blow)
    {
        var read = s_read(combat);
        var techniques = s_techniques(combat);
        if ((uint)attacker >= (uint)read.Length) return 0;
        int tech = read[attacker].AttackTech;
        if ((uint)tech >= (uint)techniques.Length) return 0;

        var prev = world.Characters.Previous;
        return HitOutcomes.Facing(techniques[tech].DirectionOf(blow),
            HitOutcomes.Behind(prev[attacker].Position, prev[i].Position, prev[i].Yaw));
    }

    /// <summary>
    /// A guard the hand-fought character raised counts if it is up when the blow lands, however late the player pressed: its clip is set to
    /// be past the half the game asks for (HitOutcomes.BlockProgress) at the blow's landing tick.
    /// </summary>
    static void StartDefencePostfix(CombatSystem __instance, World world, int i, ref CombatSlot s, object[] __args)
    {
        try
        {
            if (s.ReactTech < 0 || s.ReactTicks <= 0) return;
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, i)) return;

            var tech = s_techniques(__instance)[s.ReactTech];
            if (!(tech.IsBlock || tech.IsDodge)) return;

            // A block counts past half its clip, a dodge from 0.1 of it (HitOutcomes): the clip is set to be that far on when the blow lands.
            int lands = (int)s_blowLands.GetValue(__args[s_startDefenceSwing])!;
            float least = tech.IsDodge ? HitOutcomes.DodgeFrom : HitOutcomes.BlockProgress;
            int latest = lands - (int)MathF.Ceiling(least * s.ReactTicks) - 1;
            if (s.ReactStart > latest) s.ReactStart = latest;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("StartDefence postfix", e);
        }
    }

    /// <summary>A blow the hand-fought character blocked within the parry window of pressing block: the attacker staggers.</summary>
    static void ApplyBlockedPostfix(CombatSystem __instance, World world, int a, int d, CharacterCold ac, int now)
    {
        try
        {
            var mc = ManualCombatSystem.For(world);
            if (mc is null || !mc.Controls(world, d)) return;
            if (!mc.ParryOn || mc.ParryTicks <= 0 || mc.BlockPressTick == long.MinValue || mc.BlockPressPaused || now - mc.BlockPressTick > mc.ParryTicks)
            {
                mc.Detail("blocked");
                return;
            }

            var slots = s_write(__instance);
            if ((uint)a >= (uint)slots.Length) return;

            // The stagger picks its clip by the body part hit: the chest, as a solid blow to the guard would rock the attacker.
            var slot = slots[a];
            slot.HitPart = ChestOf(ac);
            slot.HitBehind = false;
            ulong counter = 0x4D414E55414CUL ^ ((ulong)(uint)a << 32) ^ (uint)now;
            object[] args = [world, slot, a, ac, true, now, counter, false];
            s_stagger.Invoke(__instance, args);
            slots[a] = (CombatSlot)args[1];

            long after = now - mc.BlockPressTick;
            mc.Parried(now);
            ManualCombatMod.Notify("Parry!");
            mc.Detail($"PARRY! attacker {a} staggered ({after} ticks after the press; parries this world: {mc.Parries})");
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("ApplyBlocked postfix", e);
        }
    }

    static int ChestOf(CharacterCold c)
    {
        var parts = c.Medical?.Parts;
        if (parts is null || parts.Count == 0) return -1;
        for (int p = 0; p < parts.Count; p++)
            if (parts[p].Template.StringId.Contains("chest", StringComparison.OrdinalIgnoreCase)) return p;
        return 0;
    }

    #endregion

    #region Lookup helpers

    static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(CombatPatches), name));

    static T Need<T>(T? member, string what, List<string> missing) where T : class
    {
        if (member is null) missing.Add(what);
        return member!;
    }

    static void Field(ref AccessTools.FieldRef<CombatSystem, CombatSlot[]> into, string name, List<string> missing)
    {
        if (AccessTools.Field(typeof(CombatSystem), name) is { } f && f.FieldType == typeof(CombatSlot[]))
            into = AccessTools.FieldRefAccess<CombatSystem, CombatSlot[]>(name);
        else missing.Add($"field CombatSystem.{name}");
    }

    static void Params(MethodInfo? m, List<string> missing, params string[] names)
    {
        if (m is null) return;
        foreach (string n in names)
            if (IndexOf(m, n) < 0) missing.Add($"parameter '{n}' of {m.DeclaringType?.Name}.{m.Name}");
    }

    static int IndexOf(MethodInfo? m, string name) => m is null ? -1 : Array.FindIndex(m.GetParameters(), p => p.Name == name);

    #endregion
}
