// Manual Combat for Meitou. GPLv3 or later.
// The world-side state: which character the player fights with by hand, its pending attack, block, dodge and footwork, and its target choice.
// Registered right after the combat system (SystemAnchor.AfterPart(Combat)), so its Schedule runs after combat's Schedule has copied the fight
// states for this tick and may adjust them before anyone acts. The patches (CombatPatches) only read this state while the tick runs in parallel;
// it is written in serial steps (Inputs, Schedule, and the serial Apply step for parries).
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace ManualCombat;

public sealed class ManualCombatSystem(CombatSystem combat) : ITickSystem, IStateHashed, ICombatHitFilter
{
    /// <summary>Each world's system, for the patches (which get the world, not the system).</summary>
    static readonly ConditionalWeakTable<World, ManualCombatSystem> s_byWorld = new();
    /// <summary>The last world that ran Inputs: the quick path of <see cref="For"/>.</summary>
    static volatile ManualCombatSystem? s_last;

    World? _world;

    /// <summary>The combat system of this world.</summary>
    public CombatSystem Combat { get; } = combat;

    // ---- The hand-fought character (none: Slot -1). ----
    public int Slot { get; private set; } = -1;
    public int Generation { get; private set; }

    // ---- Attack: valid while the fighter's AttackSeq is still AttackSeq (a swing it starts raises it) and the tick is not past AttackUntil. ----
    public int AttackDir { get; private set; }
    public int AttackSeq { get; private set; } = -1;
    public long AttackUntil { get; private set; } = -1;
    bool _attackNew, _attackReported;
    Vector2 _attackLook;
    bool _fireNew;
    long _retargetTick = long.MinValue;
    /// <summary>When the attack was pressed, and the last tick its aimed side was out of reach (the character steps in meanwhile).</summary>
    long _attackPressTick, _stepInTick = long.MinValue;
    /// <summary>How long a press may keep stepping in for its aimed swing.</summary>
    const float StepInSeconds = 1.5f;

    // ---- Hold to ready: the attack button held down; the swing it started (its AttackSeq) waits at the ready point until it is let go. ----
    public bool AttackHeld { get; private set; }
    int _heldSwingSeq = -1;
    /// <summary>The held swing is waiting at its ready point now (as of this tick's Schedule).</summary>
    bool _readied;
    bool _holdToReady;
    float _readyPoint;
    /// <summary>The held swing's wind-up speed-up, the swing it was given to (undone on release), and a side switch asked for / just made.</summary>
    float _heldWindup = 1;
    int _fastWindupSeq = -1;
    bool _heldSideSwitch, _reaimed, _switched;
    /// <summary>The held attack was let go in this tick's inputs (a side switch in the same moment still counts).</summary>
    bool _releasedNow;
    /// <summary>Walking while holding a swing: on (setting), its speed share, and whether it applies this tick (the upper-body swing plays).</summary>
    bool _walkReadied;
    float _readiedWalkSpeed = 0.5f;
    public bool UpperSwing { get; private set; }
    /// <summary>The held swing waits at its ready point now (enemies may swing into it: HasInitiative).</summary>
    public bool HeldReady => _heldActive && _readySince >= 0 && CurrentTick - _readySince >= _guardBreakTicks;
    /// <summary>A held swing (winding up or readied) is under way at the last Schedule.</summary>
    bool _heldActive;
    /// <summary>The tick the swing came to the ready point (-1: not readied), and how long enemies guard against it before taking their turn.</summary>
    int _readySince = -1, _guardBreakTicks = 30;
    public float ReadiedWalkSpeed => _readiedWalkSpeed;
    /// <summary>The footwork speed share with the crossbow out (aiming or reloading).</summary>
    public float CrossbowWalkSpeed => _crossbowWalkSpeed;
    float _crossbowWalkSpeed = 0.35f;
    /// <summary>The footwork speed share while the reload runs in a fight.</summary>
    public float ReloadWalkSpeed => _reloadWalkSpeed;
    float _reloadWalkSpeed = 0.2f;

    // ---- Cut the recovery. ----
    bool _cutRecovery;

    // ---- Blocking turns the character to the attacker (and makes it the target). ----
    public bool TurnToBlocked { get; private set; }
    /// <summary>The attacker the game's defence update last met with a held guard (-1: none since the last Schedule).</summary>
    int _blockedFrom = -1;

    // ---- Animals: per enemy slot, the AttackSeq of its swing at the character that started with a slowed wind-up (-1: none). ----
    int[] _animalSlowSeq = [];
    /// <summary>Per enemy slot: since when it has guarded against the held attack (-1: not guarding against it).</summary>
    int[] _guardSince = [];
    /// <summary>Per enemy slot: when it was last let off its guard against the held attack; whether any of these is set.</summary>
    int[] _guardFreed = [];
    bool _guardHeldSeen;
    public float AnimalWindup { get; private set; } = 1;

    // ---- Block. ----
    public bool BlockHeld { get; private set; }
    public int BlockDir { get; private set; }
    /// <summary>The tick the block button went down (long.MinValue once its parry chance was used).</summary>
    public long BlockPressTick { get; private set; } = long.MinValue;
    /// <summary>The block was pressed while the game was paused: no parry from that press.</summary>
    public bool BlockPressPaused { get; private set; }
    bool _attackPaused;
    /// <summary>The tick the block button went down (kept for the guard pose's raise).</summary>
    public long BlockDownTick { get; private set; }
    bool _blockDown, _blockUp, _blockTurned;

    // ---- Dodge: the window a dodge press holds, its move and that move's length. ----
    public long DodgeTick { get; private set; } = long.MinValue;
    public int DodgeTicks { get; private set; }
    /// <summary>The dodge move shown with no swing coming (an index in the combat system's techniques; -1 none).</summary>
    public int DodgeShown { get; private set; } = -1;
    /// <summary>Set once a real dodge (one meeting a swing) played for the press: the shown one is not replayed after it.</summary>
    bool _dodgeNew;

    // ---- Feints and the lock after a blow. ----
    bool _swinging;
    long _swingEndedTick = long.MinValue / 2;
    /// <summary>Until this tick no attack starts (after a feint).</summary>
    public long FeintLockUntil { get; private set; } = long.MinValue;

    // ---- Combos. ----
    int _chain, _lastSwingTag, _pendingLevel, _lastSwingAim;
    /// <summary>The player's last hit: its tick and the AttackSeq of the swing that landed it (a combo press must come after it).</summary>
    long _hitTick = long.MinValue / 2;
    int _hitSwingSeq = -1;
    /// <summary>The player's last swing landed a hit (combos chain only from a hit).</summary>
    bool _lastSwingHit;
    const int MaxChain = 1;   // combo x2 at most: one cut-in
    long _riposteUntil = long.MinValue;
    /// <summary>The play-rate factor of the swing the player asked for (combos), used by the PlayRate patch as it starts.</summary>
    public float NextSwingRate { get; private set; } = 1;

    // ---- Hyper armor. ----
    public bool HyperArmorOn { get; private set; }
    float _poiseScale;
    int _poise, _poiseRefillTicks, _graceTicks;
    long _poiseHitTick = long.MinValue / 2, _staggerGraceUntil = long.MinValue / 2;
    bool _clashCrush;
    float _crushScale;

    bool _armorSwingOnly;

    static int PoiseMax(CharacterCold dc, float scale)
    {
        float toughness = CombatPatches.Stat(dc, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Toughness);
        return Math.Max(0, (int)MathF.Floor(scale * toughness / 40f));
    }

    /// <summary>
    /// A hit about to stagger the character (serial, Apply): shrugged off in the grace after a stagger, or paid from poise (a heavy hit 2, a light
    /// one 1). Poise refills to full after the refill time without a hit paid.
    /// </summary>
    public bool ResistStagger(CharacterCold dc, bool heavy, int now, bool swinging)
    {
        if (now < _staggerGraceUntil)
        {
            Detail("stagger shrugged off (grace after a stagger)");
            return true;
        }
        if (_armorSwingOnly && !swinging) return false;   // poise only holds a swing
        int max = PoiseMax(dc, _poiseScale);
        if (now - _poiseHitTick >= _poiseRefillTicks) _poise = max;
        _poise = Math.Min(_poise, max);
        int cost = heavy ? 2 : 1;
        if (_poise < cost)
        {
            Detail($"staggered ({(heavy ? "heavy" : "light")} hit, poise {_poise}/{max})");
            return false;
        }
        _poise -= cost;
        _poiseHitTick = now;
        Detail($"stagger shrugged off ({(heavy ? "heavy" : "light")} hit, poise left {_poise}/{max})");
        ManualCombatMod.Notify("Held firm");
        return true;
    }

    /// <summary>A stagger went through: the grace runs from its end, and poise is full again after it.</summary>
    public void Staggered(int stunUntil, int now)
    {
        if (stunUntil <= now) return;
        _staggerGraceUntil = stunUntil + _graceTicks;
        _poiseHitTick = long.MinValue / 2;   // full again on the next hit
    }

    // ---- Counters. ----
    int _clashes, _chambers;

    // ---- Crossbow. ----
    /// <summary>0: Kenshi chooses; 1: melee (the swap key); 2: crossbow.</summary>
    public int WeaponMode { get; private set; }
    long _fireUntil = long.MinValue;
    /// <summary>The crossbow reload is held (left click on an empty crossbow, Mouse 4 out of fights, the reload key).</summary>
    public bool ReloadHeld { get; private set; }
    /// <summary>The reload counted on at the last Schedule (held, standing, empty): when it stops early, the checkpoint applies.</summary>
    bool _reloadRunning;
    /// <summary>The share of a reload that is kept once reached when the reload stops early (the rest of an unfinished reload is lost).</summary>
    const float ReloadCheckpoint = 1f / 3;
    /// <summary>The reload counts on now: held, and the character is not stepping (W A S D).</summary>
    public bool ReloadGoes => ReloadHeld && (MoveIntent == Vector2.Zero || _reloadWhileWalking);   // walking: in fights at the reload walking speed
    bool _reloadWhileWalking = true, _fightNow;
    int _lastLoaded;
    bool _emptyTold, _rangeTold;
    /// <summary>The crosshair ray of the last fire press (the shot goes along it), and whether there is one.</summary>
    public Vector3 ShotOrigin { get; private set; }
    public Vector3 ShotDirection { get; private set; }
    public bool HasShotRay { get; private set; }
    /// <summary>Right click held with the crossbow out: the aim steadies (0 to 1).</summary>
    bool _steadyHeld;
    float _steadyLevel;
    /// <summary>The reticle's spread now: the half-angle in degrees a shot may stray from the crosshair.</summary>
    public float ShotSpread { get; private set; }
    /// <summary>Whether a fire press is waiting for the shot.</summary>
    public bool FireAsked(long tick) => tick <= _fireUntil;
    /// <summary>The character uses the crossbow now (the last finished tick).</summary>
    bool Ranged => Slot >= 0 && CombatPatches.ReadSlots(Combat) is var r && Slot < r.Length && r[Slot].Ranged;

    /// <summary>The enemies swinging at the character at the last Schedule (auto-block).</summary>
    public int Attackers { get; private set; }

    // ---- Footwork: the W A S D direction in the world (X, Z), zero to stand. ----
    public Vector2 MoveIntent { get; private set; }

    // ---- Target and leaving. ----
    Vector2? _targetLook;
    long _leaveUntil = long.MinValue;
    bool _leaveNew;

    // ---- Settings, copied each tick in Inputs. ----
    public bool Mirror { get; private set; }
    public bool ParryOn { get; private set; }
    public int ParryTicks { get; private set; }
    public bool LogDetails { get; private set; }
    public bool SkipInitiative { get; private set; }
    public bool Footwork { get; private set; }
    public bool AutoFootwork { get; private set; }
    public float FootworkSpeed { get; private set; }
    public bool GuardPoseOn { get; private set; }
    public bool AutoRetarget { get; private set; }
    public bool CancelSwing { get; private set; }
    public bool SwingAnyway { get; private set; }
    public bool MirrorAttacks { get; private set; }
    public float FeintWindow { get; private set; }
    public int AutoBlockAt { get; private set; }
    public bool ClashOn { get; private set; }
    /// <summary>Enemies are not kept waiting on the player's turn (see the HasInitiative patch); the short yield they keep, in ticks.</summary>
    public bool EnemiesDontWait { get; private set; }
    public int YieldTicks { get; private set; }
    /// <summary>The longest an enemy keeps losing the initiative roll before it swings anyway, ticks (0: no limit).</summary>
    int _longestWaitTicks;
    /// <summary>Per enemy slot: the tick its run of lost rolls against the player began, and its last lost roll (-1: not waiting).</summary>
    int[] _waitSince = [], _waitLast = [];
    /// <summary>A gap this long between lost rolls starts a new wait (a roll comes about every half second while it waits).</summary>
    const int WaitGapTicks = 60;

    /// <summary>
    /// An enemy (slot <paramref name="i"/>) lost its initiative roll against the player: whether it has kept losing for the longest wait. Called
    /// from the combat step, which runs fighters in parallel: it writes only enemy <paramref name="i"/>'s entries.
    /// </summary>
    public bool WaitedTooLong(int i, int now)
    {
        if (_longestWaitTicks <= 0 || (uint)i >= (uint)_waitSince.Length) return false;
        if (_waitSince[i] < 0 || now - _waitLast[i] > WaitGapTicks) _waitSince[i] = now;
        _waitLast[i] = now;
        return now - _waitSince[i] >= _longestWaitTicks;
    }

