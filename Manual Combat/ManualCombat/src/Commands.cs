// Manual Combat for Meitou. GPLv3 or later.
// What the player's keys tell the world. Keys are read on the interface's thread; the world only ever changes through these commands, applied
// in ManualCombatSystem.Inputs at the start of a tick, so the game stays deterministic (docs/modding.md "Commands").
using Meitou.Simulation;

namespace ManualCombat;

/// <summary>The directions the player picks with W A S D, and the Kenshi blow codes they stand for.</summary>
public static class Dir
{
    /// <summary>No movement key held: the game picks.</summary>
    public const int None = 0;
    /// <summary>W: an overhead (downward) swing; Kenshi blow code 1.</summary>
    public const int Overhead = 1;
    /// <summary>A: a swing from the left; Kenshi blow code 2.</summary>
    public const int Left = 2;
    /// <summary>D: a swing from the right; Kenshi blow code 3.</summary>
    public const int Right = 3;
    /// <summary>S: a thrust; Kenshi blow code 4.</summary>
    public const int Thrust = 4;
    /// <summary>Kenshi's upward swing (blow code 5). The player's overhead guard covers it.</summary>
    public const int Up = 5;

    public static string Name(int d) => d switch
    {
        Overhead => "overhead",
        Left => "left",
        Right => "right",
        Thrust => "thrust",
        Up => "up",
        None => "any",
        _ => $"code {d}",
    };
}

/// <summary>Turns manual combat on or off for a character (the toggle key).</summary>
public sealed record ManualControl(CharacterId Character, bool On) : SimCommand;

/// <summary>
/// The attack button was pressed with a direction aimed (Dir.None: the game picks the swing). <paramref name="Look"/>: the camera's flat forward
/// (X, Z), for turning on the enemy at the crosshair; zero when unknown.
/// </summary>
public sealed record ManualAttack(CharacterId Character, int Direction, System.Numerics.Vector2 Look = default) : SimCommand
{
    /// <summary>Pressed while the game was paused: no chamber or combo timing from it.</summary>
    public bool InPause { get; init; }

    /// <summary>The button stays held (left click): a <see cref="ManualAttackRelease"/> follows when it is let go (hold to ready).</summary>
    public bool Held { get; init; }
}

/// <summary>The aim changed while the attack button is held: the held swing switches to that side.</summary>
public sealed record ManualAttackAim(CharacterId Character, int Direction) : SimCommand;

/// <summary>The held attack button was let go: a readied swing strikes.</summary>
public sealed record ManualAttackRelease(CharacterId Character) : SimCommand
{
    /// <summary>Let go while the game was paused: no chamber timing from it.</summary>
    public bool InPause { get; init; }
}

/// <summary>The block button went down or up, or the held direction changed while blocking.</summary>
public sealed record ManualBlock(CharacterId Character, bool Held, int Direction) : SimCommand
{
    /// <summary>Pressed while the game was paused: a block, but no parry from it.</summary>
    public bool InPause { get; init; }
}

/// <summary>
/// The way W A S D push the hand-fought character in its fight footwork: a flat world direction (X, Z), unit length, or zero to stand. Sent when
/// it changes (keys, or the camera turning while a key is held).
/// </summary>
public sealed record ManualMove(CharacterId Character, System.Numerics.Vector2 Direction) : SimCommand;

/// <summary>The dodge button.</summary>
public sealed record ManualDodge(CharacterId Character) : SimCommand
{
    /// <summary>Pressed while the game was paused: a dodge, but no perfect dodge from it.</summary>
    public bool InPause { get; init; }
}

/// <summary>The target button: switch to the enemy nearest the camera's aim (<paramref name="Look"/>: the camera's flat forward, X and Z).</summary>
public sealed record ManualTarget(CharacterId Character, System.Numerics.Vector2 Look) : SimCommand;

/// <summary>The leave-fight button: the character drops its fight and may walk (or run) away.</summary>
public sealed record ManualLeave(CharacterId Character) : SimCommand;

/// <summary>The swap key: crossbow or melee weapon.</summary>
public sealed record ManualSwap(CharacterId Character) : SimCommand;

/// <summary>A crossbow shot along the crosshair ray (the camera's eye and direction through the centre of the screen).</summary>
public sealed record ManualShot(CharacterId Character, System.Numerics.Vector3 Origin, System.Numerics.Vector3 Direction) : SimCommand;

/// <summary>
/// The crossbow reload held (true) or let go (false): left click held on an empty crossbow in a fight, Mouse 4 (dodge) out of fights, or the reload
/// key. The reload runs only while held and standing still.
/// </summary>
public sealed record ManualReload(CharacterId Character, bool Held = true) : SimCommand;
