// Manual Combat for Meitou. GPLv3 or later.
// "Hold position in fights". In Meitou a move order or a stop command ends the character's fight (CombatSystem.Inputs: MoveOrder ->
// LeaveFight, StopCommand -> Disengage). PZA's WASD mod steers by sending a MoveOrder for the steered character every few ticks while a key is
// held, and a StopCommand when it is released, so holding A/D/W/S to aim a swing ended the fight before the swing could start.
// While the hand-fought character is in a fight, this removes exactly those commands as the world takes them off its queue
// (CommandQueue.TakeDue, a postfix): a MoveOrder or StopCommand naming that one character and nobody else. A right-click move (which orders
// "the selection", an empty list) and orders for several characters still go through, so the player can always break off; so does everything
// once manual combat is off (U). TakeDue runs inside the simulation's serial input step, so what is dropped follows the world's own state.
using HarmonyLib;
using Meitou.Simulation;

namespace ManualCombat;

public static class FightHold
{
    // Set by ManualCombatSystem.Schedule each tick (serial), read in TakeDue (serial, the same world's next input step).
    static CommandQueue? s_queue;
    static int s_slot = -1, s_generation;
    static bool s_inFight;

    /// <summary>
    /// The hand-fought character of the world owning <paramref name="queue"/>, and whether it is fighting (false while it leaves a fight, so its
    /// stop and the player's steps go through).
    /// </summary>
    public static void Publish(CommandQueue queue, int slot, int generation, bool inFight, bool leaving)
    {
        s_queue = queue;
        s_slot = slot;
        s_generation = generation;
        s_inFight = inFight && slot >= 0;
    }

    public static string Patch(Harmony h)
    {
        var takeDue = AccessTools.Method(typeof(CommandQueue), "TakeDue")
            ?? throw new MissingMethodException("CommandQueue.TakeDue");
        h.Patch(takeDue, postfix: new HarmonyMethod(AccessTools.Method(typeof(FightHold), nameof(TakeDuePostfix))));
        return "patched CommandQueue.TakeDue (hold position in fights)";
    }

    static void TakeDuePostfix(CommandQueue __instance, ref IReadOnlyList<SimCommand> __result)
    {
        try
        {
            if (!s_inFight || __result.Count == 0 || !ReferenceEquals(__instance, s_queue) || !ManualCombatMod.Settings.HoldInFights) return;

            List<SimCommand>? kept = null;
            for (int k = 0; k < __result.Count; k++)
            {
                var c = __result[k];
                if (Steers(c))
                {
                    kept ??= [.. __result.Take(k)];
                    continue;
                }
                kept?.Add(c);
            }
            if (kept is null) return;

            __result = kept;   // the fight-mode hint (top right, Hud) tells the player W A S D step here
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("CommandQueue.TakeDue postfix", e);
        }
    }

    /// <summary>A move or stop for the hand-fought character alone (what PZA's WASD steering sends).</summary>
    static bool Steers(SimCommand c)
    {
        IReadOnlyList<CharacterId>? who = c switch
        {
            MoveOrder m => m.Characters,
            StopCommand s => s.Characters,
            _ => null,
        };
        return who is { Count: 1 } && who[0].Slot == s_slot && who[0].Generation == s_generation;
    }
}
