// Manual Combat for Meitou. GPLv3 or later.
// The rows on Settings > Mods > Manual Combat. The page changes them on the interface's thread; ManualCombatSystem copies what the world needs
// in its serial Inputs step each tick (docs/modding.md "Settings").
using Meitou.Modding;

namespace ManualCombat;

public sealed class ManualCombatSettings
{
    public const string AimMouse = "Mouse flick", AimKeys = "Movement keys";

    [Setting("Aim swings and guards with",
        Hint = "Mouse flick: the last way you moved the mouse picks the side (left, right, up = overhead, down = thrust), as in Mount & Blade. "
            + "Movement keys: hold A, D, W or S instead.",
        Options = [AimMouse, AimKeys])]
    public string AimWith { get; set; } = AimMouse;

    [Setting("Mouse flick sensitivity", Min = 2, Max = 40, Step = 1,
        Hint = "How far (in pixels) the mouse must move in an instant for a new side. Lower switches on smaller flicks.",
        VisibleWhen = nameof(AimingWithMouse))]
    public int FlickPixels { get; set; } = 4;

    /// <summary>For VisibleWhen: the flick setting shows only while aiming with the mouse.</summary>
    public bool AimingWithMouse => AimWith == AimMouse;

    [Setting("Left/right click fight in a fight",
        Hint = "On: while your character is in a fight, left click attacks and right click blocks (rebind under Controls). Clicks on windows and "
            + "the HUD work as usual, and outside fights the mouse is normal. Press U to leave the fight and get the normal clicks back.")]
    public bool InFightClicks { get; set; } = true;

    [Setting("Direction arrows", Hint = "Mount & Blade's arrows round the crosshair in a fight: gold for your swing's side, blue for your guard. "
        + "Drawn under the game's windows.")]
    public bool Arrows { get; set; } = true;

    [Setting("Arrow opacity", Min = 0.1, Max = 1, Step = 0.05, VisibleWhen = nameof(Arrows))]
    public double ArrowOpacity { get; set; } = 0.45;

    [Setting("Show the incoming swing's side", Hint = "A red arrow pointing in from the side the enemy's swing comes from.",
        VisibleWhen = nameof(Arrows))]
    public bool ArrowIncoming { get; set; } = true;

    [Setting("Attack what you look at",
        Hint = "An attack turns on the enemy in reach nearest the crosshair; and when your target is out of reach, on an enemy in reach in front.")]
    public bool AutoRetarget { get; set; } = true;

    [Setting("Swing even out of reach",
        Hint = "On: a click always swings, the way you aimed; too far and you whiff (and Kenshi's longer pause after a miss leaves you open). "
            + "Kenshi's limit on how many may attack one target at once does not hold you back. Off: the swing waits while you step in.")]
    public bool SwingAnyway { get; set; } = true;

    [Setting("Mirror attack sides",
        Hint = "Kenshi names a swing by the side it lands on. On (as Mount & Blade): aiming right swings from your right. "
            + "Turn off if a weapon's swings come out the wrong way round.")]
    public bool MirrorAttacks { get; set; } = true;

    [Setting("No falling dodges",
        Hint = "Leave out Kenshi's dodges that throw you to the ground (\"Dodge back fall\" and \"Dodge back fly\") when another dodge is there.")]
    public bool NoFallingDodges { get; set; } = true;

    [Setting("W A S D footwork in fights",
        Hint = "On: in a fight your hand-fought character steps with W A S D (facing its enemy) instead of walking away, which in Kenshi would end "
            + "the fight. Press U to leave the fight. Off: W A S D walk as usual and end the fight.")]
    public bool HoldInFights { get; set; } = true;

    [Setting("Fight-mode hint", Hint = "A small line at the top right while you fight by hand: W A S D step, U leaves the fight.",
        VisibleWhen = nameof(HoldInFights))]
    public bool FightHint { get; set; } = true;

