// Manual Combat for Meitou. GPLv3 or later.
// Mount & Blade's direction arrows, drawn round the centre of the screen (PZA's crosshair in its views) while the hand-fought character is in a fight:
//   gold    the side your next swing goes (the aim)
//   blue    the side your guard faces, while block is held
//   red     the side the enemy's swing is coming from (pointing in)
// Drawn after the game's interface each frame, in a postfix on GameLoop.DrawInterface (the Vulkan host; the same hook PZA's squad wheel uses),
// with the game's own overlay triangles. Optional: if the hook is missing the mod works without arrows.
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Game.Rendering.Overlays;

namespace ManualCombat;

public static class Hud
{
    // Published by ManualCombatSystem.Schedule each tick, read on the interface's thread.
    static volatile bool s_inFight;
    static volatile int s_incoming;
    /// <summary>The sides of every swing coming at the character (a bit per Kenshi blow code).</summary>
    static volatile int s_incomingSides;

    /// <summary>Whether the hand-fought character is in a fight now (the in-fight controls and the arrows).</summary>
    public static bool InFight => s_inFight;

    /// <summary>The locked-on target (none: Slot -1), packed as slot and generation for a lock-free read.</summary>
    static long s_target = -1;

    static volatile bool s_ranged, s_reloading;
    static float s_spread, s_steady, s_reload;
    static volatile int s_loaded, s_shots;

    /// <summary>The hand-fought character carries a crossbow (in a fight or not).</summary>
    public static bool HasCrossbow => s_shots > 0;

    /// <summary>Its crossbow is empty.</summary>
    public static bool Empty => s_shots > 0 && s_loaded <= 0;

    /// <summary>Whether the hand-fought character has its crossbow out (left click shoots at the crosshair).</summary>
    public static bool Ranged => s_ranged && s_inFight && ManualCombatMod.Settings.ManualCrossbow;

    /// <summary>How steadied the crossbow's aim is (0 to 1; 0 without the crossbow out): the zoom follows it.</summary>
    public static float Steady => Ranged ? Volatile.Read(ref s_steady) : 0;

    public static void Publish(bool inFight, int incomingSide, Meitou.Simulation.CharacterId target, bool ranged, float spread,
        int loaded = 0, int shots = 0, float steady = 0, float reload = 0, bool reloading = false, int incomingSides = 0)
    {
        s_ranged = ranged;
        s_incomingSides = incomingSides;
        Volatile.Write(ref s_spread, spread);
        Volatile.Write(ref s_steady, steady);
        Volatile.Write(ref s_reload, reload);
        s_reloading = reloading;
        s_loaded = loaded;
        s_shots = shots;
        s_inFight = inFight;
        s_incoming = incomingSide;
        Interlocked.Exchange(ref s_target, target.IsNone ? -1 : (long)target.Slot << 32 | (uint)target.Generation);
    }

    static Meitou.Simulation.CharacterId? Target()
    {
        long t = Interlocked.Read(ref s_target);
        return t < 0 ? null : new Meitou.Simulation.CharacterId((int)(t >> 32), (int)(uint)t);
    }

    static FieldInfo? s_overlay, s_sizeX, s_sizeY;
    static Type? s_sizeType;

    /// <summary>The line under the crosshair and when it was set (Environment.TickCount64); shown for <see cref="LineMs"/>, fading out.</summary>
    static volatile string? s_line;
    static long s_lineAt;
    const long LineMs = 1400, FadeMs = 400;

    /// <summary>Shows <paramref name="text"/> under the crosshair now, replacing the line before (interface thread).</summary>
    public static void Show(string text)
    {
        s_lineAt = Environment.TickCount64;
        s_line = text;
    }
    static readonly int[] Sides = [Dir.Left, Dir.Right, Dir.Overhead, Dir.Thrust];

    static readonly Vector4 Gold = new(0.95f, 0.75f, 0.35f, 0.95f), Blue = new(0.55f, 0.78f, 1f, 0.95f), Red = new(0.95f, 0.22f, 0.15f, 0.95f),
        Edge = new(0.05f, 0.03f, 0.02f, 0.8f), Slot = new(0.9f, 0.86f, 0.75f, 0.22f);