    /// <summary>Enemy <paramref name="i"/> swings at the player: its wait is over.</summary>
    public void WaitOver(int i)
    {
        if ((uint)i < (uint)_waitSince.Length) _waitSince[i] = -1;
    }

    /// <summary>The wait arrays cover every fighter slot (grown in the serial Schedule, never during the parallel combat step).</summary>
    void SizeWaits(int count)
    {
        if (_waitSince.Length >= count) return;
        int old = _waitSince.Length, size = Math.Max(count, old * 2);
        Array.Resize(ref _waitSince, size);
        Array.Resize(ref _waitLast, size);
        Array.Resize(ref _animalSlowSeq, size);
        Array.Fill(_waitSince, -1, old, size - old);
        Array.Fill(_waitLast, -1, old, size - old);
        Array.Fill(_animalSlowSeq, -1, old, size - old);
        Array.Resize(ref _guardSince, size);
        Array.Fill(_guardSince, int.MinValue / 2, old, size - old);
        Array.Resize(ref _guardFreed, size);
        Array.Fill(_guardFreed, int.MinValue / 2, old, size - old);
        Array.Resize(ref _defenceSeq, size);
        Array.Fill(_defenceSeq, -1, old, size - old);
        Array.Resize(ref _defenceOk, size);
    }

    /// <summary>
    /// A swing in progress plays on at <paramref name="speed"/> times its current speed from <paramref name="now"/>: its clip length scales, and
    /// its start and end move so that its progress now is kept (the animation does not jump). Done in Schedule, before the combat step.
    /// </summary>
    static void Rescale(ref CombatSlot a, int now, float speed)
    {
        if (a.AttackTicks <= 0 || speed <= 0) return;
        int old = a.AttackTicks;
        int ticks = Math.Max(2, (int)MathF.Round(old / speed));
        float at = (now - a.AttackStart) / (float)old, end = (a.AttackEnd - a.AttackStart) / (float)old;
        a.AttackTicks = ticks;
        a.AttackStart = now - (int)MathF.Round(at * ticks);
        a.AttackEnd = Math.Max(now + 1, a.AttackStart + (int)MathF.Ceiling(end * ticks));
    }

    /// <summary>The PlayRate patch gave the player's swing <paramref name="seq"/> its combo speed (when the JIT has not inlined PlayRate).</summary>
    public void RateApplied(int seq) => _rateAppliedSeq = seq;
    int _rateAppliedSeq = -1;
    /// <summary>The riposte swing in progress (its AttackSeq; -1 none), for its slow motion.</summary>
    int _riposteSwingSeq = -1;

    /// <summary>
    /// The animals' swings at the character: one that started in the last combat step plays its wind-up at the Animal wind-up speed; once its
    /// first blow has struck (or the swing is over), the rest plays at the normal speed again. Both changes keep the clip's progress (Rescale).
    /// </summary>
    void AnimalSwings(World world, CombatSlot[] slots, int now)
    {
        var prev = world.Characters.Previous;
        for (int c = 0; c < _animalSlowSeq.Length && c < slots.Length && c < prev.Length; c++)
        {
            ref var a = ref slots[c];
            int seq = _animalSlowSeq[c];
            if (seq < 0)
            {
                // A new swing at the character (started in the last step), an animal's, still in its wind-up: slowed.
                if (AnimalWindup >= 1 || c == Slot || a.AttackTech < 0 || a.NextBlow != 1 || a.AttackStart != now - 1 || a.AttackTicks <= 0
                    || a.TargetSlot != Slot || a.TargetGeneration != Generation || !prev[c].Alive) continue;
                if (world.Characters.Cold(c) is not { } ac || !(ac.Fighter is { IsAnimal: true } || Meitou.Simulation.Bodies.CharacterFacts.Animal(ac)))
                    continue;
                int before = a.AttackTicks;
                Rescale(ref a, now, AnimalWindup);
                _animalSlowSeq[c] = a.AttackSeq;
                Detail($"animal swing (slot {c}): wind-up slowed, clip {before} -> {a.AttackTicks} ticks");
                continue;
            }
            if (a.AttackSeq != seq || a.AttackTech < 0 || a.AttackTicks <= 0)
            {
                _animalSlowSeq[c] = -1;
                continue;
            }
            // Still winding up at the character: stays slow. Its first blow struck, or it turned on someone else: full speed from here.
            if (a.NextBlow == 1 && a.TargetSlot == Slot && a.TargetGeneration == Generation) continue;
            _animalSlowSeq[c] = -1;
            int slow = a.AttackTicks;
            Rescale(ref a, now, 1 / AnimalWindup);
            Detail($"animal swing (slot {c}): {(a.NextBlow == 1 ? "turned on someone else" : "first blow struck")}, back to full speed (clip {slow} -> {a.AttackTicks} ticks)");
        }
    }

    /// <summary>Every animal swing still slowed goes back to full speed at once (manual combat let go of the character).</summary>
    void RestoreAnimalSwings(int now)
    {
        var slots = CombatPatches.WriteSlots(Combat);
        for (int c = 0; c < _animalSlowSeq.Length; c++)
        {
            int seq = _animalSlowSeq[c];
            if (seq < 0) continue;
            _animalSlowSeq[c] = -1;
            if (c >= slots.Length) continue;
            ref var a = ref slots[c];
            if (a.AttackSeq == seq && a.AttackTech >= 0 && a.AttackTicks > 0 && AnimalWindup > 0) Rescale(ref a, now, 1 / AnimalWindup);
        }
    }

    /// <summary>Per enemy slot: the character's swing (AttackSeq) its first defence decision was against, and whether that decision defended.</summary>
    int[] _defenceSeq = [];
    bool[] _defenceOk = [];

    /// <summary>
    /// Enemy <paramref name="i"/> decided its defence against the character's swing <paramref name="seq"/> (<paramref name="defends"/>: a defence
    /// was picked). The first decision per swing is kept; returns whether this one may stand (false: the first was lost, so none now). From the
    /// combat step, which runs fighters in parallel: only enemy <paramref name="i"/>'s entries are written (sized in the serial Schedule).
    /// </summary>
    /// <summary>
    /// The key an enemy's first defence decision is kept under: the swing, and whether it is still held (0) or let go (1). A side switch is a new
    /// swing and letting it go a new phase, so each gets a fresh roll; only re-rolls while the same swing is held are refused.
    /// </summary>
    public int DefenceKey(int seq) => seq * 2 + (AttackHeld && seq == _heldSwingSeq ? 0 : 1);

    public bool FirstDefence(int i, int seq, bool defends)
    {
        if ((uint)i >= (uint)_defenceSeq.Length) return true;
        if (_defenceSeq[i] != seq)
        {
            _defenceSeq[i] = seq;
            _defenceOk[i] = defends;
            return true;
        }
        return _defenceOk[i] || !defends;
    }

    /// <summary>The game's defence update raised the held guard against <paramref name="attacker"/>'s swing (the character's own combat step).</summary>
    public void BlockingFrom(int attacker) => _blockedFrom = attacker;
    public bool ChamberOn { get; private set; }
    public bool ManualCrossbow { get; private set; }
    /// <summary>The chance a held block is beaten, as a share of Kenshi's own (Block beaten chance setting).</summary>
    float _beatenScale;
    /// <summary>The last blow the block roll answered (attacker, attack sequence, blow) and its answer: one roll per blow.</summary>
    int _rollAttacker = -1, _rollSeq, _rollBlow;
    bool _rollHolds;
    bool _combos;
    float _riposteSpeed, _chainSpeed, _spreadScale, _steadyMin, _steadySeconds;
    int _comboFromTicks, _comboToTicks, _comboRestTicks;
    public bool CounterSounds { get; private set; }
    int _bufferTicks, _recoveryTicks, _feintLockoutTicks, _clashTicks, _chamberTicks;

    /// <summary>
    /// The Kenshi blow code an aimed swing asks for. Kenshi's left and right name the side the blow lands on (the defender's), so a swing that
    /// starts at your right, as Mount & Blade's right attack, is Kenshi's "left" (Mirror attack sides, on by default).
    /// </summary>
    public int AttackTag(int aim) => !MirrorAttacks ? aim : aim switch { Dir.Left => Dir.Right, Dir.Right => Dir.Left, _ => aim };

    /// <summary>Parries so far in this world (hashed, and in the log).</summary>
    public int Parries { get; private set; }

    /// <summary>This world's system, or null (the mod not running in it).</summary>
    public static ManualCombatSystem? For(World world)
    {
        var last = s_last;
        if (last is not null && ReferenceEquals(last._world, world)) return last;
        return s_byWorld.TryGetValue(world, out var found) ? found : null;
    }

    /// <summary>The system of the world whose combat system is <paramref name="combat"/> (for the patches that get no world), or null.</summary>
    public static ManualCombatSystem? ForCombat(CombatSystem combat)
    {
        var last = s_last;
        if (last is not null && ReferenceEquals(last.Combat, combat)) return last;
        foreach (var (_, system) in s_byWorld) if (ReferenceEquals(system.Combat, combat)) return system;
        return null;
    }

    /// <summary>Whether slot <paramref name="i"/> is the hand-fought character.</summary>
    public bool Controls(World world, int i)
    {
        if (i != Slot || Slot < 0) return false;
        var prev = world.Characters.Previous;
        return i < prev.Length && prev[i].Alive && prev[i].Generation == Generation;
    }

    /// <summary>The aimed swing is out of reach at <paramref name="now"/> (from the Act step, for this character only).</summary>
    public void StepIn(long now) => _stepInTick = now;

    /// <summary>Whether the character steps in for an aimed swing waiting for reach.</summary>
    public bool SteppingIn(long tick) => AttackSeq >= 0 && tick - _stepInTick <= 2 && tick <= AttackUntil;

    long _noSideTold = long.MinValue;

    /// <summary>The weapon has no swing at all from the aimed side, so another side's swing played: said once per press.</summary>
    public void NoSwingFromSide(int tag)
    {
        if (_noSideTold == _attackPressTick) return;
        _noSideTold = _attackPressTick;
        Detail($"no {Dir.Name(tag)} swing with this weapon: another side's swing instead");
        ManualCombatMod.Notify("No Attack For That Direction");
    }

    /// <summary>The world's tick (for the patches that get none).</summary>
    public long CurrentTick => _world?.Tick ?? 0;

    /// <summary>Whether a dodge press is still in its window at <paramref name="now"/>.</summary>
    public bool Dodging(long now) => DodgeTick != long.MinValue && now - DodgeTick <= DodgeTicks;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        if (!ReferenceEquals(_world, world))
        {
            _world = world;
            s_byWorld.AddOrUpdate(world, this);
        }
        s_last = this;
        CopySettings(world);