    [Setting("Hint height (pixels from the top)", Min = 0, Max = 600, Step = 2,
        Hint = "Where the fight-mode hint sits (at 1080p; it scales with the screen).", VisibleWhen = nameof(FightHint))]
    public int HintTop { get; set; } = 84;

    [Setting("Footwork speed", Min = 8, Max = 40, Step = 1,
        Hint = "How fast your character steps in a fight (Kenshi's own footwork is 18).", VisibleWhen = nameof(HoldInFights))]
    public double FootworkSpeed { get; set; } = 18;

    [Setting("Close in by itself",
        Hint = "On: with no key held your character keeps Kenshi's own footwork (it closes in and keeps its distance). Off: it stands still.",
        VisibleWhen = nameof(HoldInFights))]
    public bool AutoFootwork { get; set; } = false;

    [Setting("Show the guard at once",
        Hint = "Holding block raises the guard right away, even with no swing coming (the real block takes over when one comes).")]
    public bool GuardPose { get; set; } = true;

    // Blocking is always directional (the guard must face the swing's side, aimed like your swings): an integral part of the mod, no setting.

    [Setting("Mirror left and right guards",
        Hint = "Swap which side a left or right aim guards, if blocking left feels backwards.")]
    public bool MirrorGuards { get; set; } = false;

    [Setting("Parry", Hint = "A block pressed just before the swing lands staggers the attacker.")]
    public bool Parry { get; set; } = true;

    // The parry window comes from the character's Defence (Martial Arts with fists): ManualCombatSystem.SkillWindow.

    [Setting("Block beaten chance (x Kenshi's)", Min = 0, Max = 1, Step = 0.05,
        Hint = "A held block facing the right way can still be beaten, as in Kenshi, when the attacker's skill is well above your defence. "
            + "1: Kenshi's own chance; 0.5: half as likely; 0: a held block always holds. A parry always blocks.")]
    public double BlockBeatenScale { get; set; } = 0.5;

    [Setting("Attack buffer (seconds)", Min = 0.1, Max = 2, Step = 0.05,
        Hint = "How long an attack press waits for the moment your character is able to swing. Short: a press either swings now or not at all. "
            + "(Stepping in for an aimed swing out of reach gets up to 1.5 s.)")]
    public double AttackBuffer { get; set; } = 0.4;

    [Setting("Block and dodge cancel your swing (feint)",
        Hint = "Pressing block or dodge early in your own swing gives it up, and shortens the pause after a swing, in which Kenshi lets a "
            + "fighter neither attack nor defend. Late in the swing you are committed.")]
    public bool CancelSwing { get; set; } = true;

    [Setting("Feint window", Min = 0, Max = 1, Step = 0.05, SubGroup = "Feints",
        Hint = "How far into your wind-up a block or dodge can still cancel the swing (0.5: the first half of the way to the blow).",
        VisibleWhen = nameof(CancelSwing))]
    public double FeintWindow { get; set; } = 0.5;

    [Setting("Locked after your blow (seconds)", Min = 0, Max = 0.8, Step = 0.05, SubGroup = "Feints",
        Hint = "After your swing's blow, this long passes before you can block or dodge.", VisibleWhen = nameof(CancelSwing))]
    public double RecoveryLock { get; set; } = 0.2;

    [Setting("Attack lockout after a feint (seconds)", Min = 0, Max = 1, Step = 0.05, SubGroup = "Feints",
        Hint = "After you cancel a swing, your next attack waits this long, so feints are not free.", VisibleWhen = nameof(CancelSwing))]
    public double FeintLockout { get; set; } = 0.3;

    [Setting("Combos", SubGroup = "Combos and counters",
        Hint = "A swing right after a parry comes out faster (a riposte), and attacking from another side at the right moment after a hit cuts straight into the next swing (a combo).")]
    public bool Combos { get; set; } = true;