    // The target outline: GameLoop.Outline sets the GPU's hover keys (the characters Kenshi's hover outline draws, by draw key and colour) each
    // frame; a postfix adds the locked-on target in red.
    static FieldInfo? s_loopHost, s_hostGpu, s_hoverKeys;
    static MethodInfo? s_drawKey;
    static readonly Vector4 TargetOutline = new(1f, 0.3f, 0.22f, 1f);

    static string PatchOutline(Harmony h, Type loop)
    {
        var outline = AccessTools.Method(loop, "Outline");
        s_loopHost = loop.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(f => f.FieldType.FullName == "Meitou.Game.GameHost");
        var hostType = s_loopHost?.FieldType;
        s_hostGpu = hostType is null ? null : AccessTools.Field(hostType, "Gpu");
        s_hoverKeys = s_hostGpu is null ? null : AccessTools.Field(s_hostGpu.FieldType, "HoverKeys");
        s_drawKey = hostType is null ? null : AccessTools.Method(hostType, "DrawKey", [typeof(Meitou.Simulation.CharacterId)]);
        if (outline is null || s_loopHost is null || s_hostGpu is null || s_hoverKeys is null || s_drawKey is null
            || s_hoverKeys.FieldType != typeof(IReadOnlyList<(long, Vector4)>))
            return "target outline unavailable in this Meitou version";
        h.Patch(outline, postfix: new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(OutlinePostfix))));
        return "patched GameLoop.Outline (target outline)";
    }

    static void OutlinePostfix(object __instance, string? takeFile)
    {
        try
        {
            if (takeFile is not null || !s_inFight || !InputWatch.Active || !ManualCombatMod.Settings.OutlineTarget) return;
            if (Target() is not { } target) return;
            var host = s_loopHost!.GetValue(__instance);
            var gpu = host is null ? null : s_hostGpu!.GetValue(host);
            if (gpu is null || s_hoverKeys!.GetValue(gpu) is not IReadOnlyList<(long, Vector4)> keys) return;
            long key = (long)s_drawKey!.Invoke(host, [target])!;
            var list = new List<(long, Vector4)>(keys.Count + 1);
            foreach (var k in keys) if (k.Item1 != key) list.Add(k);
            list.Add((key, TargetOutline));
            s_hoverKeys.SetValue(gpu, list);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("target outline", e);
        }
    }

    // The game's pointer in fights: PZA's views pin it at the centre (Meitou's own pointer is their crosshair spot), so the action pointer (a
    // sword over an enemy...) sat on the crosshair. While the hand-fought character fights and PZA's mouse look holds the pointer, it is not
    // drawn; Alt, a window or a menu end PZA's mouse look (PZA.WASDControl.Meitou.Look.On), and the pointer shows again.
    static System.Reflection.PropertyInfo? s_pzaLook;
    static bool s_pzaLookSought;

    static bool PzaLookOn()
    {
        if (!s_pzaLookSought)
        {
            s_pzaLookSought = true;
            s_pzaLook = AccessTools.TypeByName("PZA.WASDControl.Meitou.Look") is { } look ? AccessTools.Property(look, "On") : null;
        }
        return s_pzaLook?.GetValue(null) is true;
    }

    /// <summary>PointerView.DrawPointer / DrawPointerBeside prefix: skipped while the pointer is hidden in a fight.</summary>
    static bool PointerPrefix()
    {
        try
        {
            return !(s_inFight && InputWatch.Active && ManualCombatMod.Settings.HidePointerInFights && PzaLookOn());
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("pointer prefix", e);
            return true;
        }
    }

    static string PatchPointer(Harmony h)
    {
        var view = AccessTools.TypeByName("Meitou.Game.Ui.PointerView");
        var draw = view is null ? null : AccessTools.Method(view, "DrawPointer");
        var beside = view is null ? null : AccessTools.Method(view, "DrawPointerBeside");
        if (draw is null) return "hiding the pointer in fights unavailable in this Meitou version (PointerView.DrawPointer)";
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(PointerPrefix)));
        h.Patch(draw, prefix: prefix);
        if (beside is not null) h.Patch(beside, prefix: prefix);
        return "patched PointerView.DrawPointer (pointer hidden in fights in PZA's views)";
    }

    public static string Patch(Harmony h)
    {
        ManualCombatMod.Log(PatchPointer(h));
        var loop = AccessTools.TypeByName("Meitou.Game.Host.GameLoop");
        var draw = loop is null ? null : AccessTools.Method(loop, "DrawInterface");
        s_overlay = loop is null ? null : AccessTools.Field(loop, "_overlay");
        if (draw is null || s_overlay is null || s_overlay.FieldType != typeof(DebugOverlay))
            return "direction arrows unavailable in this Meitou version (GameLoop.DrawInterface not found)";
        h.Patch(draw, prefix: new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(DrawInterfacePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(DrawInterfacePostfix))));
        return "patched GameLoop.DrawInterface (direction arrows); " + PatchOutline(h, loop!);
    }

    /// <summary>The overlay and frame size of a DrawInterface call, or null (no overlay, covered, not in a fight).</summary>
    static (DebugOverlay O, int W, int H, float S, Vector2 C)? Frame(object loop, object[] args, bool inFight = true)
    {
        if (inFight && !s_inFight || !InputWatch.Active) return null;
        if (args.Length > 1 && args[1] is true) return null;   // covered (loading)
        if (s_overlay!.GetValue(loop) is not DebugOverlay o || args.Length == 0 || args[0] is not { } size) return null;
        var t = size.GetType();
        if (!ReferenceEquals(t, s_sizeType))   // the size struct's fields, looked up once (this runs several times a frame)
        {
            s_sizeType = t;
            s_sizeX = t.GetField("X");
            s_sizeY = t.GetField("Y");
        }
        if (s_sizeX?.GetValue(size) is not int w || s_sizeY?.GetValue(size) is not int h || w <= 0 || h <= 0) return null;
        return (o, w, h, h / 1080f, new Vector2(w / 2f, h / 2f));   // the centre: PZA's crosshair in its views
    }

    /// <summary>The arrows, before the game's interface draws, so its windows and HUD cover them.</summary>
    static void DrawInterfacePrefix(object __instance, object[] __args)
    {
        try
        {
            var settings = ManualCombatMod.Settings;
            ReloadBarOutOfFight(__instance, __args);
            if (Frame(__instance, __args) is not { } frame) return;
            var (o, w, hgt, s, c) = frame;
            if (Ranged)
            {
                Reticle(o, w, hgt, s, c);
                return;
            }
            if (!settings.Arrows) return;
            float a = (float)Math.Clamp(settings.ArrowOpacity, 0.05, 1);

            o.Flush(w, hgt);
            foreach (int side in Sides) Arrow(o, c, side, s, 40, 9, 7, Fade(Slot, a), outward: true, edge: false);

            int aim = InputWatch.AimNow;
            if (aim != Dir.None) Arrow(o, c, aim, s, 40, 14, 10, Fade(InputWatch.Blocking ? Blue : Gold, a), outward: true, edge: true, a);

            if (settings.ArrowIncoming)
            {
                // One red arrow for each side a swing comes from (a group fight shows them all); the blow landing soonest gets the largest.
                // Stronger arrows: larger and more opaque, always somewhat see-through.
                bool strongArrows = settings.StrongIncomingArrows;
                float strong = strongArrows ? Math.Clamp(a * 1.6f, 0.55f, 0.75f) : a;
                int? soonest = ScreenSide(s_incoming, settings.MirrorGuards);
                int sides = s_incomingSides, shown = 0;
                for (int code = 1; code < 16; code++)
                {
                    if ((sides & (1 << code)) == 0 || ScreenSide(code, settings.MirrorGuards) is not { } screen) continue;
                    if (screen == soonest || (shown & (1 << screen)) != 0) continue;
                    shown |= 1 << screen;
                    if (strongArrows) Arrow(o, c, screen, s, 66, 15, 10, Fade(Red, strong * 0.85f), outward: false, edge: true, strong * 0.85f);
                    else Arrow(o, c, screen, s, 66, 14, 10, Fade(Red, a), outward: false, edge: true, a);
                }
                if (soonest is { } first)
                {
                    if (strongArrows) Arrow(o, c, first, s, 64, 19, 13, Fade(Red, strong), outward: false, edge: true, strong);
                    else Arrow(o, c, first, s, 66, 14, 10, Fade(Red, a), outward: false, edge: true, a);
                }
            }
            o.Flush(w, hgt);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("direction arrows", e);
        }
    }

    static readonly Vector4 ReticleInk = new(0.95f, 0.9f, 0.8f, 0.85f), ReticleRing = new(0.95f, 0.9f, 0.8f, 0.5f);

    /// <summary>The crossbow's reticle: a dot at the crosshair and a ring as wide as the spread a bolt may stray by.</summary>
    static void Reticle(DebugOverlay o, int w, int hgt, float s, Vector2 c)
    {
        float radius = 0;
        float spread = Volatile.Read(ref s_spread);
        if (spread > 0 && InputWatch.CrosshairRay() is { } ray)
        {
            var (origin, dir) = ray;
            var strayed = Meitou.Simulation.Combat.RangedFormulas.Deviate(dir, spread, 0);
            if (InputWatch.Project(origin + dir * 100) is { } a && InputWatch.Project(origin + strayed * 100) is { } b) radius = Vector2.Distance(a, b);
        }
        radius = Math.Clamp(radius, 0, hgt * 0.45f);

        o.Flush(w, hgt);
        float d = 2.2f * s;
        o.Rect(c.X - d - 1, c.Y - d - 1, c.X + d + 1, c.Y + d + 1, Edge);
        o.Rect(c.X - d, c.Y - d, c.X + d, c.Y + d, ReticleInk);

        ReloadBar(o, c, s);
        if (radius > d + 3)
        {
            const int n = 48;
            for (int k = 0; k < n; k++)
            {
                float a0 = k * MathF.Tau / n, a1 = (k + 1) * MathF.Tau / n;
                o.Line(c.X + radius * MathF.Cos(a0), c.Y + radius * MathF.Sin(a0), c.X + radius * MathF.Cos(a1), c.Y + radius * MathF.Sin(a1),
                    Math.Max(1, 1.5f * s), ReticleRing);
            }
        }
        o.Flush(w, hgt);
    }

    static readonly Vector4 BarBack = new(0.05f, 0.04f, 0.03f, 0.7f), BarLoaded = new(0.9f, 0.86f, 0.75f, 0.9f),
        BarSaved = new(0.6f, 0.58f, 0.52f, 0.85f), BarEmpty = new(0.95f, 0.22f, 0.15f, 0.9f);

    /// <summary>
    /// The crossbow's reload bar under the crosshair, in two steps: the notch at a third is the point a reload that stops early is kept at. Full and
    /// pale: loaded. Filling gold: reloading (held, standing still). Grey: a reload stopped part way (what is kept). An empty bar has a red edge.
    /// </summary>
    static void ReloadBar(DebugOverlay o, Vector2 c, float s)
    {
        if (s_shots <= 0) return;
        float progress = Math.Clamp(Volatile.Read(ref s_reload), 0, 1);
        bool loaded = s_loaded > 0, reloading = s_reloading;
        float half = 55 * s, height = 6 * s, top = c.Y + 26 * s, left = c.X - half, right = c.X + half;
        float g = Math.Max(1, s);

        o.Rect(left - g, top - g, right + g, top + height + g, loaded || progress > 0 ? Edge : BarEmpty);
        o.Rect(left, top, right, top + height, BarBack);
        if (progress > 0)
            o.Rect(left, top, left + 2 * half * progress, top + height, loaded ? BarLoaded : reloading ? Gold : BarSaved);
        float notch = left + 2 * half / 3;
        o.Rect(notch - g, top - 2 * g, notch + g, top + height + 2 * g, Edge);
    }

    /// <summary>Out of a fight (no reticle): the reload bar alone, while a reload is held or one stopped part way is kept.</summary>
    static void ReloadBarOutOfFight(object loop, object[] args)
    {
        if (s_inFight || !InputWatch.Active || s_shots <= 0 || s_loaded > 0) return;
        if (!s_reloading && Volatile.Read(ref s_reload) <= 0) return;
        if (Frame(loop, args, inFight: false) is not { } frame) return;
        var (o, w, hgt, s, c) = frame;
        o.Flush(w, hgt);
        ReloadBar(o, c, s);
        o.Flush(w, hgt);
    }

    static Vector4 Fade(Vector4 colour, float a) => colour with { W = colour.W * a };

    /// <summary>The fight-mode hint and the message line, after the interface (readable over it).</summary>
    static void DrawInterfacePostfix(object __instance, object[] __args)
    {
        try
        {
            var settings = ManualCombatMod.Settings;
            if (Frame(__instance, __args) is not { } frame) return;
            var (o, w, hgt, s, c) = frame;
            if (settings.HoldInFights && settings.FightHint) DrawHint(o, s, w, hgt, settings.HintTop);
            DrawLine(o, c, s, w, hgt);
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("fight hint", e);
        }
    }

    const string HintText = "Fight mode: W A S D step   U leaves the fight";
    const string RangedHintText = "Crossbow: left click fires   hold left click to reload   hold right click steadies   X: melee";
    static readonly Vector4 HintInk = new(0.86f, 0.82f, 0.72f, 0.85f);

    /// <summary>The fight-mode hint: small, right-aligned at the top right, under PZA's WASD window and its tiny button.</summary>
    static void DrawHint(DebugOverlay o, float s, int w, int hgt, int topAt1080)
    {
        string text = Ranged ? RangedHintText : HintText;
        float scale = Math.Max(0.75f, 0.8f * s);
        float width = o.MeasureText(text, scale);
        float x = w - width - 18 * s, top = topAt1080 * s;
        o.TextScaled(text, x + 1, top + 1, Edge, scale);
        o.TextScaled(text, x, top, HintInk, scale);
        o.Flush(w, hgt);
    }

    /// <summary>The message line, centred under the arrows, fading out at its end.</summary>
    static void DrawLine(DebugOverlay o, Vector2 c, float s, int w, int hgt)
    {
        if (s_line is not { } line) return;
        long age = Environment.TickCount64 - s_lineAt;
        if (age > LineMs)
        {
            s_line = null;
            return;
        }
        float alpha = age < LineMs - FadeMs ? 1 : (LineMs - age) / (float)FadeMs;
        float scale = Math.Max(1, s);
        float width = o.MeasureText(line, scale);
        float x = c.X - width / 2, top = c.Y + 90 * s;
        o.TextScaled(line, x + scale, top + scale, Edge with { W = 0.8f * alpha }, scale);
        o.TextScaled(line, x, top, Gold with { W = alpha }, scale);
        o.Flush(w, hgt);
    }

    /// <summary>Where on screen a blow code's arrow goes (left, right, top = overhead or upward, bottom = thrust); none from behind.</summary>
    static int? ScreenSide(int blow, bool mirror) => blow switch
    {
        Dir.Left => mirror ? Dir.Right : Dir.Left,
        Dir.Right => mirror ? Dir.Left : Dir.Right,
        Dir.Overhead or Dir.Up => Dir.Overhead,
        Dir.Thrust or 6 => Dir.Thrust,
        _ => null,
    };

    /// <summary>A triangle on <paramref name="side"/> of <paramref name="c"/>, <paramref name="at"/> px out, pointing out or in (sizes at 1080p).</summary>
    static void Arrow(DebugOverlay o, Vector2 c, int side, float s, float at, float length, float half, Vector4 colour, bool outward, bool edge,
        float alpha = 1)
    {
        var d = side switch
        {
            Dir.Left => new Vector2(-1, 0),
            Dir.Right => new Vector2(1, 0),
            Dir.Overhead => new Vector2(0, -1),
            _ => new Vector2(0, 1),
        };
        var across = new Vector2(-d.Y, d.X);
        var near = c + d * (at * s);
        var far = c + d * ((at + length) * s);
        var (tip, baseMid) = outward ? (far, near) : (near, far);
        var b1 = baseMid + across * (half * s);
        var b2 = baseMid - across * (half * s);
        if (edge)
        {
            float g = 2 * s;
            var dir = Vector2.Normalize(tip - baseMid);
            o.Triangle(tip + dir * g, b1 + across * g - dir * g, b2 - across * g - dir * g, Edge with { W = Edge.W * alpha });
        }
        o.Triangle(tip, b1, b2, colour);
    }
}
