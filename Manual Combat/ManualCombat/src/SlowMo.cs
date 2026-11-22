// Manual Combat for Meitou. GPLv3 or later.
// Optional slow motion through PZA's SlowMo mod (its public SlowMoApi, contract version 1: Hold / Release, found by name; neither mod references
// the other). Without that mod in the load order nothing here does anything, and its settings rows stay hidden.
//   - A short moment after a parry or a perfect dodge (Moment speed and length).
//   - The riposte swing (the faster swing after one) in slow motion until its blow lands, and a little after.
// The world only reports what happens (Moment, Riposte, from its tick); the interface's control tick (InputWatch's Resolve postfix, the host thread
// SlowMoApi asks for) holds and releases the speed in real time. The game speed is the host's clock, not world state, so nothing here is hashed.
using System.Diagnostics;
using System.Reflection;

namespace ManualCombat;

public static class SlowMo
{
    public enum Kind { Parry, PerfectDodge }

    const string MomentHolder = "manualcombat.counter", RiposteHolder = "manualcombat.riposte";
    const string ApiType = "PZA.SlowMotion.Meitou.SlowMoApi";

    static int s_parries, s_dodges, s_seenParries, s_seenDodges;
    /// <summary>The riposte swing is on (set by the world), and the last value the host saw.</summary>
    static volatile bool s_riposte;
    static bool s_riposteSeen;
    static long s_momentUntil, s_riposteUntil;
    static bool s_momentHeld, s_riposteHeld;

    static Func<string, double, bool>? s_hold;
    static Action<string>? s_release;
    static long s_lookedAt = long.MinValue;
    static bool s_toldFound;
    static int s_installed = -1;
    static long s_installedAt = long.MinValue;

    /// <summary>The longest a riposte's slow motion may run (real seconds), should its end never be reported.</summary>
    const double RiposteLongest = 4;

    /// <summary>PZA's SlowMo is in the load order (for the settings rows; while it is not found, looked for again at most every 5 s).</summary>
    public static bool Installed
    {
        get
        {
            if (s_installed == 1) return true;
            long now = Stopwatch.GetTimestamp();
            if (s_installedAt == long.MinValue || now - s_installedAt >= 5 * Stopwatch.Frequency)
            {
                s_installedAt = now;
                s_installed = FindType() is null ? 0 : 1;
            }
            return s_installed == 1;
        }
    }

    /// <summary>A parry or perfect dodge happened in the world (any thread).</summary>
    public static void Moment(Kind kind)
    {
        if (kind == Kind.Parry) Interlocked.Increment(ref s_parries);
        else Interlocked.Increment(ref s_dodges);
    }

    /// <summary>The riposte swing started (true) or its blow has landed / it ended (false), from the world.</summary>
    public static void Riposte(bool on) => s_riposte = on;

    /// <summary>The control tick (host thread): starts what the world asked for since the last, ends what has run its time.</summary>
    public static void HostTick()
    {
        try
        {
            var settings = ManualCombatMod.Settings;
            long now = Stopwatch.GetTimestamp();

            int parries = Volatile.Read(ref s_parries), dodges = Volatile.Read(ref s_dodges);
            bool parry = parries != s_seenParries, dodge = dodges != s_seenDodges;
            s_seenParries = parries;
            s_seenDodges = dodges;
            if ((parry && settings.SlowMoParry || dodge && settings.SlowMoPerfectDodge)
                && Hold(MomentHolder, settings.SlowMoSpeed))
            {
                s_momentUntil = now + Ticks(settings.SlowMoSeconds);
                s_momentHeld = true;
                Told($"slow motion: {settings.SlowMoSpeed:0.00}x for {settings.SlowMoSeconds:0.00} s ({(parry ? "parry" : "perfect dodge")})");
            }

            bool riposte = s_riposte;
            if (riposte != s_riposteSeen)
            {
                s_riposteSeen = riposte;
                if (riposte && settings.SlowMoRiposte && Hold(RiposteHolder, settings.SlowMoRiposteSpeed))
                {
                    s_riposteUntil = now + Ticks(RiposteLongest);
                    s_riposteHeld = true;
                    Told($"slow motion: riposte at {settings.SlowMoRiposteSpeed:0.00}x");
                }
                else if (!riposte && s_riposteHeld)
                    s_riposteUntil = Math.Min(s_riposteUntil, now + Ticks(settings.SlowMoRiposteAfter));
            }

            if (s_momentHeld && now >= s_momentUntil)
            {
                s_momentHeld = false;
                s_release?.Invoke(MomentHolder);
            }
            if (s_riposteHeld && now >= s_riposteUntil)
            {
                s_riposteHeld = false;
                s_release?.Invoke(RiposteHolder);
                Told("slow motion: riposte over");
            }
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("slow motion", e);
        }
    }

    static long Ticks(double seconds) => (long)(Math.Clamp(seconds, 0, 10) * Stopwatch.Frequency);

    static void Told(string line)
    {
        if (ManualCombatMod.Settings.LogDetails || InputWatch.TestChannel.On) ManualCombatMod.Log(line);
    }

    /// <summary>Asks PZA's SlowMo for <paramref name="speed"/> under <paramref name="holder"/>: false without it (or while it is off).</summary>
    static bool Hold(string holder, double speed) => Find() && s_hold!(holder, Math.Clamp(speed, 0.05, 1));

    static Type? FindType() =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(ApiType, false)).FirstOrDefault(t => t is not null);

    /// <summary>PZA's SlowMoApi (looked up again at most every 10 s while it is missing).</summary>
    static bool Find()
    {
        if (s_hold is not null) return true;
        long now = Stopwatch.GetTimestamp();
        if (s_lookedAt != long.MinValue && now - s_lookedAt < 10 * Stopwatch.Frequency) return false;
        s_lookedAt = now;

        var api = FindType();
        if (api is null) return false;
        s_installed = 1;
        if (api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() is not int version || version < 1)
        {
            ManualCombatMod.Log("PZA's SlowMo found, but its SlowMoApi is too old: no slow motion");
            s_lookedAt = long.MaxValue / 2;
            return false;
        }
        var hold = api.GetMethod("Hold", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(double)]);
        var release = api.GetMethod("Release", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (hold is null || release is null || hold.ReturnType != typeof(bool)) return false;
        s_hold = hold.CreateDelegate<Func<string, double, bool>>();
        s_release = release.CreateDelegate<Action<string>>();
        if (!s_toldFound)
        {
            s_toldFound = true;
            ManualCombatMod.Log($"PZA's SlowMo found (SlowMoApi version {version}): slow motion on parries, perfect dodges and ripostes");
        }
        return true;
    }
}
