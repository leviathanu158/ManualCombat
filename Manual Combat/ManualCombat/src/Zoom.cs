// Manual Combat for Meitou. GPLv3 or later (see LICENSE).
//
// Crossbow zoom: in PZA's over-the-shoulder or first-person view, holding right click with the crossbow out (steadying the aim) narrows the
// camera's field of view as the aim steadies (setting "Zoom when steadied"), and widens it back when let go. Client side only (the frame's camera,
// never the simulation).
//
// Once a frame, right after Meitou's follow camera and PZA's views placed the camera (a postfix on SandboxScenario.FollowCamera, Priority.Last so
// it runs after PZA's own postfix there). How it stays out of PZA's lens (PZA.WASDControl.Meitou.ViewLens):
//   Over the shoulder  PZA writes its own lens (its "Over-the-shoulder field of view") every frame and remembers it (ViewLens.s_ours), taking any
//                      other value it finds for the game's lens to give back later. The zoom narrows PZA's lens of this frame and writes the
//                      narrowed lens into s_ours too, so PZA still sees its own; when the zoom ends PZA's lens is put back the same way.
//   First person       PZA leaves the game's lens alone: the zoom narrows it and gives it back itself when the zoom ends or the view goes.
// PZA's view is read by reflection (Views.Current); without PZA (or with its names changed) there is no zoom, nothing else changes.
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Meitou.Game.Rendering.Frame;

namespace ManualCombat;

public static class Zoom
{
    static FieldInfo? s_host, s_camera;
    static FieldInfo? s_view, s_pzaOurs;
    static bool s_pzaLooked;

    /// <summary>The lens the zoom narrowed (radians) and the narrowed lens it wrote; NaN while not zooming.</summary>
    static float s_base = float.NaN, s_written = float.NaN;

    /// <summary>The zoom shown (1: none), eased toward the wanted one frame by frame; the last frame's time.</summary>
    static float s_shown = 1;
    static long s_frameAt;
    const float EaseSeconds = 0.06f;

    public static string Patch(Harmony h)
    {
        var scenario = AccessTools.TypeByName("Meitou.Game.Scenarios.SandboxScenario");
        var hostType = AccessTools.TypeByName("Meitou.Game.GameHost");
        var follow = scenario is null ? null : AccessTools.Method(scenario, "FollowCamera", Type.EmptyTypes);
        s_host = scenario is null || hostType is null ? null : AccessTools.GetDeclaredFields(scenario).FirstOrDefault(f => f.FieldType == hostType);
        s_camera = hostType is null ? null : AccessTools.Field(hostType, "Camera");
        if (follow is null || s_host is null || s_camera is null || s_camera.FieldType != typeof(WorldCamera))
            return "crossbow zoom unavailable in this Meitou version (SandboxScenario.FollowCamera / GameHost.Camera not found)";
        h.Patch(follow, postfix: new HarmonyMethod(AccessTools.Method(typeof(Zoom), nameof(FollowCameraPostfix))) { priority = Priority.Last });
        return "patched SandboxScenario.FollowCamera (crossbow zoom, after PZA's views)";
    }

    /// <summary>PZA's view now ("Shoulder", "First", "Normal"), or null without PZA's mod.</summary>
    static string? PzaView()
    {
        if (!s_pzaLooked)
        {
            s_pzaLooked = true;
            s_view = AccessTools.TypeByName("PZA.WASDControl.Meitou.Views") is { } views ? AccessTools.Field(views, "Current") : null;
            s_pzaOurs = AccessTools.TypeByName("PZA.WASDControl.Meitou.ViewLens") is { } lens ? AccessTools.Field(lens, "s_ours") : null;
            if (s_pzaOurs is not null && s_pzaOurs.FieldType != typeof(float)) s_pzaOurs = null;
            ManualCombatMod.Log(s_view is null ? "crossbow zoom off: PZA's views not found" : "crossbow zoom follows PZA's views");
        }
        return s_view?.GetValue(null)?.ToString();
    }

    static void FollowCameraPostfix(object __instance)
    {
        try
        {
            if (s_host!.GetValue(__instance) is not { } host || s_camera!.GetValue(host) is not WorldCamera cam) return;
            string? view = PzaView();
            bool shoulder = view == "Shoulder", inView = shoulder || view == "First";

            // The lens without the zoom: this frame's, unless nobody rewrote ours since the last frame (first person), then the one we narrowed.
            float lens = !float.IsNaN(s_written) && cam.FieldOfView == s_written ? s_base : cam.FieldOfView;

            float zoom = (float)Math.Clamp(ManualCombatMod.Settings.CrossbowZoom, 0.25, 1);
            float want = inView && InputWatch.Active && ManualCombatMod.Settings.ManualCrossbow ? 1 - (1 - zoom) * Hud.Steady : 1;
            long now = Stopwatch.GetTimestamp();
            float dt = s_frameAt == 0 ? 1 : (float)Stopwatch.GetElapsedTime(s_frameAt, now).TotalSeconds;
            s_frameAt = now;
            s_shown = !inView || dt > 0.2f ? want : want + (s_shown - want) * MathF.Exp(-dt / EaseSeconds);

            if (!inView || s_shown > 0.999f)
            {
                // No zoom: the lens back, if ours is still on the camera.
                if (!float.IsNaN(s_written) && cam.FieldOfView == s_written)
                {
                    cam.FieldOfView = s_base;
                    if (shoulder) KeepPzaLens(s_written, s_base);
                }
                s_base = s_written = float.NaN;
                s_shown = 1;
                return;
            }

            float narrowed = lens * s_shown;
            cam.FieldOfView = narrowed;
            if (shoulder) KeepPzaLens(lens, narrowed);
            s_base = lens;
            s_written = narrowed;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("crossbow zoom", e);
        }
    }

    /// <summary>PZA's lens record follows ours: where it holds <paramref name="was"/>, it is set to <paramref name="now"/> (see the file's notes).</summary>
    static void KeepPzaLens(float was, float now)
    {
        if (s_pzaOurs?.GetValue(null) is float ours && ours == was) s_pzaOurs.SetValue(null, now);
    }
}