        foreach (var command in commands)
        {
            switch (command)
            {
                case ManualControl c:
                    if (c.On && IsAnimal(world, c.Character))
                    {
                        // Animals fight with their own attacks and never block or dodge: no manual combat for them, Kenshi's own fighting.
                        if (Slot >= 0) Release("an animal selected");
                        ManualCombatMod.Log($"no manual combat for character {c.Character}: an animal");
                        ManualCombatMod.Notify("Manual combat: not for animals");
                    }
                    else if (c.On) Bind(c.Character);
                    else if (Is(c.Character)) Release("turned off");
                    break;
                case ManualShot shot when Is(shot.Character) && ManualCrossbow && Ranged:
                    if (Finite(shot.Origin) && Finite(shot.Direction) && shot.Direction.LengthSquared() > 0.5f)
                    {
                        ShotOrigin = shot.Origin;
                        ShotDirection = Vector3.Normalize(shot.Direction);
                        HasShotRay = true;
                        var flat = new Vector2(ShotDirection.X, ShotDirection.Z);
                        FirePress(world, flat.LengthSquared() > 1e-4f ? Vector2.Normalize(flat) : Vector2.Zero);
                    }
                    break;
                case ManualAttack a when Is(a.Character) && ManualCrossbow && Ranged:
                    HasShotRay = false;   // no crosshair ray: Kenshi's own aim at the target
                    FirePress(world, float.IsFinite(a.Look.X) && float.IsFinite(a.Look.Y) ? a.Look : Vector2.Zero);
                    break;
                case ManualBlock b when Is(b.Character) && ManualCrossbow && Ranged:
                    _steadyHeld = b.Held;   // right click with the crossbow out steadies the aim
                    break;
                case ManualReload r when Is(r.Character) && ManualCrossbow:
                    if (r.Held != ReloadHeld) Detail(r.Held ? "reload held" : "reload let go");
                    ReloadHeld = r.Held;
                    break;
                case ManualSwap w when Is(w.Character):
                    WeaponMode = Ranged ? 1 : 2;
                    Detail(WeaponMode == 2 ? "swap: crossbow" : "swap: melee");
                    ManualCombatMod.Notify(WeaponMode == 2 ? "Crossbow" : "Melee");
                    break;
                case ManualAttack a when Is(a.Character):
                    AttackDir = a.Direction;
                    _attackLook = float.IsFinite(a.Look.X) && float.IsFinite(a.Look.Y) ? a.Look : Vector2.Zero;
                    AttackUntil = world.Tick + _bufferTicks;
                    _attackPressTick = world.Tick;
                    _attackPaused = a.InPause;
                    _attackNew = true;
                    AttackHeld = a.Held && _holdToReady;
                    Detail($"attack pressed, direction {Dir.Name(a.Direction)}{(AttackHeld ? " (held)" : "")}");
                    break;
                case ManualAttackAim m when Is(m.Character):
                    if (!AttackHeld || m.Direction == Dir.None || m.Direction == AttackDir) break;
                    AttackDir = m.Direction;
                    _reaimed = true;
                    Detail($"held attack aimed {Dir.Name(m.Direction)}");
                    break;
                case ManualAttackRelease r when Is(r.Character):
                    if (!AttackHeld) break;
                    AttackHeld = false;
                    _releasedNow = true;
                    // Letting go is when the held attack is unleashed: a chamber is timed from it (readied, still winding up, or switched to the
                    // blow's side while held), never from the press long before. Switching sides alone chambers nothing: the release must come
                    // within the chamber window before the blow lands.
                    _attackPressTick = world.Tick;
                    _attackPaused = r.InPause;
                    if (_readied) Detail("attack let go: the readied swing strikes");
                    else
                    {
                        Detail("attack let go (the swing was not readied yet)");
                        if (AttackSeq >= 0) AttackUntil = Math.Max(AttackUntil, world.Tick + _bufferTicks);   // not started yet: a press as any
                    }
                    break;
                case ManualBlock b when Is(b.Character):
                    if (b.Held && !BlockHeld)
                    {
                        BlockPressTick = world.Tick;
                        BlockPressPaused = b.InPause;
                        if (b.InPause) Detail("block pressed while paused: no parry from it");
                        BlockDownTick = world.Tick;
                        _blockDown = true;
                        Detail($"block down, guard {Dir.Name(b.Direction)}");
                    }
                    else if (!b.Held && BlockHeld)
                    {
                        _blockUp = true;
                        Detail("block up");
                    }
                    else if (b.Held && b.Direction != BlockDir)
                    {
                        _blockTurned = true;
                    }
                    BlockHeld = b.Held;
                    BlockDir = b.Direction;
                    break;
                case ManualMove m when Is(m.Character):
                    var d = m.Direction;
                    MoveIntent = float.IsFinite(d.X) && float.IsFinite(d.Y) && d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
                    break;
                case ManualDodge g when Is(g.Character):
                    // One dodge at a time: a press during the last one's window is ignored.
                    if (!Dodging(world.Tick))
                    {
                        _dodgeNew = true;
                        _dodgePaused = g.InPause;
                    }
                    break;
                case ManualTarget t when Is(t.Character):
                    _targetLook = float.IsFinite(t.Look.X) && float.IsFinite(t.Look.Y) ? t.Look : Vector2.Zero;
                    break;
                case ManualLeave l when Is(l.Character):
                    _leaveNew = true;
                    break;
            }
        }
    }

    public void Schedule(World world)
    {
        long tick = world.Tick;
        bool leaving = tick < _leaveUntil;
        if (Slot < 0)
        {
            RestoreAnimalSwings((int)tick);   // released mid-swing: no animal keeps a slowed wind-up
            FightHold.Publish(world.Commands, -1, 0, false, false);
            Hud.Publish(false, 0, CharacterId.None, false, 0);
            return;
        }
        var prev = world.Characters.Previous;
        if (Slot >= prev.Length || !prev[Slot].Alive || prev[Slot].Generation != Generation)
        {
            Release("character gone");
            return;
        }

        var slots = CombatPatches.WriteSlots(Combat);
        if (Slot >= slots.Length) return;
        SizeWaits(Math.Max(slots.Length, prev.Length));
        SkillWindows(world);
        ref var s = ref slots[Slot];
        int now = (int)tick;

        // Leave the fight: a stop (which ends a fight), let through FightHold, and a short while in which W A S D walk, so the player can run.
        if (_leaveNew)
        {
            _leaveNew = false;
            _leaveUntil = tick + (long)MathF.Ceiling(2f / Dt(world));
            leaving = true;
            AttackSeq = -1;
            BlockHeld = false;
            world.Commands.Enqueue(new StopCommand([new CharacterId(Slot, Generation)]) { Tick = tick + 1 });
            Detail("leaving the fight");
            ManualCombatMod.Notify("Leaving the fight - run!");
        }
        else if (leaving)
        {
            // Still leaving while the player runs (W A S D): an enemy chasing and hitting would otherwise pull the character back into a fight as
            // soon as the leave ran out (Kenshi fights back when attacked), and the hold would then stop the run. It ends a second after the keys
            // stop, or at once with an attack or a block.
            if (_attackNew || _blockDown)
            {
                _leaveUntil = long.MinValue;
                leaving = false;
                Detail("leaving given up (attack or block)");
            }
            else if (MoveIntent != Vector2.Zero) _leaveUntil = Math.Max(_leaveUntil, tick + (long)MathF.Ceiling(1f / Dt(world)));
        }

        bool fighting = s.TargetSlot >= 0;
        _fightNow = fighting && !leaving;
        FightHold.Publish(world.Commands, Slot, Generation, fighting && !leaving, leaving, tick, Dt(world));
        Hud.Publish(fighting && !leaving, IncomingSide(world, now, fighting),
            fighting ? new CharacterId(s.TargetSlot, s.TargetGeneration) : CharacterId.None, s.Ranged, ShotSpread,
            s.Loaded, world.Characters.Cold(Slot)?.Fighter?.Crossbow?.NumShots ?? 0, _steadyLevel,
            s.Loaded > 0 ? 1 : s.ReloadTime > 0 && s.ReloadLeft > 0 ? 1 - s.ReloadLeft / s.ReloadTime : 0, _reloadRunning, _incomingAnimal);

        // The target button: the enemy nearest the camera's aim.
        if (_targetLook is { } look)
        {
            _targetLook = null;
            if (!fighting) Detail("target button: not in a fight");
            else if (Targets.Pick(world, Combat, Slot, look, exclude: s.TargetSlot, reachOnly: false) is { } pick) Retarget(world, ref s, pick, "switched");
            else ManualCombatMod.Notify("No other enemy to switch to");
        }

        // An attack request is good until the fighter starts a swing (its AttackSeq goes up) or the buffer runs out.
        // A perfect dodge (fists): told once its blow has gone by.
        if (!_counterTold && tick >= _counterFrom && tick <= _counterUntil)
        {
            _counterTold = true;
            ManualCombatMod.Notify("Perfect dodge!");
            SlowMo.Moment(SlowMo.Kind.PerfectDodge);
        }

        // The animals' swings at the character: full speed again after their first blow (Animal wind-up speed).
        AnimalSwings(world, slots, now);

        // Blocking turns the character to the attacker: one met by a held guard (the game's defence update) becomes the target.
        if (_blockedFrom >= 0)
        {
            int from = _blockedFrom;
            _blockedFrom = -1;
            if (TurnToBlocked && fighting && from != s.TargetSlot && from < prev.Length && prev[from].Alive && BlockHeld)
                Retarget(world, ref s, from, "blocked");
        }

        if (_attackNew)
        {
            _attackNew = false;
            AttackSeq = s.AttackSeq;
            _attackReported = false;


            // Attack what you look at: an enemy in reach near the crosshair becomes the target (the current one too, so it stays).
            if (AutoRetarget && fighting && _attackLook != Vector2.Zero
                && Targets.Pick(world, Combat, Slot, _attackLook, exclude: -1, reachOnly: true, cone: Targets.CrosshairCone) is { } aimed
                && aimed != s.TargetSlot)
                Retarget(world, ref s, aimed, "at the crosshair");

            // A combo: pressed from another side within the combo timing after the swing that hit. That swing is cut where it is (the rest of
            // its follow-through and further blows dropped), the pause after it skipped, and the new swing starts at once, faster.
            _pendingLevel = 0;
            long sinceHit = tick - _hitTick;
            if (_combos && fighting && !_attackPaused && _chain < MaxChain && s.AttackSeq == _hitSwingSeq && sinceHit >= _comboFromTicks && sinceHit <= _comboToTicks
                && AttackDir != Dir.None && AttackDir != _lastSwingAim)
            {
                if (s.AttackTech >= 0)
                {
                    s.AttackTech = -1;
                    s.NextBlow = 0;
                    s.CancelTick = now;
                }
                s.ReadyTick = Math.Min(s.ReadyTick, now);
                s.PauseUntil = Math.Min(s.PauseUntil, now);
                _pendingLevel = Math.Min(_chain + 1, MaxChain);
                _hitSwingSeq = -1;   // one combo per hit
                Detail($"combo: cut into a {Dir.Name(AttackDir)} swing {sinceHit} ticks after the hit");
            }
            else if (_combos && s.AttackSeq == _hitSwingSeq)
                Detail($"no combo: pressed {sinceHit} ticks after the hit{(AttackDir == _lastSwingAim ? " from the same side" : "")} (timing {_comboFromTicks} to {_comboToTicks})");
        }
        else if (AttackSeq >= 0 && s.AttackSeq != AttackSeq)
        {
            // Used: a swing started. The combo it made is now the chain. Held down, it waits at the ready point (hold to ready).
            AttackSeq = -1;
            _heldSwingSeq = AttackHeld ? s.AttackSeq : -1;
            // Its combo or riposte speed, when the PlayRate patch could not give it (the JIT inlined PlayRate): set now, the clip kept where it is.
            if (NextSwingRate > 1 && _rateAppliedSeq != s.AttackSeq && s.AttackTech >= 0)
            {
                Rescale(ref s, now, NextSwingRate);
                Detail($"swing sped up x{NextSwingRate:0.00} (clip now {s.AttackTicks} ticks)");
            }
            if (_pendingLevel > 0) ManualCombatMod.Notify($"Combo x{_pendingLevel + 1}");
            if (NextSwingRate > 1 && tick <= _riposteUntil)
            {
                _riposteSwingSeq = s.AttackSeq;
                SlowMo.Riposte(true);
                ManualCombatMod.Notify("Riposte");
                Detail($"riposte: the swing plays x{NextSwingRate:0.00}");
            }
            // A side switched while held (or as it was let go): the new swing starts part way into its wind-up (half way to the ready point), so the
            // change reads as a turn of the weapon rather than a new swing from nothing.
            if (_switched && s.AttackTech >= 0 && s.NextBlow == 1 && s.AttackTicks > 0 && CombatPatches.Technique(Combat, s.AttackTech) is { } turned)
            {
                int jump = (int)MathF.Floor(0.5f * _readyPoint * turned.StrikeProgress(1) * s.AttackTicks);
                s.AttackStart -= jump;
                s.AttackEnd -= jump;
            }
            if (AttackHeld && s.AttackTech >= 0 && s.NextBlow == 1 && s.AttackTicks > 0)
            {
                // Held: the wind-up into the ready pose plays faster (undone when let go).
                if (_heldWindup > 1)
                {
                    Rescale(ref s, now, _heldWindup);
                    _fastWindupSeq = s.AttackSeq;
                }
            }
            _switched = false;
            _chain = _pendingLevel;
            _pendingLevel = 0;
            _lastSwingTag = AttackTag(AttackDir);
            _lastSwingAim = AttackDir;
            _lastSwingHit = false;
            _riposteUntil = long.MinValue;
        }
        else if (AttackSeq >= 0 && tick - _stepInTick <= 2 && now <= AttackUntil)
        {
            // Stepping in for the aimed swing: the press holds on while the character closes in (up to StepInSeconds).
            long limit = _attackPressTick + (long)MathF.Ceiling(StepInSeconds / Dt(world));
            AttackUntil = Math.Max(AttackUntil, Math.Min(tick + 2, limit));
        }
        else if (AttackSeq >= 0 && now > AttackUntil && !_attackReported)
        {
            _attackReported = true;
            Detail($"attack press ran out without a swing: {WhyNoSwing(in s, now)}");
            AttackSeq = -1;
        }

        // The riposte swing (slow motion through PZA's SlowMo): over once its first blow has landed or it ended.
        if (_riposteSwingSeq >= 0 && (s.AttackSeq != _riposteSwingSeq || s.AttackTech < 0 || s.NextBlow != 1))
        {
            _riposteSwingSeq = -1;
            SlowMo.Riposte(false);
        }

        // The held wind-up's speed-up ends when the button is let go (or the swing is over): the strike plays at its own speed.
        if (_fastWindupSeq >= 0 && (!AttackHeld || s.AttackSeq != _fastWindupSeq || s.AttackTech < 0 || s.NextBlow != 1))
        {
            if (s.AttackSeq == _fastWindupSeq && s.AttackTech >= 0 && s.AttackTicks > 0) Rescale(ref s, now, 1 / _heldWindup);
            _fastWindupSeq = -1;
        }

        // A side switch while held: the held swing is given up and a swing of the new side starts at once (no feint lockout), part way in.
        if (_reaimed)
        {
            _reaimed = false;
            // (Let go in the same moment as the switch: the switch still counts, and the new side strikes.)
            if (_heldSideSwitch && (AttackHeld || _releasedNow) && s.AttackTech >= 0 && s.AttackSeq == _heldSwingSeq && s.NextBlow == 1)
            {
                bool wasRiposte = _riposteSwingSeq == s.AttackSeq;
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.CancelTick = now;
                s.ReadyTick = Math.Min(s.ReadyTick, now);
                s.PauseUntil = Math.Min(s.PauseUntil, now);
                _heldSwingSeq = _fastWindupSeq = -1;
                AttackSeq = s.AttackSeq;
                AttackUntil = tick + _bufferTicks;
                _attackReported = false;
                _switched = true;
                if (wasRiposte) _riposteUntil = tick + _bufferTicks;   // the switched riposte is still a riposte
                Detail($"held swing switched to {Dir.Name(AttackDir)}");
            }
        }

        // Hold to ready: while the button is held, a press waits for its swing (no buffer running out), and the swing it started stops at the
        // ready point of its wind-up (its clip held there: the start and end move on with the clock) until the button is let go.
        if (AttackHeld && AttackSeq >= 0) AttackUntil = Math.Max(AttackUntil, tick + 1);
        _readied = false;
        if (AttackHeld && s.AttackTech >= 0 && s.AttackSeq == _heldSwingSeq && s.NextBlow == 1 && s.AttackTicks > 0
            && CombatPatches.Technique(Combat, s.AttackTech) is { } readying)
        {
            int strikes = s.AttackStart + (int)MathF.Ceiling(readying.StrikeProgress(1) * s.AttackTicks);
            int readyAt = s.AttackStart + (int)MathF.Floor(_readyPoint * (strikes - s.AttackStart));
            if (now >= readyAt && readyAt < strikes)
            {
                s.AttackStart++;
                s.AttackEnd++;
                _readied = true;
            }
        }
        // How long the attack has been held (since its held swing started; a side switch does not start it again, so switching back and forth cannot
        // keep the enemies off).
        _heldActive = AttackHeld && s.AttackTech >= 0 && s.AttackSeq == _heldSwingSeq && s.NextBlow == 1;
        if (!AttackHeld) _readySince = -1;
        else if (_heldActive && _readySince < 0) _readySince = now;

        // A held swing counts as a blow on its way only for a moment (Enemy guard against a held swing): after that an enemy guarding against it
        // lowers that guard to take its turn (HasInitiative lets it), as in Mount & Blade, where the AI blocks a held attack but still attacks you.
        // An enemy that cannot attack then (out of reach, no turn) guards again at once, so a held swing never opens a free hit. A side switch
        // starts a new wind-up the enemy must read again (Kenshi's own reaction and block roll), which is where timing pays.
        // Switching sides makes Kenshi end the enemy's guard (a tick with no swing) and start its wait after a block, then a new guard: so the
        // enemy's guarding is followed here (its last tick guarding against the held attack), and it is let off at most once per guard time.
        if (!AttackHeld)
        {
            if (_guardHeldSeen)
            {
                Array.Fill(_guardSince, int.MinValue / 2);
                Array.Fill(_guardFreed, int.MinValue / 2);
                _guardHeldSeen = false;
            }
        }
        else if (_heldActive)
        {
            _guardHeldSeen = true;
            bool over = now - _readySince >= _guardBreakTicks;
            var bodies = world.Characters.Previous;
            for (int c = 0; c < slots.Length && c < bodies.Length; c++)
            {
                if (c == Slot || !bodies[c].Alive) continue;
                ref var e = ref slots[c];
                if (e.TargetSlot != Slot || e.TargetGeneration != Generation) continue;
                // An enemy swinging at the character keeps its swing: Kenshi gives a swing up to defend once a blow at it is past half, and a held
                // swing waits near its blow, so every swing an enemy started was given up at once (it is no blow on its way any more).
                if (e.AttackTech >= 0 && e.NextBlow > 0)
                {
                    if (over) e.DefendReadyTick = Math.Max(e.DefendReadyTick, now + 2);
                    continue;
                }
                bool guarding = e.ReactTech >= 0 && e.ReactedSlot == Slot && CombatPatches.IsBlock(Combat, e.ReactTech);
                if (guarding) _guardSince[c] = now;   // its last tick guarding against the held attack
                if (!over || now - _guardFreed[c] < _guardBreakTicks) continue;
                // Guarding against it, or kept waiting by the end of such a guard (a switch): let off to take its turn. Its own pause after a swing
                // is left alone (only a guard's wait is cut), so it attacks no faster than in Kenshi.
                bool waiting = now - _guardSince[c] <= 3 && e.AttackTech < 0 && (now < e.ReadyTick || now < e.PauseUntil);
                if (!guarding && !waiting) continue;
                if (guarding) e.ReactTech = -1;
                e.ReadyTick = Math.Min(e.ReadyTick, now);
                e.PauseUntil = Math.Min(e.PauseUntil, now);
                _guardFreed[c] = now;
                Detail($"{world.Characters.Cold(c)?.Name} lowers its guard against the held swing to take its turn");
            }
        }

        // Walking while holding a swing: the held swing (winding up or readied) plays on the upper body only and W A S D step at the readied
        // walking speed (CombatPatches: the swing's upper-body copy, the footwork during a swing). Standing still, the whole-body swing plays.
        UpperSwing = _walkReadied && Footwork && AttackHeld && !s.Ranged && s.AttackTech >= 0 && s.AttackSeq == _heldSwingSeq && s.NextBlow == 1
            && MoveIntent != Vector2.Zero && UpperBody.Ready;

        // Combos: the speed of the swing waiting to start (riposte after a parry; a chain of swings from alternating sides).
        // After a combo (the last swing was a combo's cut-in), the next ordinary swing waits the combo rest after it ends: a combo is a burst,
        // not a stun-lock. Blocking and dodging are free meanwhile.
        if (_combos && AttackSeq >= 0 && _pendingLevel == 0 && _chain >= 1 && s.AttackTech < 0)
            FeintLockUntil = Math.Max(FeintLockUntil, _swingEndedTick + _comboRestTicks);

        NextSwingRate = 1;
        if (AttackSeq < 0) _pendingLevel = 0;
        if (_combos && AttackSeq >= 0)
        {
            NextSwingRate = 1 + _chainSpeed * _pendingLevel;
            if (tick <= _riposteUntil) NextSwingRate *= _riposteSpeed;
        }

        // Clashing: both swings meeting in the air.
        if (ClashOn && fighting) Clash(world, ref s, now);

        // The crossbow: the target at the crosshair for a shot, the shot taken, an empty one told about.
        if (ManualCrossbow) Crossbow(world, ref s, tick);

        // Auto-retarget: asked to swing at a target out of reach (or gone) while another enemy stands in reach in front.
        if (AutoRetarget && AttackSeq >= 0 && fighting && s.AttackTech < 0 && tick - _retargetTick >= 10
            && !Targets.InReach(world, Slot, in s, s.TargetSlot)
            && Targets.Pick(world, Combat, Slot, Targets.Facing(prev[Slot].Yaw), exclude: s.TargetSlot, reachOnly: true) is { } near)
        {
            _retargetTick = tick;
            Retarget(world, ref s, near, "in reach");
        }

        // Own swing ending: the start of the lock after a blow (RecoveryLock).
        if (s.AttackTech >= 0) _swinging = true;
        else if (_swinging)
        {
            _swinging = false;
            _swingEndedTick = tick;
            Detail($"own swing over: last blow {s.LastOutcome}");
        }

        // Block or dodge pressed: the guard comes first, within limits. The pause after a swing (in which Kenshi neither attacks nor defends)
        // ends once the lock after the blow is over; the character's own swing is given up (a feint) only early in its wind-up, and a feint
        // keeps the next attack back a moment. Late in the swing it is committed: the guard waits for the swing to end.
        // Cut the recovery: once the swing's last blow has struck and the lock after the blow is over, blocking, dodging, stepping or a new attack
        // ends the swing's follow-through at once. Kenshi's pause after a swing still keeps the next attack back (counted from the cut); blocking,
        // dodging and stepping are free.
        if (_cutRecovery && s.AttackTech >= 0 && s.NextBlow == 0 && s.AttackTicks > 0 && now < s.AttackEnd
            && CombatPatches.Technique(Combat, s.AttackTech) is { } done
            && (BlockHeld || _dodgeNew || MoveIntent != Vector2.Zero || AttackSeq >= 0 && AttackSeq == s.AttackSeq))
        {
            int lastBlow = s.AttackStart + (int)MathF.Ceiling(done.StrikeProgress(Math.Max(1, done.Blows)) * s.AttackTicks);
            if (now >= lastBlow + _recoveryTicks)
            {
                float pause = s.LastOutcome switch
                {
                    BlowOutcome.Blocked => CombatTuning.BlockedPause,
                    BlowOutcome.Dodged => CombatTuning.DodgedPause,
                    BlowOutcome.Missed => CombatTuning.MissedPause,
                    _ => CombatTuning.HitPause,
                };
                s.AttackTech = -1;
                s.CancelTick = now;
                s.ReadyTick = Math.Max(s.ReadyTick, now + Math.Max(1, (int)MathF.Ceiling(pause / Dt(world))));
                s.PauseUntil = Math.Min(s.PauseUntil, now);
                s.DefendReadyTick = Math.Min(s.DefendReadyTick, now);
                Detail($"recovery cut {s.AttackEnd - now} ticks early ({(BlockHeld ? "block" : _dodgeNew ? "dodge" : MoveIntent != Vector2.Zero ? "step" : "attack")})");
            }
        }

        bool committed = false;
        if (_blockDown || _dodgeNew)
        {
            long unlocked = _swingEndedTick + _recoveryTicks;
            if (now < s.PauseUntil) s.PauseUntil = (int)Math.Max(now, Math.Min(s.PauseUntil, unlocked));
            if (s.AttackTech >= 0 && s.NextBlow == 1 && CombatPatches.Technique(Combat, s.AttackTech) is { } own)
            {
                int strikes = s.AttackStart + (int)MathF.Ceiling(own.StrikeProgress(1) * s.AttackTicks);
                int feintBy = s.AttackStart + (int)MathF.Floor(FeintWindow * (strikes - s.AttackStart));
                if (CancelSwing && (now <= feintBy || _readied))   // a readied swing (held at the ready point) can always be given up
                {
                    s.AttackTech = -1;
                    s.NextBlow = 0;
                    FeintLockUntil = tick + _feintLockoutTicks;
                    AttackHeld = false;
                    _heldSwingSeq = -1;
                    _readied = false;
                    Detail("feint: own swing given up");
                    ManualCombatMod.Notify("Feint");
                }
                else committed = true;
            }
            else if (s.AttackTech >= 0) committed = true;
            if (committed) Detail("committed to the swing: the guard waits");
        }

        // Block pressed or turned: let the fighter answer the swing coming at it now, not after the game's "no defence" wait.
        if (_blockDown || _blockTurned)
        {
            s.DefendReadyTick = Math.Min(s.DefendReadyTick, now);
            s.ReactCheckTick = Math.Min(s.ReactCheckTick, now);
        }

        // Block released: lower the guard.
        if (_blockUp)
        {
            // only a block: a dodge under way plays out (a perfect dodge is a quick tap, let go before the blow lands)
            if (s.ReactTech >= 0 && CombatPatches.IsBlock(Combat, s.ReactTech)) s.ReactTech = -1;
            s.DefendReadyTick = Math.Min(s.DefendReadyTick, now);
        }

        // Dodge pressed: a real dodge at once, swing coming or not (the game plays it, its clip carries the character, and a blow that comes
        // while it is on is dodged); a block in progress gives way to it. Not while committed to a swing, staggered or down.
        if (_dodgeNew)
        {
            _dodgeNew = false;
            var cold = world.Characters.Cold(Slot);
            if (committed || s.Down || now < s.StunUntil) Detail("dodge: not now (committed, staggered or down)");
            else if (cold?.Fighter is { } fighter && cold.Medical is { } medical
                && CombatPatches.DodgeTechnique(Combat, fighter, medical.Prone, (float)((tick * 0.6180339887) % 1.0)) is { } dodge)
            {
                DodgeTick = tick;
                DodgeTicks = CombatPatches.ClipTicks(Combat, world, Slot, cold, dodge.Technique);
                DodgeShown = dodge.Index;
                s.ReactTech = dodge.Index;
                s.ReactStart = now;
                s.ReactTicks = DodgeTicks;
                s.ReactCheckTick = now;
                s.DefendReadyTick = Math.Max(s.DefendReadyTick, now + DodgeTicks);
                if (now < s.PauseUntil) s.PauseUntil = now;
                Detail($"dodge: '{dodge.Technique.Name}' ({DodgeTicks} ticks)");
            }
            else Detail("dodge: no dodge move for this weapon");
        }

        // The dodge key pressed within the perfect-dodge window (the parry window: Defence, Martial Arts with fists) before a blow lands, its dodge
        // avoiding that blow: a perfect dodge, as with right click and fists ("Perfect dodge!", a riposte for the next swing). Once per dodge.
        if (Dodging(tick) && DodgeTick != _perfectDodgeOf && !_dodgePaused && ParryOn && ParryTicks > 0 && s.ReactTech >= 0 && s.ReactTicks > 0
            && CombatPatches.IsDodge(Combat, s.ReactTech) && CombatPatches.Incoming(Combat, world, Slot, now) is { } dodged
            && dodged.Lands >= DodgeTick && dodged.Lands - DodgeTick <= ParryTicks)
        {
            float at = (dodged.Lands - s.ReactStart) / (float)s.ReactTicks;
            if (at >= HitOutcomes.DodgeFrom && at <= HitOutcomes.DodgeTo)
            {
                _perfectDodgeOf = DodgeTick;
                PerfectDodge(dodged.Lands);
                Detail($"perfect dodge (dodge key, {dodged.Lands - DodgeTick} ticks before the blow)");
            }
        }

        // The enemies swinging at the character (auto-block in group fights).
        Attackers = fighting ? CombatPatches.SwingingAt(Combat, world, Slot) : 0;

        // The held guard: while the block button is held the character keeps a real block up, swing or no swing (Kenshi would lower it as soon
        // as no swing is coming). With none coming it faces the aimed side and holds its pose; a swing that comes is met by the game's own
        // defence update, which turns the guard to the blow's side through ChooseDefence.
        HoldGuard(world, ref s, now, fighting && !leaving);

        // The crossbow reload: held and standing still, in or out of a fight (a third kept once reached when it stops early).
        ReloadControl(world, ref s, fighting);

        // The player's own swing is never given up by itself to defend (Kenshi's abort when a blow at it is past half): only a feint gives it
        // up, and a chamber needs it to go on.
        if (s.AttackTech >= 0) s.DefendReadyTick = Math.Max(s.DefendReadyTick, now + 2);

        // The crossbow chosen with the swap key: Kenshi's ranged mode on for it.
        if (WeaponMode == 2 && world.Characters.Cold(Slot) is { } shooter) shooter.Orders |= Meitou.Simulation.Player.CharacterOrders.Ranged;

        _blockDown = _blockUp = _blockTurned = false;
        _releasedNow = false;
    }

    /// <summary>
    /// Clashing: the character's swing and one enemy's swing at it, both from the same side (the same blow direction: both from their right,
    /// both overhead...), landing within the clash window of each other, the first of them now: those two swings stop (no blow lands), sparks
    /// fly, and both wait a moment. In a group fight only the one enemy whose blow meets the character's is stopped: the one landing closest
    /// in time to it.
    /// </summary>
    void Clash(World world, ref CombatSlot s, int now)
    {
        if (s.AttackTech < 0 || s.NextBlow <= 0 || s.AttackTicks <= 0) return;
        if (CombatPatches.Technique(Combat, s.AttackTech) is not { } mine) return;
        int myLand = s.AttackStart + (int)MathF.Ceiling(mine.StrikeProgress(s.NextBlow) * s.AttackTicks);
        int mySide = mine.DirectionOf(s.NextBlow);

        var slots = CombatPatches.WriteSlots(Combat);
        var prev = world.Characters.Previous;
        if ((uint)Slot >= (uint)prev.Length) return;
        var at = prev[Slot].Position;
        int best = -1, bestGap = int.MaxValue;
        for (int c = 0; c < slots.Length && c < prev.Length; c++)
        {
            if (c == Slot || !prev[c].Alive) continue;
            ref var o = ref slots[c];
            if (o.Down || o.AttackTech < 0 || o.NextBlow <= 0 || o.AttackTicks <= 0 || o.TargetSlot != Slot || o.TargetGeneration != Generation) continue;
            if (Vector3.DistanceSquared(prev[c].Position, at) > 50 * 50) continue;
            if (CombatPatches.Technique(Combat, o.AttackTech) is not { } theirs || theirs.DirectionOf(o.NextBlow) != mySide) continue;
            int theirLand = o.AttackStart + (int)MathF.Ceiling(theirs.StrikeProgress(o.NextBlow) * o.AttackTicks);
            int gap = Math.Abs(myLand - theirLand);
            if (Math.Min(myLand, theirLand) > now || gap > _clashTicks || gap >= bestGap) continue;
            best = c;
            bestGap = gap;
        }
        if (best < 0) return;

        // Crushing through: the stronger side (Strength, weapon skill, weapon weight) may keep its swing; only the weaker one's stops.
        int[] both = [Slot, best];
        string crushed = "";
        if (_clashCrush && world.Characters.Cold(Slot) is { } meC && world.Characters.Cold(best) is { } themC)
        {
            float mine2 = ClashPower(meC), theirs2 = ClashPower(themC);
            float strong = MathF.Max(mine2, theirs2), weak = MathF.Min(mine2, theirs2);
            float chance = strong > 0 ? Math.Clamp((strong - weak) / (strong + weak) * _crushScale, 0, 0.9f) : 0;
            ulong r = CombatPatches.Mix(world.Seed ^ 0x434C415348UL ^ (ulong)(uint)now * 0x9E37UL ^ (ulong)(uint)best);
            float roll = (r >> 40) / (float)(1UL << 24);
            Detail($"clash power: you {mine2:0}, {themC.Name} {theirs2:0}; crush chance {chance:P0}, roll {roll:0.00}");
            if (roll < chance)
            {
                bool iWin = mine2 >= theirs2;
                both = [iWin ? best : Slot];   // only the loser's swing stops
                crushed = iWin ? "Crushed through!" : "Overpowered!";
            }
        }

        int pause = (int)MathF.Ceiling(0.35f / Dt(world));
        foreach (int k in both)
        {
            ref var f = ref slots[k];
            f.AttackTech = -1;
            f.NextBlow = 0;
            f.CancelTick = now;
            f.ReadyTick = Math.Max(f.ReadyTick, now + pause);
        }
        world.BlockSparks?.Enqueue(new CharacterId(Slot, Generation));
        world.BlockSparks?.Enqueue(new CharacterId(best, slots[best].Generation));
        if (CounterSounds) world.PostSound("Impact", new CharacterId(Slot, Generation), "Impacts", "Parry");
        _clashes++;
        Detail($"CLASH with {world.Characters.Cold(best)?.Name}, both {Dir.Name(mySide)}{(crushed.Length > 0 ? ": " + crushed : "")} (clashes this world: {_clashes})");
        ManualCombatMod.Notify(crushed.Length > 0 ? crushed : "Clash!");
    }

    /// <summary>A fighter's weight in a clash: Strength + its weapon skill + 2 per kg of weapon weight (Kenshi's effective stats).</summary>
    static float ClashPower(CharacterCold c)
    {
        if (c.Fighter is not { } f) return 0;
        float str = CombatPatches.Stat(c, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength);
        float skill = CombatPatches.Stat(c, f.WeaponSkill);
        float weight = f.Unarmed ? 0 : MathF.Min(f.Weapon!.Stats.Weight, 40);
        return str + skill + 2 * weight;
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>A left click with the crossbow out: a shot, or a reload when it is empty.</summary>
    void FirePress(World world, Vector2 look)
    {
        var read = CombatPatches.ReadSlots(Combat);
        if ((uint)Slot < (uint)read.Length && read[Slot].Loaded <= 0) return;   // empty: holding the button reloads (ReloadControl)
        _fireUntil = world.Tick + (long)MathF.Ceiling(1f / Dt(world));
        _attackLook = look;
        _fireNew = true;
        _rangeTold = false;
        Detail("fire pressed");
    }

    /// <summary>The crossbow (see Schedule).</summary>
    void Crossbow(World world, ref CombatSlot s, long tick)
    {
        if (!s.Ranged)
        {
            _emptyTold = false;
            _steadyHeld = false;
            _steadyLevel = 0;
            ShotSpread = 0;
            return;
        }

        // The reticle: the crossbow's own deviation for the character's crossbows and perception skills, times the spread setting; right click
        // held draws it in to the steady fraction over the steady time (and it opens again, quicker, when let go).
        float dt = Dt(world);
        _steadyLevel = _steadyHeld ? MathF.Min(1, _steadyLevel + dt / _steadySeconds) : MathF.Max(0, _steadyLevel - dt / 0.3f);
        float baseSpread = world.Characters.Cold(Slot) is { } me ? CombatPatches.CrossbowDeviation(me) : 0;
        ShotSpread = baseSpread * _spreadScale * (1 - (1 - _steadyMin) * _steadyLevel);

        if (FireAsked(tick) && s.RangedState == (byte)RangedStates.Closing && !_rangeTold)
        {
            _rangeTold = true;
            ManualCombatMod.Notify("Out of range");
        }
        if (_fireNew)
        {
            _fireNew = false;
            if (_attackLook != Vector2.Zero && Targets.Pick(world, Combat, Slot, _attackLook, exclude: -1, reachOnly: false) is { } aimed
                && aimed != s.TargetSlot)
                Retarget(world, ref s, aimed, "aimed");
        }
        if (s.Loaded < _lastLoaded) _fireUntil = long.MinValue;   // the shot went
        if (s.Loaded <= 0 && s.ReloadLeft <= 0 && !_emptyTold)
        {
            _emptyTold = true;
            ManualCombatMod.Notify("Empty - hold left click to reload");
        }
        if (s.Loaded > 0) _emptyTold = false;
        _lastLoaded = s.Loaded;
    }

    /// <summary>
    /// Chambering (ICombatHitFilter, serial): a melee blow about to land on the character while it swings a swing the player pressed within the
    /// chamber window before this blow lands is turned aside: it misses (and its attacker gets Kenshi's pause after a miss).
    /// </summary>
    public void HitLanding(World world, ref PendingHit hit)
    {
        Chamber(world, ref hit);
        // Log details / test runs: a blow that still lands on the character after the chamber check (a chambered one does not).
        if (Slot >= 0 && hit.Source == CombatHitSource.Melee && !hit.Cancelled && hit.Victim.Slot == Slot && hit.Victim.Generation == Generation
            && Logging)
            LogHitTaken(world, in hit);
    }

    /// <summary>
    /// The crossbow reload, in and out of fights: it counts on only while the player holds it (left click on the empty crossbow in a fight, Mouse 4
    /// out of fights, or the reload key) and stands still (no W A S D). Stopped early (let go, or stepping), a reload that got a third of the way is
    /// kept at the third and goes on from there next time; less is lost. In a fight Meitou's own reload counts it (CombatPatches.ReloadPrefix lets it
    /// run only now); out of a fight this does, on the same fight-state timer, so a reload carries over between the two.
    /// </summary>
    void ReloadControl(World world, ref CombatSlot s, bool fighting)
    {
        var cold = world.Characters.Cold(Slot);
        var x = cold?.Fighter?.Crossbow;
        bool empty = ManualCrossbow && x is not null && s.Loaded <= 0;
        bool goes = empty && ReloadGoes && !s.Down && cold!.Medical is { BothArmsWork: true, Prone: false } && cold.WaterState != 3;

        if (_reloadRunning && !goes && empty && s.ReloadLeft > 0 && s.ReloadTime > 0)
        {
            float done = 1 - s.ReloadLeft / s.ReloadTime;
            bool kept = done >= ReloadCheckpoint;
            s.ReloadLeft = kept ? s.ReloadTime * (1 - ReloadCheckpoint) : 0;
            Detail($"reload stopped at {done:P0}: {(kept ? "kept at a third" : "lost")}");
        }
        _reloadRunning = goes;

        if (goes && !fighting) ReloadOutOfFight(world, ref s, cold!, x!);
        // Its animation (Meitou plays crossbow poses only in fights): the reload's progress, -1 with none (CombatPatches.TechniqueOfPostfix).
        OutOfFightReload = goes && !fighting && s.Loaded <= 0 && s.ReloadTime > 0 && s.ReloadLeft > 0 ? Math.Clamp(1 - s.ReloadLeft / s.ReloadTime, 0, 1) : -1;
    }

    /// <summary>The out-of-fight reload's progress (0 to 1) while it runs, else -1: its animation plays.</summary>
    public float OutOfFightReload { get; private set; } = -1;

    /// <summary>
    /// The reload out of fights (Meitou reloads only in a fight): as long as Kenshi's reload takes the character (the crossbow's reload time by
    /// Crossbows and Dexterity), then one bolt is taken, as a reload in a fight does. No reload animation plays out of a fight.
    /// </summary>
    void ReloadOutOfFight(World world, ref CombatSlot s, CharacterCold cold, CrossbowInstance x)
    {
        if (cold.InCombat || cold.Fighter is not { } fighter) return;
        if (!x.InfiniteAmmo && Ammunition.FirstCarried(cold.Inventory, x.Data) < 0) return;

        if (s.ReloadLeft <= 0)
        {
            float skill = RangedFormulas.SkillBlend(CombatPatches.Stat(cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Crossbows),
                CombatPatches.Stat(cold, Meitou.Data.Gameplay.Bodies.StatsEnumerated.Dexterity), fighter.DexterityMult);
            s.ReloadTime = RangedFormulas.ReloadTime(skill, x.Stats.ReloadMin, x.Stats.ReloadMax);
            s.ReloadLeft = s.ReloadTime;
            Detail($"reloading out of a fight ({s.ReloadTime:0.0} s)");
        }
        s.ReloadLeft -= Dt(world);
        if (s.ReloadLeft > 0) return;

        CombatPatches.FinishReload(world, Combat, Slot, ref s, cold, x);
        Detail($"reloaded out of a fight: {s.Loaded} loaded");
    }

    /// <summary>
    /// The parry and chamber windows, seconds, from a skill: none under 10, then 0.2 s (10 to 29), 0.3 s (30 to 49), 0.4 s (50 to 79) and the full
    /// 0.5 s from 80. The parry's skill is Defence, the chamber's Attack (Martial Arts for both with fists), as the stats panel shows them
    /// (injuries and gear included).
    /// </summary>
    public static float SkillWindow(float skill) => skill < 10 ? 0 : skill < 30 ? 0.2f : skill < 50 ? 0.3f : skill < 80 ? 0.4f : 0.5f;

    /// <summary>The hand-fought character's parry and chamber windows from its skills now (each tick: injuries and gear change them).</summary>
    void SkillWindows(World world)
    {
        if (world.Characters.Cold(Slot) is not { Stats: not null, Fighter: { } fighter } cold) return;
        bool fists = fighter.Unarmed;
        float defence = CombatPatches.Stat(cold, fists ? Meitou.Data.Gameplay.Bodies.StatsEnumerated.MartialArts
            : Meitou.Data.Gameplay.Bodies.StatsEnumerated.MeleeDefence);
        float attack = CombatPatches.Stat(cold, fists ? Meitou.Data.Gameplay.Bodies.StatsEnumerated.MartialArts
            : Meitou.Data.Gameplay.Bodies.StatsEnumerated.MeleeAttack);
        float dt = Dt(world);
        ParryTicks = (int)MathF.Round(SkillWindow(defence) / dt);   // with fists: the perfect dodge's window
        _chamberTicks = fists ? 0 : (int)MathF.Round(SkillWindow(attack) / dt);   // no chambering with fists
        _counterTicks = (int)MathF.Round(1.5f / dt);
    }

    /// <summary>The tick the chambering swing ends: no stagger before it (a chamber that lands is not punished).</summary>
    public long ChamberSafeUntil { get; private set; } = long.MinValue;
    /// <summary>The enemy whose swing was chambered, and that swing's sequence (its later blows are turned aside too).</summary>
    int _chamberedBy = -1, _chamberedSeq = -1;

    /// <summary>The widest angle off the character's heading a chambered attacker may stand at (120 degrees: front, sides, a little behind).</summary>
    const float ChamberArcDegrees = 120;
    static readonly float ChamberArcCos = MathF.Cos(ChamberArcDegrees * MathF.PI / 180);

    /// <summary>The player's own blows (combos) and chambering: see <see cref="HitLanding"/>.</summary>
    void Chamber(World world, ref PendingHit hit)
    {
        if (Slot < 0 || hit.Source != CombatHitSource.Melee) return;

        // The player's own blow landing: the next swing may chain from it (combos).
        if (hit.Attacker.Slot == Slot && hit.Attacker.Generation == Generation)
        {
            _lastSwingHit = true;
            var own = CombatPatches.WriteSlots(Combat);
            if ((uint)Slot < (uint)own.Length)
            {
                _hitTick = hit.Tick;
                _hitSwingSeq = own[Slot].AttackSeq;
            }
            return;
        }

        if (!ChamberOn || hit.Victim.Slot != Slot || hit.Victim.Generation != Generation) return;
        var slots = CombatPatches.WriteSlots(Combat);
        int a = hit.Attacker.Slot;
        if ((uint)Slot >= (uint)slots.Length || (uint)a >= (uint)slots.Length) return;
        ref var me = ref slots[Slot];
        ref var them = ref slots[a];

        // The rest of a chambered swing (a move of two blows) is turned aside too, while the chambering swing goes on.
        if (a == _chamberedBy && them.AttackSeq == _chamberedSeq && hit.Tick < ChamberSafeUntil)
        {
            hit.Cancelled = true;
            Detail($"chamber: {world.Characters.Cold(a)?.Name}'s next blow of the same swing turned aside too");
            return;
        }

        if (_chamberTicks <= 0 || _attackPaused || _readied || hit.Tick - _attackPressTick > _chamberTicks || hit.Tick < _attackPressTick) return;   // a swing held at the ready point (not let go) chambers nothing
        if (me.AttackTech < 0 || them.AttackTech < 0) return;
        if (CombatPatches.Technique(Combat, me.AttackTech) is not { } mine || CombatPatches.Technique(Combat, them.AttackTech) is not { } theirs) return;
        // Only a swing from the same direction as the blow turns it aside, aimed as a block against it is (a thrust by a thrust, a blow at your left by
        // a swing from your left), and the weapon must really have swung that way (not another side for want of the aimed one).
        int theirSide = theirs.DirectionOf(Math.Max(1, hit.Blow));
        int aim = Mirror ? AttackDir switch { Dir.Left => Dir.Right, Dir.Right => Dir.Left, _ => AttackDir } : AttackDir;
        static int Plain(int d) => d == Dir.Up ? Dir.Overhead : d;   // the overhead guard meets an upward swing, so the overhead aim chambers one
        if (AttackDir == Dir.None || Plain(aim) != Plain(theirSide))
        {
            Detail($"no chamber: aimed {Dir.Name(AttackDir)}, the blow comes {Dir.Name(theirSide)}");
            return;
        }
        if (mine.DirectionOf(Math.Max(1, me.NextBlow)) != AttackTag(AttackDir))
        {
            Detail($"no chamber: the weapon swung another side than the aimed {Dir.Name(AttackDir)}");
            return;
        }
        // Only an attacker in front or at the side (up to ChamberArc off the way the character faces): a forward swing cannot turn aside a blow
        // from well behind.
        var prev = world.Characters.Previous;
        if ((uint)a < (uint)prev.Length && (uint)Slot < (uint)prev.Length)
        {
            var to = new Vector2(prev[a].Position.X - prev[Slot].Position.X, prev[a].Position.Z - prev[Slot].Position.Z);
            var facing = new Vector2(MathF.Sin(prev[Slot].Yaw), MathF.Cos(prev[Slot].Yaw));   // as HitOutcomes.Behind reads the heading
            if (to.LengthSquared() > 1e-4f && Vector2.Dot(Vector2.Normalize(to), facing) < ChamberArcCos)
            {
                Detail($"no chamber: {world.Characters.Cold(a)?.Name} is too far behind");
                return;
            }
        }

        hit.Cancelled = true;
        _chambers++;
        _attackPressTick = long.MinValue / 2;   // one chamber per press
        // A chamber that lands keeps the swing safe: no stagger until it ends (StaggerPrefix), and the same enemy swing's later blows miss.
        _chamberedBy = a;
        _chamberedSeq = them.AttackSeq;
        ChamberSafeUntil = Math.Max(me.AttackEnd, hit.Tick + 1);
        if (CounterSounds) world.PostSound("Impact", new CharacterId(Slot, Generation), "Impacts", "Parry");
        Detail($"CHAMBER: {world.Characters.Cold(a)?.Name}'s blow ({Dir.Name(theirSide)}) turned aside (chambers this world: {_chambers})");
        ManualCombatMod.Notify("Chamber!");
    }

    /// <summary>
    /// Log details / test runs: why a blow at the hand-fought character got through, as the game's outcome rule (HitOutcomes.Decide) saw it: the
    /// defence up (a block counts when its direction is the blow's and its clip is past half), and whether a stagger voided it. Read-only.
    /// </summary>
    void LogHitTaken(World world, in PendingHit hit)
    {
        var slots = CombatPatches.WriteSlots(Combat);
        int a = hit.Attacker.Slot;
        if ((uint)Slot >= (uint)slots.Length || (uint)a >= (uint)slots.Length) return;
        ref var me = ref slots[Slot];
        long now = hit.Tick;
        int side = CombatPatches.BlowSide(Combat, world, Slot, a, hit.Blow);
        var defence = me.ReactTech >= 0 ? CombatPatches.Technique(Combat, me.ReactTech) : null;
        string up = defence is null
            ? "no defence up"
            : $"defence '{defence.Name}' ({(defence.IsBlock ? "block" : defence.IsDodge ? "dodge" : "other")}, side {defence.AttackDirection1}, "
              + $"{(me.ReactTicks > 0 ? (now - me.ReactStart) / (float)me.ReactTicks : 0):0.00} of its clip)";
        Detail($"HIT TAKEN from {world.Characters.Cold(a)?.Name}: blow side {side}, block held {(BlockHeld ? $"yes ({Dir.Name(BlockDir)})" : "no")}, {up}"
            + (now < me.StunUntil ? $", staggered for {me.StunUntil - now} more ticks (a stagger voids any guard)" : ""));
    }

    /// <summary>The last system to run (the test channel's report).</summary>
    public static ManualCombatSystem? Last => s_last;

    /// <summary>The test channel's report: the hand-fought character's fight as of the last finished tick, and the mod's counters.</summary>
    public string TestReport()
    {
        if (Slot < 0 || _world is not { } world) return "no character bound";
        var read = CombatPatches.ReadSlots(Combat);
        if ((uint)Slot >= (uint)read.Length) return "slot gone";
        var s = read[Slot];
        long now = world.Tick;
        var cold = world.Characters.Cold(Slot);
        string target = s.TargetSlot < 0 ? "none" : $"{world.Characters.Cold(s.TargetSlot)?.Name} #{s.TargetSlot}";
        string state = s.Down ? "down" : s.AttackTech >= 0 ? "swinging" : s.ReactTech >= 0 ? "defending" : now < s.StunUntil ? "staggered" : "free";
        int incoming = CombatPatches.IncomingSide(Combat, world, Slot, (int)now);
        string lands = incoming != 0 && CombatPatches.Incoming(Combat, world, Slot, (int)now) is { } b ? $" (lands in {b.Lands - now})" : "";
        return $"tick {now}, {cold?.Name} #{Slot}: {state}, target {target}, block held {(BlockHeld ? "yes" : "no")}, attack waiting {(AttackSeq >= 0 ? "yes" : "no")}, "
            + $"enemies near {Attackers}, incoming {(incoming == 0 ? "none" : Dir.Name(incoming))}{lands}, poise {_poise} (set at a hit), combo step {_chain}, "
            + $"crossbow {(s.Ranged ? $"yes (loaded {s.Loaded}, spread {ShotSpread:0.00})" : "no")}, "
            + $"reload {(s.Loaded > 0 ? "loaded" : s.ReloadTime > 0 && s.ReloadLeft > 0 ? $"{1 - s.ReloadLeft / s.ReloadTime:P0}" : "0%")}"
            + $"{(ReloadHeld ? " held" : "")}{(_reloadRunning ? " running" : "")}, parry window {ParryTicks}t, chamber window {_chamberTicks}t, "
            + $"defence {(s.ReactTech >= 0 && CombatPatches.Technique(Combat, s.ReactTech) is { } rt ? $"'{rt.Name}' {(s.ReactTicks > 0 ? (now - s.ReactStart) / (float)s.ReactTicks : 0):0.00}" : "none")}"
            + $"{(now < s.StunUntil ? $" stunned {s.StunUntil - now}t" : "")}{(now < s.DefendReadyTick ? $" defend ready in {s.DefendReadyTick - now}t" : "")}, "
            + $"attack held {(AttackHeld ? "yes" : "no")}{(_readied ? " (readied)" : "")}{(UpperSwing ? " (upper body, walking)" : "")}, "
            + $"swing {(s.AttackTech >= 0 && s.AttackTicks > 0 ? $"{(now - s.AttackStart) / (float)s.AttackTicks:0.00} of its clip, blow {s.NextBlow}" : "none")}, "
            + $"yaw {world.Characters.Previous[Slot].Yaw:0.00}; "
            + $"{EnemiesReport(world, read, now)}; "
            + $"parries {Parries}, clashes {_clashes}, chambers {_chambers}";
    }

    /// <summary>
    /// The test report's enemies: each fighter whose target is the hand-fought character, with what keeps it from swinging now (the first of
    /// Kenshi's checks before a swing that holds it), its gap to the character and its run of lost initiative rolls. Test channel only.
    /// </summary>
    string EnemiesReport(World world, CombatSlot[] read, long now)
    {
        var prev = world.Characters.Previous;
        if ((uint)Slot >= (uint)prev.Length) return "enemies: none";
        var parts = new List<string>();
        for (int c = 0; c < read.Length && c < prev.Length; c++)
        {
            ref readonly var e = ref read[c];
            if (c == Slot || !prev[c].Alive || e.TargetSlot != Slot || e.TargetGeneration != Generation) continue;
            string why = e.Down ? "down" : e.AttackTech >= 0 ? "swinging" : e.ReactTech >= 0 ? "defending" : now < e.StunUntil ? "staggered"
                : now < e.PauseUntil ? $"pause {e.PauseUntil - now}t" : now < e.ReadyTick ? $"not ready {e.ReadyTick - now}t"
                : !e.Engaged ? "not let in (engage list)" : e.YieldTo == Slot && now < e.YieldUntil ? $"yielding {e.YieldUntil - now}t"
                : e.Ranged ? "shooting" : "free (out of reach, no room, or no slot)";
            var d = prev[c].Position - prev[Slot].Position;
            string wait = c < _waitSince.Length && _waitSince[c] >= 0 ? $", losing rolls {now - _waitSince[c]}t" : "";
            parts.Add($"{world.Characters.Cold(c)?.Name}: {why}, {MathF.Sqrt(d.X * d.X + d.Z * d.Z):0} away{wait}");
        }
        return parts.Count == 0 ? "enemies: none" : "enemies: " + string.Join(" | ", parts);
    }

    /// <summary>The held guard (see Schedule).</summary>
    void HoldGuard(World world, ref CombatSlot s, int now, bool fighting)
    {
        if (!BlockHeld || !fighting || s.Down || now < s.StunUntil || now < s.PauseUntil || s.AttackTech >= 0) return;
        if (s.ReactTech >= 0 && !CombatPatches.IsBlock(Combat, s.ReactTech)) return;   // a dodge plays out
        var cold = world.Characters.Cold(Slot);
        if (cold?.Fighter is not { } fighter || cold.Medical is not { } medical) return;

        // A swing on its way, the one landing this tick included, is met: by the guard the player holds for its side (easy blocking: its own
        // side), or, when the block roll is lost, by a guard on another side that it gets past. Kenshi's plain scan stops counting a swing on its
        // landing tick; the guard must not turn back to the aim then, or the blow lands on the wrong side (0.9.0's lost blocks).
        if (CombatPatches.Incoming(Combat, world, Slot, now) is { } b)
        {
            // Blocking turns the character to the attacker: a blow from behind is met with the front guard against it (the character turns to
            // it before it lands: the game turns a defender to the swing its block follows), and that attacker becomes the target.
            int meetSide = TurnToBlocked ? FrontSide(world, b.Attacker, b.Blow, b.Side, b.Lands, now) : b.Side;
            int want = AutoBlockAt > 0 && Attackers >= AutoBlockAt ? meetSide : GuardFor(meetSide);
            int meet = want == meetSide && !BlockHolds(world, b.Attacker, b.Seq, b.Blow, b.Lands)
                ? CombatPatches.BeatenGuard(Combat, fighter, medical.Prone, meetSide)
                : CombatPatches.GuardIndex(Combat, fighter, medical.Prone, want);
            if (TurnToBlocked && meet >= 0)
            {
                if (b.Attacker != s.TargetSlot && fighting) Retarget(world, ref s, b.Attacker, "blocked");
                s.ReactedSlot = b.Attacker;
                s.ReactedSeq = b.Seq;
                s.ReactedBlow = b.Blow;
            }
            if (meet < 0 || s.ReactTech == meet) return;   // up already (the game's defence update raised it), or none for that side
            s.ReactTech = meet;
            s.ReactTicks = CombatPatches.ClipTicks(Combat, world, Slot, cold, CombatPatches.Technique(Combat, meet)!);
            s.ReactCheckTick = now;
            if (s.ReactTicks > 0) s.ReactStart = now - (int)MathF.Ceiling(GuardHold * s.ReactTicks);   // up at once: past the half a block needs
            return;
        }

        int side = BlockDir;
        if (Mirror) side = side switch { Dir.Left => Dir.Right, Dir.Right => Dir.Left, _ => side };
        if (side is Dir.None or Dir.Up) side = Dir.Overhead;
        int guard = CombatPatches.GuardIndex(Combat, fighter, medical.Prone, side);
        if (guard < 0) return;

        if (s.ReactTech != guard)
        {
            s.ReactTech = guard;
            s.ReactTicks = CombatPatches.ClipTicks(Combat, world, Slot, cold, CombatPatches.Technique(Combat, guard)!);
            s.ReactedSlot = -1;
            s.ReactCheckTick = now;
        }
        // Raised quickly (GuardRaiseSeconds) and held at the guard's point (past the half a block needs), so a swing that comes meets it up.
        float dt = Dt(world);
        float point = Math.Min(GuardHold, (now - BlockDownTick) * dt / GuardRaiseSeconds * GuardHold);
        if (s.ReactTicks > 0) s.ReactStart = now - (int)MathF.Ceiling(point * s.ReactTicks);
    }

    /// <summary>
    /// Whether the held block stops this blow (the right guard for it is up). A parry press (block pressed within the parry window before the
    /// blow lands, not while paused) always does. Otherwise Kenshi's own block roll decides, with the chance of being beaten scaled down by the
    /// Block beaten chance setting, so a huge skill gap still tells but a good block is rewarded. One answer per blow (attacker, swing, blow),
    /// drawn from the world seed and the blow (deterministic, hashed).
    /// </summary>
    public bool BlockHolds(World world, int attacker, int seq, int blow, int lands)
    {
        if (IsPerfectPress(lands)) return true;
        if (attacker == _rollAttacker && seq == _rollSeq && blow == _rollBlow) return _rollHolds;

        float chance = CombatPatches.KenshiBlockChance(Combat, world, Slot, attacker);
        float beaten = chance < 0 ? 0 : Math.Clamp((100 - chance) / 100f * _beatenScale, 0, 1);
        bool holds = beaten <= 0 || BlockRoll(world.Seed, attacker, seq, blow) >= beaten;
        _rollAttacker = attacker;
        _rollSeq = seq;
        _rollBlow = blow;
        _rollHolds = holds;
        Detail($"block roll vs {world.Characters.Cold(attacker)?.Name}: Kenshi's block chance {chance:0}%, beaten chance {beaten * 100:0}% -> {(holds ? "holds" : "BEATEN")}");
        return holds;
    }

    /// <summary>
    /// Block (or, with fists, the dodge) pressed within the parry window before a blow landing at <paramref name="lands"/>, not while paused: a
    /// parry with a weapon, a perfect dodge with fists. Always holds.
    /// </summary>
    public bool IsPerfectPress(long lands) =>
        ParryOn && ParryTicks > 0 && !BlockPressPaused && BlockPressTick != long.MinValue && BlockPressTick <= lands && lands - BlockPressTick <= ParryTicks;

    /// <summary>The dodge key's press was made while paused (no perfect dodge from it); the dodge (its DodgeTick) a perfect dodge was counted for.</summary>
    bool _dodgePaused;
    long _perfectDodgeOf = long.MinValue;

    /// <summary>A perfect dodge (fists) against the blow landing at _counterFrom: "Perfect dodge!" once it has gone by.</summary>
    long _counterFrom = long.MinValue, _counterUntil = long.MinValue;
    bool _counterTold;
    /// <summary>How long after the dodged blow its riposte lasts (1.5 s, as a parry's).</summary>
    int _counterTicks;

    /// <summary>
    /// A perfect dodge was chosen against a blow landing at <paramref name="lands"/>: the dodge plays out, and a swing started within the
    /// riposte time after the blow plays faster (the Riposte speed, as after a parry; with Combos on).
    /// </summary>
    public void PerfectDodge(long lands)
    {
        _counterFrom = lands;
        _counterUntil = lands + _counterTicks;
        _counterTold = false;
        _riposteUntil = Math.Max(_riposteUntil, _counterUntil);
    }

    /// <summary>A number in [0, 1) from the world seed and the blow (SplitMix64), the same on every machine.</summary>
    float BlockRoll(ulong seed, int attacker, int seq, int blow)
    {
        unchecked
        {
            ulong x = seed ^ 0x424C4F434B524F4CUL ^ ((ulong)(uint)attacker << 40) ^ ((ulong)(uint)seq << 12) ^ (uint)blow ^ ((ulong)(uint)Slot << 56);
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (x >> 40) / (float)(1UL << 24);
        }
    }

    /// <summary>Turns the hand-fought character on <paramref name="pick"/> (the combat system's own Retarget, with the player's pick kept).</summary>
    void Retarget(World world, ref CombatSlot s, int pick, string why)
    {
        var prev = world.Characters.Previous;
        s.TargetSlot = pick;
        s.TargetGeneration = prev[pick].Generation;
        s.Engaged = false;
        s.LastTech = -1;
        s.Picked = true;   // a lock: the game's chooser does not turn it on someone else
        world.Characters.Next[Slot].TaskTime = 0;
        string name = world.Characters.Cold(pick)?.Name ?? "?";
        Detail($"target {why}: {name}");
        if (why == "switched") ManualCombatMod.Notify("Target: " + name);
    }

    /// <summary>The side the swing coming at the hand-fought character comes from (a Kenshi blow code), 0 with none; for the HUD.</summary>
    int IncomingSide(World world, int now, bool fighting)
    {
        _incomingAnimal = false;
        if (!fighting) return 0;
        var (side, attacker) = CombatPatches.IncomingSideFrom(Combat, world, Slot, now);
        _incomingAnimal = attacker >= 0 && world.Characters.Cold(attacker)?.Fighter is { IsAnimal: true };
        return side;
    }

    /// <summary>The swing coming at the character is an animal's (for the HUD's bigger warning; set by <see cref="IncomingSide"/>).</summary>
    bool _incomingAnimal;

    /// <summary>Why a fighter did not start the swing the player asked for, from its fight state at the end of the last tick.</summary>
    static string WhyNoSwing(in CombatSlot s, int now)
    {
        if (s.TargetSlot < 0) return "not in a fight";
        if (s.Down) return "your character is down";
        if (now < s.StunUntil) return "staggered";
        if (s.AttackTech >= 0) return "still finishing the last swing";
        if (s.ReactTech >= 0) return "busy blocking";
        if (now < s.PauseUntil || now < s.ReadyTick) return "recovering from the last swing";
        if (!s.Engaged) return "no room to get at the target (others are attacking it)";
        if (now < s.YieldUntil) return "lost the initiative roll";
        return "target out of reach";
    }

    /// <summary>A parry happened (called from the serial Apply step).</summary>
    public void Parried(long tick)
    {
        Parries++;
        BlockPressTick = long.MinValue;   // one parry per press
        SlowMo.Moment(SlowMo.Kind.Parry);
        if (_combos && _world is { } w) _riposteUntil = tick + (long)MathF.Ceiling(1.5f / Dt(w));
    }

    /// <summary>
    /// The side a blow is met from when blocking turns the character to its attacker: a blow from behind (its rear side, 7 or 9) as the swing's
    /// own side, the way it comes at a character facing the attacker, when the turn can still happen before it lands (a tick at least); else
    /// <paramref name="side"/> as it is.
    /// </summary>
    public int FrontSide(World world, int attacker, int blow, int side, long lands, long now) =>
        side is >= 7 and <= 9 && lands > now ? CombatPatches.SwingSide(Combat, attacker, blow) is > 0 and var front ? front : side : side;

    /// <summary>The guard direction (a Kenshi blow code) the player's block takes against a blow from <paramref name="side"/>.</summary>
    public int GuardFor(int side)
    {
        // Blocking is always directional. The sides the player cannot aim at (behind: 7 to 9; unknown 0; 6): the guard meets the blow.
        if (side is 0 or >= 6) return side;

        int want = BlockDir;
        if (Mirror) want = want switch { Dir.Left => Dir.Right, Dir.Right => Dir.Left, _ => want };
        return want switch
        {
            Dir.Left => Dir.Left,
            Dir.Right => Dir.Right,
            Dir.Thrust => Dir.Thrust,
            // Up, or no aim yet: the overhead guard, which also meets an upward swing.
            _ => side is Dir.Overhead or Dir.Up ? side : Dir.Overhead,
        };
    }

    /// <summary>The guard's raise: the block clip runs from its start to <see cref="GuardHold"/> in this long, then holds there.</summary>
    const float GuardRaiseSeconds = 0.12f;
    /// <summary>Where in a block clip the guard is up (a block counts from half way, HitOutcomes.BlockProgress).</summary>
    const float GuardHold = 0.6f;

    /// <summary>
    /// The combat move source (ModContext.AddCombatMove), asked only when the game plays nothing of its own for the character: while the block
    /// button is held the guard shows at once, even with no swing coming. A real block or dodge
    /// (or a swing, a stagger) takes over. The moves are the weapon class's own, found in the data by direction, so animation mods' moves are used
    /// as they are. Read-only and deterministic: everything it reads is settled before the animation's parallel step.
    /// </summary>
    public CombatMove? Moves(World world, CharacterId id, long tick)
    {
        if (id.Slot != Slot || id.Generation != Generation) return null;
        float dt = Dt(world);

        if (!GuardPoseOn || !BlockHeld) return null;
        var cold = world.Characters.Cold(id.Slot);
        if (cold is not { Fighter: { CanBlock: true } fighter, Medical: { } medical, InCombat: true }) return null;

        int side = BlockDir;
        if (Mirror) side = side switch { Dir.Left => Dir.Right, Dir.Right => Dir.Left, _ => side };
        if (side is Dir.None or Dir.Up) side = Dir.Overhead;

        var tech = CombatPatches.GuardTechnique(Combat, fighter, medical.Prone, side);
        if (tech is null) return null;

        float raised = (tick - BlockDownTick) * dt / GuardRaiseSeconds;
        return new CombatMove(tech, Math.Clamp(raised, 0, 1) * GuardHold);
    }

    /// <summary>Detail lines go to log.txt: the Log details (debug) setting, or a test run.</summary>
    public bool Logging => LogDetails || InputWatch.TestChannel.On;

    /// <summary>A detail line built with $"...": nothing of it is built unless <see cref="Logging"/> (see <see cref="DetailText"/>).</summary>
    public void Detail([InterpolatedStringHandlerArgument("")] ref DetailText line)
    {
        if (line.On) ManualCombatMod.Log(line.Text());
    }

    /// <summary>A detail line that is a plain string. A line joined with + is built before the call: guard it with <see cref="Logging"/>.</summary>
    public void Detail(string line)
    {
        if (Logging) ManualCombatMod.Log(line);
    }

    bool Is(CharacterId id) => !id.IsNone && id.Slot == Slot && id.Generation == Generation;

    static float Dt(World world) => world.TickSeconds > 0 ? world.TickSeconds : 1f / 30;

    /// <summary>Whether <paramref name="id"/> is an animal (Kenshi's animal races and creatures: CharacterFacts.Animal).</summary>
    static bool IsAnimal(World world, CharacterId id)
    {
        var prev = world.Characters.Previous;
        if (id.IsNone || (uint)id.Slot >= (uint)prev.Length || prev[id.Slot].Generation != id.Generation) return false;
        return world.Characters.Cold(id.Slot) is { } cold && Meitou.Simulation.Bodies.CharacterFacts.Animal(cold);
    }

    void Bind(CharacterId id)
    {
        if (id.IsNone || Is(id)) return;
        Slot = id.Slot;
        Generation = id.Generation;
        Clear();
        ManualCombatMod.Log($"manual combat on for character {id}");
    }

    void Release(string why)
    {
        if (Slot < 0) return;
        ManualCombatMod.Log($"manual combat off for character {Slot}.{Generation} ({why})");
        if (why == "turned off") ManualCombatMod.Notify("Manual combat OFF - your character fights on its own");
        Slot = -1;
        Generation = 0;
        Clear();
    }

    void Clear()
    {
        BlockHeld = false;
        BlockPressTick = long.MinValue;
        AttackSeq = -1;
        AttackUntil = -1;
        MoveIntent = Vector2.Zero;
        DodgeTick = long.MinValue;
        DodgeShown = -1;
        FeintLockUntil = long.MinValue;
        Attackers = 0;
        _chain = _lastSwingTag = _pendingLevel = 0;
        _riposteUntil = _fireUntil = long.MinValue;
        ReloadHeld = _reloadRunning = false;
        NextSwingRate = 1;
        WeaponMode = 0;
        _emptyTold = _rangeTold = _lastSwingHit = _steadyHeld = HasShotRay = false;
        _steadyLevel = ShotSpread = 0;
        _leaveUntil = long.MinValue;
        _targetLook = null;
        _attackNew = _blockDown = _blockUp = _blockTurned = _dodgeNew = _leaveNew = false;
        _rollAttacker = -1;
        _rollSeq = _rollBlow = 0;
        _rollHolds = false;
        Array.Fill(_waitSince, -1);
        Array.Fill(_waitLast, -1);
        ChamberSafeUntil = long.MinValue;
        _chamberedBy = _chamberedSeq = -1;
        _counterFrom = _counterUntil = long.MinValue;
        _counterTold = false;
        _dodgePaused = false;
        _perfectDodgeOf = long.MinValue;
        AttackHeld = _readied = _incomingAnimal = false;
        _heldSwingSeq = _blockedFrom = _fastWindupSeq = -1;
        _reaimed = _switched = false;
        UpperSwing = false;
        _fightNow = false;
        OutOfFightReload = -1;
        _readySince = -1;
        _heldActive = false;
        Array.Fill(_defenceSeq, -1);
        if (_riposteSwingSeq >= 0) SlowMo.Riposte(false);
        _riposteSwingSeq = -1;
    }

    void CopySettings(World world)
    {
        var settings = ManualCombatMod.Settings;
        float tick = Dt(world);
        Mirror = settings.MirrorGuards;
        ParryOn = settings.Parry;
        _beatenScale = (float)Math.Clamp(settings.BlockBeatenScale, 0, 1);
        _bufferTicks = Math.Max(1, (int)Math.Round(settings.AttackBuffer / tick));
        LogDetails = settings.LogDetails;
        SkipInitiative = settings.SkipInitiative;
        Footwork = settings.HoldInFights;
        AutoFootwork = settings.AutoFootwork;
        FootworkSpeed = (float)Math.Clamp(settings.FootworkSpeed, 1, 100);
        GuardPoseOn = settings.GuardPose;
        AutoRetarget = settings.AutoRetarget;
        CancelSwing = settings.CancelSwing;
        SwingAnyway = settings.SwingAnyway;
        MirrorAttacks = settings.MirrorAttacks;
        FeintWindow = (float)Math.Clamp(settings.FeintWindow, 0, 1);
        _recoveryTicks = Math.Max(0, (int)Math.Round(settings.RecoveryLock / tick));
        _feintLockoutTicks = Math.Max(0, (int)Math.Round(settings.FeintLockout / tick));
        AutoBlockAt = Math.Max(0, settings.AutoBlockAttackers);
        _combos = settings.Combos;
        _riposteSpeed = (float)Math.Clamp(settings.RiposteSpeed, 1, 3);
        _chainSpeed = (float)Math.Clamp(settings.ChainSpeed, 0, 1);
        _comboFromTicks = Math.Max(0, (int)Math.Round(settings.ComboFrom / tick));
        _comboToTicks = _comboFromTicks + Math.Max(1, (int)Math.Round(settings.ComboLength / tick));
        _comboRestTicks = Math.Max(0, (int)Math.Round(settings.ComboRest / tick));
        CounterSounds = settings.CounterSounds;
        EnemiesDontWait = settings.EnemiesDontWait;
        YieldTicks = Math.Max(1, (int)Math.Round(0.5 / tick));
        _longestWaitTicks = Math.Max(0, (int)Math.Round(settings.LongestEnemyWait / tick));
        ClashOn = settings.Clash;
        _clashTicks = Math.Max(1, (int)Math.Round(settings.ClashWindow / tick));
        ChamberOn = settings.Chamber;
        ManualCrossbow = settings.ManualCrossbow;
        HyperArmorOn = settings.HyperArmor;
        _poiseScale = (float)Math.Clamp(settings.PoiseScale, 0, 3);
        _poiseRefillTicks = Math.Max(1, (int)Math.Round(settings.PoiseRefill / tick));
        _graceTicks = Math.Max(0, (int)Math.Round(settings.StaggerGrace / tick));
        _armorSwingOnly = settings.ArmorSwingOnly;
        _clashCrush = settings.ClashCrush;
        _crushScale = (float)Math.Clamp(settings.CrushScale, 0, 4);
        _spreadScale = (float)Math.Clamp(settings.SpreadScale, 0, 5);
        _steadyMin = (float)Math.Clamp(settings.SteadySpread, 0, 1);
        _steadySeconds = (float)Math.Clamp(settings.SteadyTime, 0.05, 10);
        _holdToReady = settings.HoldToReady;
        _readyPoint = (float)Math.Clamp(settings.ReadyPoint, 0.1, 0.95);
        _heldWindup = (float)Math.Clamp(settings.HeldWindup, 1, 3);
        _heldSideSwitch = settings.HeldSideSwitch;
        _walkReadied = settings.WalkReadied;
        _readiedWalkSpeed = (float)Math.Clamp(settings.ReadiedWalkSpeed, 0.1, 1);
        _guardBreakTicks = Math.Max(1, (int)Math.Round(Math.Clamp(settings.HeldGuardTime, 0.05, 3) / tick));
        _crossbowWalkSpeed = (float)Math.Clamp(settings.CrossbowWalkSpeed, 0.1, 1);
        _reloadWhileWalking = settings.ReloadWhileWalking;
        _reloadWalkSpeed = (float)Math.Clamp(settings.ReloadWalkSpeed, 0.05, 1);
        _cutRecovery = settings.CutRecovery;
        TurnToBlocked = settings.TurnToBlocked;
        AnimalWindup = (float)Math.Clamp(settings.AnimalWindup, 0.4, 1);
    }

    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(Slot);
        hasher.Add(Generation);
        hasher.Add(AttackDir);
        hasher.Add(AttackSeq);
        hasher.Add(AttackUntil);
        hasher.Add(BlockHeld);
        hasher.Add(BlockDir);
        hasher.Add(BlockPressTick);
        hasher.Add(BlockPressPaused);
        hasher.Add(_attackPaused);
        hasher.Add(BlockDownTick);
        hasher.Add(DodgeTick);
        hasher.Add(DodgeTicks);
        hasher.Add(DodgeShown);
        hasher.Add(_swinging);
        hasher.Add(_swingEndedTick);
        hasher.Add(FeintLockUntil);
        hasher.Add(Attackers);
        hasher.Add(FeintWindow);
        hasher.Add(AutoBlockAt);
        hasher.Add(_recoveryTicks);
        hasher.Add(_chain);
        hasher.Add(_lastSwingTag);
        hasher.Add(_pendingLevel);
        hasher.Add(_riposteUntil);
        hasher.Add(NextSwingRate);
        hasher.Add(_clashes);
        hasher.Add(_chambers);
        hasher.Add(WeaponMode);
        hasher.Add(_fireUntil);
        hasher.Add(ReloadHeld);
        hasher.Add(_reloadRunning);
        hasher.Add(_lastLoaded);
        hasher.Add(_lastSwingHit);
        hasher.Add(ShotOrigin);
        hasher.Add(ShotDirection);
        hasher.Add(HasShotRay);
        hasher.Add(_steadyHeld);
        hasher.Add(_steadyLevel);
        hasher.Add(ShotSpread);
        hasher.Add(_spreadScale);
        hasher.Add(_steadyMin);
        hasher.Add(_steadySeconds);
        hasher.Add(_combos);
        hasher.Add(ClashOn);
        hasher.Add(ChamberOn);
        hasher.Add(ManualCrossbow);
        hasher.Add(_comboFromTicks);
        hasher.Add(_comboToTicks);
        hasher.Add(_comboRestTicks);
        hasher.Add(EnemiesDontWait);
        hasher.Add(YieldTicks);
        hasher.Add(_longestWaitTicks);
        for (int k = 0; k < _waitSince.Length; k++)
            if (_waitSince[k] >= 0)
            {
                hasher.Add(k);
                hasher.Add(_waitSince[k]);
                hasher.Add(_waitLast[k]);
            }
        hasher.Add(HyperArmorOn);
        hasher.Add(_poiseScale);
        hasher.Add(_poise);
        hasher.Add(_poiseRefillTicks);
        hasher.Add(_graceTicks);
        hasher.Add(_armorSwingOnly);
        hasher.Add(_poiseHitTick);
        hasher.Add(_staggerGraceUntil);
        hasher.Add(_clashCrush);
        hasher.Add(_crushScale);
        hasher.Add(_hitTick);
        hasher.Add(_hitSwingSeq);
        hasher.Add(_lastSwingAim);
        hasher.Add(CounterSounds);
        hasher.Add(_clashTicks);
        hasher.Add(_chamberTicks);
        hasher.Add(_feintLockoutTicks);
        hasher.Add(MoveIntent);
        hasher.Add(_leaveUntil);
        hasher.Add(_retargetTick);
        hasher.Add(Mirror);
        hasher.Add(_beatenScale);
        hasher.Add(ChamberSafeUntil);
        hasher.Add(_counterFrom);
        hasher.Add(_counterUntil);
        hasher.Add(_counterTold);
        hasher.Add(_counterTicks);
        hasher.Add(_dodgePaused);
        hasher.Add(_perfectDodgeOf);
        hasher.Add(_chamberedBy);
        hasher.Add(_chamberedSeq);
        hasher.Add(_rollAttacker);
        hasher.Add(_rollSeq);
        hasher.Add(_rollBlow);
        hasher.Add(_rollHolds);
        hasher.Add(ParryOn);
        hasher.Add(ParryTicks);
        hasher.Add(_bufferTicks);
        hasher.Add(SkipInitiative);
        hasher.Add(Footwork);
        hasher.Add(AutoFootwork);
        hasher.Add(FootworkSpeed);
        hasher.Add(GuardPoseOn);
        hasher.Add(AutoRetarget);
        hasher.Add(CancelSwing);
        hasher.Add(SwingAnyway);
        hasher.Add(MirrorAttacks);
        hasher.Add(_attackLook);
        hasher.Add(_attackPressTick);
        hasher.Add(_stepInTick);
        hasher.Add(Parries);
        hasher.Add(AttackHeld);
        hasher.Add(_heldSwingSeq);
        hasher.Add(_readied);
        hasher.Add(_holdToReady);
        hasher.Add(_readyPoint);
        hasher.Add(_cutRecovery);
        hasher.Add(TurnToBlocked);
        hasher.Add(_blockedFrom);
        hasher.Add(AnimalWindup);
        hasher.Add(_incomingAnimal);
        hasher.Add(_rateAppliedSeq);
        hasher.Add(_riposteSwingSeq);
        hasher.Add(_heldWindup);
        hasher.Add(_fastWindupSeq);
        hasher.Add(_heldSideSwitch);
        hasher.Add(_reaimed);
        hasher.Add(_switched);
        hasher.Add(_walkReadied);
        hasher.Add(_readiedWalkSpeed);
        hasher.Add(_crossbowWalkSpeed);
        hasher.Add(_reloadWhileWalking);
        hasher.Add(_reloadWalkSpeed);
        hasher.Add(OutOfFightReload);
        hasher.Add(_readySince);
        hasher.Add(_guardBreakTicks);
        hasher.Add(_heldActive);
        hasher.Add(_releasedNow);
        hasher.Add(_guardHeldSeen);
        for (int k = 0; k < _defenceSeq.Length; k++)
            if (_defenceSeq[k] >= 0)
            {
                hasher.Add(k);
                hasher.Add(_defenceSeq[k]);
                hasher.Add(_defenceOk[k]);
            }
        for (int k = 0; k < _guardSince.Length; k++)
            if (_guardSince[k] > int.MinValue / 2 || _guardFreed[k] > int.MinValue / 2)
            {
                hasher.Add(k);
                hasher.Add(_guardSince[k]);
                hasher.Add(_guardFreed[k]);
            }
        hasher.Add(_fightNow);
        hasher.Add(UpperSwing);
        for (int k = 0; k < _animalSlowSeq.Length; k++)
            if (_animalSlowSeq[k] >= 0)
            {
                hasher.Add(k);
                hasher.Add(_animalSlowSeq[k]);
            }
    }
}