    [Setting("Riposte speed", Min = 1, Max = 2, Step = 0.05, SubGroup = "Combos and counters", VisibleWhen = nameof(Combos),
        Hint = "How much faster your first swing within 1.5 s of a parry plays.")]
    public double RiposteSpeed { get; set; } = 1.35;

    [Setting("Combo speed per step", Min = 0, Max = 0.6, Step = 0.02, SubGroup = "Combos and counters", VisibleWhen = nameof(Combos),
        Hint = "How much faster the second swing of a combo is.")]
    public double ChainSpeed { get; set; } = 0.1;

    [Setting("Combo timing starts (seconds after the hit)", Min = 0, Max = 0.5, Step = 0.01, SubGroup = "Combos and counters",
        VisibleWhen = nameof(Combos),
        Hint = "A combo: after your swing hits, attack from a different side in this window and your swing is cut short, the pause after it "
            + "skipped, and the next swing comes out at once (and faster). Too early or too late: a normal swing after the pause.")]
    public double ComboFrom { get; set; } = 0.05;

    [Setting("Combo timing length (seconds)", Min = 0.05, Max = 1, Step = 0.01, SubGroup = "Combos and counters", VisibleWhen = nameof(Combos))]
    public double ComboLength { get; set; } = 0.2;

    [Setting("Rest after a combo (seconds)", Min = 0, Max = 3, Step = 0.05, SubGroup = "Combos and counters", VisibleWhen = nameof(Combos),
        Hint = "After a combo, your next ordinary swing waits this long after the combo's last swing ends (blocking and dodging stay free).")]
    public double ComboRest { get; set; } = 0.8;

    [Setting("Clashing", SubGroup = "Combos and counters",
        Hint = "Your swing and an enemy's swing at you, both from the same side, landing at almost the same moment meet in the air: those two stop, sparks fly. In a group only that one enemy's swing stops.")]
    public bool Clash { get; set; } = true;

    [Setting("Clash window (seconds)", Min = 0.03, Max = 0.4, Step = 0.01, SubGroup = "Combos and counters", VisibleWhen = nameof(Clash))]
    public double ClashWindow { get; set; } = 0.12;

    [Setting("Crush through clashes", SubGroup = "Combos and counters", VisibleWhen = nameof(Clash),
        Hint = "In a clash the stronger fighter may crush through and land the blow anyway: the chance grows with the gap in Strength, weapon "
            + "skill and weapon weight between the two (enemies can crush through you too).")]
    public bool ClashCrush { get; set; } = true;

    [Setting("Crush-through chance scale", Min = 0, Max = 4, Step = 0.1, SubGroup = "Combos and counters", VisibleWhen = nameof(ClashCrush),
        Hint = "1.5: a fighter twice as strong overall crushes through about half the time; at most 90%.")]
    public double CrushScale { get; set; } = 1.5;

    [Setting("Chambering", SubGroup = "Combos and counters",
        Hint = "Attack from the same side as an enemy's blow just before it lands on you and your swing turns it aside while it goes on (their blow misses).")]
    public bool Chamber { get; set; } = true;

    // The chamber window comes from the character's Attack (Martial Arts with fists): ManualCombatSystem.SkillWindow.

    [Setting("Clash and chamber sounds", SubGroup = "Combos and counters",
        Hint = "A clash or a chamber plays the game's parry sound.")]
    public bool CounterSounds { get; set; } = true;

    [Setting("Fair enemy pacing",
        Hint = "Only in manual combat. Enemies keep Kenshi's turn-taking, but one that loses its turn waits only Kenshi's half second (holding a "
            + "block no longer makes them stand and stare), and never longer than the longest wait below. How many may swing at you at once is "
            + "Meitou's attack slots setting.")]
    public bool EnemiesDontWait { get; set; } = true;

    [Setting("Longest enemy wait (seconds)", Min = 0, Max = 5, Step = 0.25, VisibleWhen = nameof(EnemiesDontWait),
        Hint = "Kenshi's initiative roll favours the stronger fighter, and your character never swings unless you attack, so a much weaker "
            + "enemy could stand in front of you for many seconds. One that has kept losing the roll this long swings anyway. 0: no limit.")]
    public double LongestEnemyWait { get; set; } = 2.5;

    [Setting("Hyper armor", SubGroup = "Staggering",
        Hint = "While you swing, your character may shrug off a hit that would stagger it (a light hit costs 1 poise, a heavy one 2), from a "
            + "small poise pool that grows with Toughness and refills after a while without being hit. After a stagger, a short grace in which "
            + "nothing staggers you again.")]
    public bool HyperArmor { get; set; } = true;

    [Setting("Only while swinging", SubGroup = "Staggering", VisibleWhen = nameof(HyperArmor),
        Hint = "Poise only holds while you swing (hyper armor on attacks). Off: it holds at any time.")]
    public bool ArmorSwingOnly { get; set; } = true;

    [Setting("Poise scale", Min = 0, Max = 3, Step = 0.1, SubGroup = "Staggering", VisibleWhen = nameof(HyperArmor),
        Hint = "Poise is 1 per 40 Toughness (0 below 40, 1 at 40, 2 at 80), times this.")]
    public double PoiseScale { get; set; } = 1.0;

    [Setting("Poise refill (seconds without a hit)", Min = 0.5, Max = 15, Step = 0.25, SubGroup = "Staggering", VisibleWhen = nameof(HyperArmor))]
    public double PoiseRefill { get; set; } = 5;

    [Setting("Grace after a stagger (seconds)", Min = 0, Max = 3, Step = 0.05, SubGroup = "Staggering", VisibleWhen = nameof(HyperArmor),
        Hint = "Once a stagger ends, nothing staggers you again for this long: no stun-locks.")]
    public double StaggerGrace { get; set; } = 0.5;

    [Setting("Manual crossbow", SubGroup = "Crossbows",
        Hint = "With a crossbow out in a fight: left click fires where the crosshair points (left click on an empty crossbow reloads), "
            + "hold right click to steady the aim. X swaps between the crossbow and your melee weapon.")]
    public bool ManualCrossbow { get; set; } = true;

    [Setting("Spread", Min = 0, Max = 5, Step = 0.05, SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow),
        Hint = "How far a bolt may stray from the crosshair, times the crossbow's own spread for your character's Crossbows and Perception skills "
            + "(1 = Kenshi's; 0 = every bolt exactly at the dot). The circle round the dot shows it.")]
    public double SpreadScale { get; set; } = 1.25;

    [Setting("Steadied spread", Min = 0, Max = 1, Step = 0.05, SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow),
        Hint = "The spread with right click held long enough, as a share of the normal spread.")]
    public double SteadySpread { get; set; } = 0.35;

    [Setting("Time to steady (seconds)", Min = 0.1, Max = 5, Step = 0.1, SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow))]
    public double SteadyTime { get; set; } = 1.0;

    [Setting("Zoom when steadied", Min = 0.25, Max = 1, Step = 0.05, SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow),
        Hint = "In PZA's over-the-shoulder or first-person view, holding right click with the crossbow out narrows the view to this share of its "
            + "width as the aim steadies (0.5: twice as close). 1: no zoom.")]
    public double CrossbowZoom { get; set; } = 0.5;

    [Setting("Walking speed while aiming", Min = 0.1, Max = 1, Step = 0.05, SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow),
        Hint = "In a fight, W A S D step this slowly with the crossbow aimed (and firing), as a share of the footwork speed. The upper body keeps "
            + "the aim, the legs walk.")]
    public double CrossbowWalkSpeed { get; set; } = 0.35;

    [Setting("Reload while walking", SubGroup = "Crossbows", VisibleWhen = nameof(ManualCrossbow),
        Hint = "On: the held reload goes on while you step (in a fight at the reload walking speed below). Off: stepping stops it (a third, once "
            + "reached, is kept).")]
    public bool ReloadWhileWalking { get; set; } = true;

    [Setting("Walking speed while reloading", Min = 0.05, Max = 1, Step = 0.05, SubGroup = "Crossbows", VisibleWhen = nameof(ReloadWhileWalking),
        Hint = "In a fight, W A S D step this slowly while the reload runs, as a share of the footwork speed.")]
    public double ReloadWalkSpeed { get; set; } = 0.2;

    [Setting("Hold to ready your swing",
        Hint = "As in Mount & Blade: hold left click and your swing stops at the top of its wind-up and waits; let go to strike. A quick click "
            + "swings at once. Right click while readied feints.")]
    public bool HoldToReady { get; set; } = true;

    [Setting("Ready point", Min = 0.3, Max = 0.95, Step = 0.05, VisibleWhen = nameof(HoldToReady),
        Hint = "Where in the wind-up a held swing waits, as a share of the way to the blow (0.7: most of the wind-up done).")]
    public double ReadyPoint { get; set; } = 0.7;

    [Setting("Wind-up speed into a held swing", Min = 1, Max = 3, Step = 0.1, VisibleWhen = nameof(HoldToReady),
        Hint = "A held swing reaches its ready pose this much faster (the strike after you let go plays at its normal speed). 1: no faster.")]
    public double HeldWindup { get; set; } = 1.6;

    [Setting("Enemy guard against a held swing (seconds)", Min = 0.05, Max = 3, Step = 0.05, VisibleWhen = nameof(HoldToReady),
        Hint = "Enemies treat a held attack as coming only this long (switching sides does not start it again); then they may lower their guard "
            + "to attack you. One that cannot attack guards again at once.")]
    public double HeldGuardTime { get; set; } = 0.3;

    [Setting("Switch sides while holding", VisibleWhen = nameof(HoldToReady),
        Hint = "Flick the mouse while holding a swing and it changes to that side (the new swing winds up from part way).")]
    public bool HeldSideSwitch { get; set; } = true;

    [Setting("Walk while holding a swing", VisibleWhen = nameof(HoldToReady),
        Hint = "W A S D step while you hold a swing ready: the upper body holds the swing, the legs walk. Let go and the full swing plays.")]
    public bool WalkReadied { get; set; } = true;

    [Setting("Walking speed while holding", Min = 0.2, Max = 1, Step = 0.05, VisibleWhen = nameof(WalkReadied),
        Hint = "As a share of the footwork speed.")]
    public double ReadiedWalkSpeed { get; set; } = 0.5;

    [Setting("Cut the recovery",
        Hint = "Once your swing's blow has landed or missed (and the lock after your blow is over), blocking, dodging, stepping or attacking "
            + "cuts off the rest of the swing animation instead of waiting for it.")]
    public bool CutRecovery { get; set; } = true;

    [Setting("Blocking turns you to the attacker",
        Hint = "Blocking a swing from the side or from behind turns your character to face that attacker, and makes it your target.")]
    public bool TurnToBlocked { get; set; } = true;

    [Setting("Animal wind-up speed", Min = 0.4, Max = 1, Step = 0.05, SubGroup = "Animals",
        Hint = "Animals' attacks at you wind up this much slower, so you see them coming (the blow and after play at normal speed). 1: Kenshi's speed.")]
    public double AnimalWindup { get; set; } = 0.75;

    [Setting("Bigger warning for animal attacks", SubGroup = "Animals", VisibleWhen = nameof(Arrows),
        Hint = "The red incoming arrow is larger and stronger (still see-through) while an animal's attack comes at you.")]
    public bool AnimalWarning { get; set; } = true;

    // Slow motion: optional, through PZA's SlowMo mod. These rows show only when that mod is in the load order; without it nothing happens.

    /// <summary>For VisibleWhen: PZA's SlowMo is loaded.</summary>
    public bool SlowMoFound => SlowMo.Installed;
    public bool SlowMoMomentShown => SlowMo.Installed && (SlowMoParry || SlowMoPerfectDodge);
    public bool SlowMoRiposteShown => SlowMo.Installed && SlowMoRiposte;

    [Setting("Slow motion on a parry", SubGroup = "Slow motion (PZA's SlowMo)", VisibleWhen = nameof(SlowMoFound),
        Hint = "A parry slows time for a short moment.")]
    public bool SlowMoParry { get; set; } = true;

    [Setting("Slow motion on a perfect dodge", SubGroup = "Slow motion (PZA's SlowMo)", VisibleWhen = nameof(SlowMoFound),
        Hint = "A perfect dodge (right click with fists, or Mouse 4) slows time for a short moment.")]
    public bool SlowMoPerfectDodge { get; set; } = true;

    [Setting("Moment speed", Min = 0.1, Max = 0.9, Step = 0.05, SubGroup = "Slow motion (PZA's SlowMo)", VisibleWhen = nameof(SlowMoMomentShown),
        Hint = "How fast time runs in the moment after a parry or perfect dodge (0.3: a third of normal speed).")]
    public double SlowMoSpeed { get; set; } = 0.3;

    [Setting("Moment length (real seconds)", Min = 0.1, Max = 2, Step = 0.05, SubGroup = "Slow motion (PZA's SlowMo)",
        VisibleWhen = nameof(SlowMoMomentShown))]
    public double SlowMoSeconds { get; set; } = 0.4;

    [Setting("Slow motion on the riposte", SubGroup = "Slow motion (PZA's SlowMo)", VisibleWhen = nameof(SlowMoFound),
        Hint = "Your riposte (the faster swing after a parry or perfect dodge) plays in slow motion until its blow lands.")]
    public bool SlowMoRiposte { get; set; } = true;

    [Setting("Riposte speed in slow motion", Min = 0.1, Max = 0.9, Step = 0.05, SubGroup = "Slow motion (PZA's SlowMo)",
        VisibleWhen = nameof(SlowMoRiposteShown), Hint = "How fast time runs during the riposte swing.")]
    public double SlowMoRiposteSpeed { get; set; } = 0.5;

    [Setting("Riposte slow motion after the blow (seconds)", Min = 0, Max = 1.5, Step = 0.05, SubGroup = "Slow motion (PZA's SlowMo)",
        VisibleWhen = nameof(SlowMoRiposteShown), Hint = "How long the slow motion lasts once the riposte's blow has landed.")]
    public double SlowMoRiposteAfter { get; set; } = 0.2;

    [Setting("Auto-block from this many attackers", Min = 0, Max = 8, Step = 1,
        Hint = "With at least this many enemies on you, your held guard turns to each swing by itself (you still hold block). "
            + "0: never.")]
    public int AutoBlockAttackers { get; set; } = 3;

    [Setting("Hide the mouse pointer in fights",
        Hint = "In PZA's over-the-shoulder and first-person views the game's pointer sits on the crosshair; in a fight it is hidden. "
            + "Hold Alt (or open a window) and it shows again.")]
    public bool HidePointerInFights { get; set; } = true;

    [Setting("Outline your target", Hint = "Kenshi's hover outline, in red, on the enemy you are locked on to in a fight.")]
    public bool OutlineTarget { get; set; } = true;

    [Setting("Skip the initiative roll",
        Hint = "Kenshi rolls dice between two fighters for who may swing first. On: your press always gets its swing, "
            + "unless the enemy's swing at you is already on its way (then block!). Off: the dice decide, as in the game.")]
    public bool SkipInitiative { get; set; } = true;

    [Setting("On-screen messages", Hint = "Show 'Parry!', a target switch and similar briefly under the crosshair in a fight.")]
    public bool Messages { get; set; } = true;

    [Setting("Log details", SubGroup = "Debug",
        Hint = "For testing and bug reports: write each manual attack, block, parry, block roll and hit taken to log.txt. "
            + "Leave it off to play: off, none of these lines is even put together.")]
    public bool LogDetails { get; set; } = false;
}
