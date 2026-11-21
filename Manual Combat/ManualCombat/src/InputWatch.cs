// Manual Combat for Meitou. GPLv3 or later.
// The interface-thread side: the key actions, the aim (mouse flick or movement keys), the W A S D footwork, which character is fought by hand,
// and the in-fight clicks. Nothing here touches the world: it only queues commands.
//
//  * The hand-fought character is the first selected one (the one PZA's WASD mod steers), followed as the selection changes, while manual combat
//    is on (O turns it off and on).
//  * In a fight, the game's Select button (left click) attacks and its Command button (right click) blocks: a prefix on PlayerInterface.MouseDown
//    takes a world click with either button and turns it into the attack or the block, so the game's own click (select, order) does not happen;
//    the release in MouseUp ends the block. They follow the player's own Select and Command bindings (Controls page), and the attack and block
//    actions can be bound to other keys as well. Clicks on windows and the HUD are left alone. Outside fights nothing changes.
//  * The move keys and the mouse are read in a postfix on InputBindings.Resolve that runs FIRST among that method's postfixes: PZA's WASD mod
//    clears the move actions in its own Resolve postfix while it steers.
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Modding;
using Meitou.Simulation;

namespace ManualCombat;

public static partial class InputWatch
{
    static ModContext s_ctx = null!;
    static ModKeyAction? s_attack, s_block, s_dodge, s_reload;
    /// <summary>The reload hold last sent, and left click held on an empty crossbow in a fight (a reload, not a shot).</summary>
    static bool s_reloadSent, s_reloadByMouse;

    /// <summary>Manual combat on (O flips it).</summary>
    public static bool Enabled { get; private set; } = true;

    static System.Reflection.FieldInfo? s_pzaOn;
    static bool s_pzaLooked;

    /// <summary>
    /// Whether PZA's WASD Control is loaded and on (its <c>PZA.WASDControl.Meitou.Driver.On</c>). Manual combat needs it: the camera, the crosshair
    /// and the movement are PZA's. False when PZA's mod is missing.
    /// </summary>
    public static bool WasdOn
    {
        get
        {
            if (!s_pzaLooked)
            {
                s_pzaLooked = true;
                s_pzaOn = AccessTools.TypeByName("PZA.WASDControl.Meitou.Driver") is { } driver ? AccessTools.Field(driver, "On") : null;
                ManualCombatMod.Log(s_pzaOn is null
                    ? "PZA's WASD & Controller Support not found: manual combat stays off (it needs WASD movement)"
                    : "found PZA's WASD & Controller Support: manual combat follows its WASD on/off");
            }
            return s_pzaOn?.GetValue(null) is true;
        }
    }

    /// <summary>Manual combat is on (O) and WASD movement is on.</summary>
    public static bool Active => Enabled && WasdOn;
    /// <summary>The pointer at the last control tick (window pixels): where the arrows are drawn.</summary>
    public static Vector2 Pointer { get; private set; }
    /// <summary>Whether block is held (the arrow turns blue).</summary>
    public static bool Blocking => s_blockSent;
    /// <summary>The block is held with the Command mouse button (released in MouseUp, not by the action's state).</summary>
    static bool s_blockByMouse;
    /// <summary>The side a press aims at now, by the setting.</summary>
    public static int AimNow => s_testAim != Dir.None ? s_testAim : ManualCombatMod.Settings.AimWith == ManualCombatSettings.AimKeys ? s_keyDir : s_mouseDir;

    static int s_keyDir, s_mouseDir;
    static Vector2 s_drift;

    static CharacterId s_controlled = CharacterId.None;
    static bool s_blockSent;
    static int s_blockSentDir;
    static Vector2 s_moveSent;
    static long s_moveSentAt, s_controlTick;

    /// <summary>The game's PlayerInterface (internal), caught once a frame in its Steer.</summary>
    static object? s_player;
    static MethodInfo? s_notice, s_selectedIds, s_overHud;
    static FieldInfo? s_session;
    static PropertyInfo? s_menuWindow;
    static long s_lastNoticeAt;
    static string? s_lastNotice;
    /// <summary>The mouse buttons whose press the in-fight clicks took (their release is taken too).</summary>
    static readonly bool[] s_taken = new bool[8];

    public static void Register(ModContext c)
    {
        s_ctx = c;
        // Left and right click are the game's Select and Command (taken in fights by MouseDownPrefix): these are extra keys, unbound by default.
        s_attack = c.AddKeyAction("fight-attack", "Attack (also Select in a fight)", OnAttack);
        s_block = c.AddKeyAction("fight-block", "Block, hold (also Command in a fight)", OnBlock);
        s_dodge = c.AddKeyAction("fight-dodge", "Dodge (out of fights: hold to reload)", OnDodge, MouseButton.Button4);
        c.AddKeyAction("fight-target", "Switch target", OnTarget, MouseButton.Button5);
        c.AddKeyAction("fight-leave", "Leave the fight", OnLeave, Key.U);
        c.AddKeyAction("fight-swap", "Swap crossbow / melee", OnSwap, Key.X);
        s_reload = c.AddKeyAction("fight-reload", "Reload crossbow (hold)", OnReload);
        c.AddKeyAction("manual-toggle", "Manual combat on/off", OnToggle, Key.O);
    }

    public static List<string> Patch(Harmony h)
    {
        var lines = new List<string>();
        var resolve = AccessTools.Method(typeof(InputBindings), "Resolve", [typeof(TickInput)])
            ?? throw new MissingMethodException("InputBindings.Resolve(TickInput)");
        h.Patch(resolve, postfix: new HarmonyMethod(AccessTools.Method(typeof(InputWatch), nameof(ResolvePostfix))) { priority = Priority.First });
        lines.Add("patched InputBindings.Resolve (first postfix)");

        var playerType = typeof(InputBindings).Assembly.GetType("Meitou.Game.PlayerInterface")
            ?? throw new TypeLoadException("Meitou.Game.PlayerInterface");
        var steer = AccessTools.Method(playerType, "Steer", [typeof(bool)]) ?? throw new MissingMethodException("PlayerInterface.Steer");
        s_selectedIds = AccessTools.Method(playerType, "SelectedIds", Type.EmptyTypes) ?? throw new MissingMethodException("PlayerInterface.SelectedIds");
        s_session = AccessTools.Field(playerType, "Session");
        if (s_session is null || s_session.FieldType != typeof(WorldSession)) throw new MissingFieldException("PlayerInterface.Session");
        h.Patch(steer, prefix: new HarmonyMethod(AccessTools.Method(typeof(InputWatch), nameof(SteerPrefix))));
        lines.Add("patched PlayerInterface.Steer (selection, camera, messages)");

        s_notice = AccessTools.Method(playerType, "Notice", [typeof(string), typeof(bool), typeof(string)]);
        if (s_notice is null) lines.Add("on-screen messages unavailable in this Meitou version; the log still has everything");

        // The in-fight clicks: optional (without them the mod's keys still work as bound).
        var down = AccessTools.Method(playerType, "MouseDown");
        var up = AccessTools.Method(playerType, "MouseUp");
        s_overHud = AccessTools.Method(playerType, "OverHud", [typeof(Vector2)]);
        s_menuWindow = AccessTools.Property(playerType, "ContextMenuWindow");
        if (down is not null && up is not null && s_overHud is not null)
        {
            h.Patch(down, prefix: new HarmonyMethod(AccessTools.Method(typeof(InputWatch), nameof(MouseDownPrefix))) { priority = Priority.First });
            h.Patch(up, prefix: new HarmonyMethod(AccessTools.Method(typeof(InputWatch), nameof(MouseUpPrefix))) { priority = Priority.First });
            lines.Add("patched PlayerInterface.MouseDown / MouseUp (left/right click fight in a fight)");
        }
        else lines.Add("in-fight clicks unavailable in this Meitou version: bind attack and block to other keys under Controls");
        return lines;
    }

    #region Once a frame: the selection

    static void SteerPrefix(object __instance)
    {
        s_player = __instance;
        try
        {
            if (s_ctx is null) return;
            if (!Active)
            {
                // WASD movement off (or manual combat off): the character goes back to Kenshi's own fighting.
                if (!s_controlled.IsNone)
                {
                    Send(new ManualControl(s_controlled, false) { Tick = s_ctx.Tick });
                    s_controlled = CharacterId.None;
                    s_blockSent = false;
                    s_reloadSent = s_reloadByMouse = false;
                    if (Enabled) ManualCombatMod.Notify("WASD off - manual combat off");
                }
                return;
            }
            if (s_selectedIds!.Invoke(__instance, null) is not IReadOnlyList<CharacterId> { Count: > 0 } ids || ids[0].IsNone) return;
            var first = ids[0];
            if (first == s_controlled) return;

            s_controlled = first;
            s_moveSent = Vector2.Zero;
            s_blockSent = false;
            s_reloadSent = s_reloadByMouse = false;
            Send(new ManualControl(first, true) { Tick = s_ctx.Tick });
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("Steer prefix", e);
        }
    }

    #endregion

    #region Each control tick: aim, footwork, block release, messages

    static void ResolvePostfix(TickInput input, ActionState __result)
    {
        try
        {
            s_controlTick++;
            ShowNotices();
            TestChannel.Poll();
            Pointer = input.MousePosition;
            s_keyDir = KeyDirection(__result);
            // Alt held (PZA's free cursor, the game's highlight): clicks are the game's again, even in a fight, and the mouse does not aim.
            s_alt = input.IsDown(Key.LeftAlt) || input.IsDown(Key.RightAlt);
            if (!s_alt) TrackFlick(input.MouseDelta);
            SendFootwork(__result);
            SendReloadHold(__result);

            // The block: let go (the block key; the mouse's release comes in MouseUp), or re-aimed while held.
            if (!s_blockSent) return;
            int aim = AimNow;
            if (!s_blockByMouse && (s_block is null || (int)s_block.Action < (int)InputBindings.FirstModAction || !__result.Held(s_block.Action)))
            {
                Send(new ManualBlock(s_controlled, false, aim) { Tick = s_ctx.Tick });
                s_blockSent = false;
            }
            else if (aim != s_blockSentDir)
            {
                Send(new ManualBlock(s_controlled, true, aim) { Tick = s_ctx.Tick, InPause = GamePaused });
                s_blockSentDir = aim;
            }
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("Resolve postfix", e);
        }
    }

    /// <summary>
    /// The crossbow reload is held while the crossbow is empty and: left click is held on it in a fight, Mouse 4 (the dodge key) out of fights, or the
    /// reload key anywhere. Sent when it changes; the reload runs only while held and standing still (ManualCombatSystem.ReloadControl).
    /// </summary>
    static void SendReloadHold(ActionState a)
    {
        if (s_controlled.IsNone) return;
        bool want = Active && Hud.HasCrossbow && Hud.Empty
            && (s_reloadByMouse || KeyHeld(s_reload, a) || !Hud.InFight && KeyHeld(s_dodge, a));
        if (want == s_reloadSent) return;
        Send(new ManualReload(s_controlled, want) { Tick = s_ctx.Tick });
        s_reloadSent = want;
    }

    static bool KeyHeld(ModKeyAction? key, ActionState a) =>
        key is not null && (int)key.Action >= (int)InputBindings.FirstModAction && a.Held(key.Action);

    /// <summary>A or D: left or right (both or neither: not a side); else W overhead, S thrust; nothing: Dir.None.</summary>
    static int KeyDirection(ActionState a)
    {
        bool left = a.Held(InputAction.MoveLeft), right = a.Held(InputAction.MoveRight);
        bool forward = a.Held(InputAction.MoveForward), back = a.Held(InputAction.MoveBack);
        if (left != right) return left ? Dir.Left : Dir.Right;
        if (forward != back) return forward ? Dir.Overhead : Dir.Thrust;
        return Dir.None;
    }

    /// <summary>
    /// Mount & Blade's aim: the side of the last clear mouse movement, kept until the next. A movement of at least the sensitivity in one control
    /// tick switches at once (a flick); slower movement adds up, fading, and switches once it is twice that. A side wins only when it is clearly
    /// more than the other axis (1.3x), so a diagonal keeps the current side.
    /// </summary>
    static void TrackFlick(Vector2 delta)
    {
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
        float px = Math.Max(1, ManualCombatMod.Settings.FlickPixels);
        if (delta.Length() >= px && Side(delta) is { } quick)
        {
            s_mouseDir = quick;
            s_drift = Vector2.Zero;
            return;
        }
        s_drift = s_drift * 0.5f + delta;
        if (s_drift.Length() >= 2 * px && Side(s_drift) is { } slow)
        {
            s_mouseDir = slow;
            s_drift = Vector2.Zero;
        }

        static int? Side(Vector2 v)
        {
            float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y);
            if (ax >= 1.3f * ay) return v.X < 0 ? Dir.Left : Dir.Right;
            if (ay >= 1.3f * ax) return v.Y < 0 ? Dir.Overhead : Dir.Thrust;   // pixels: y grows down
            return null;
        }
    }

    /// <summary>The camera's flat forward (X, Z), as PZA's steering takes it; zero without a camera.</summary>
    static MethodInfo? s_ray;
    static FieldInfo? s_width, s_height;

    /// <summary>The ray through the centre of the screen (the crosshair): the camera's near-plane point and direction, or null.</summary>
    public static (Vector3 Origin, Vector3 Direction)? CrosshairRay(object? player = null)
    {
        player ??= s_player;
        if (player is null) return null;
        var t = player.GetType();
        s_ray ??= AccessTools.Method(t, "Ray", [typeof(Vector2)]);
        s_width ??= AccessTools.Field(t, "Width");
        s_height ??= AccessTools.Field(t, "Height");
        if (s_ray is null || s_width?.GetValue(player) is not int w || s_height?.GetValue(player) is not int h) return null;
        return s_ray.Invoke(player, [new Vector2(w / 2f, h / 2f)]) as (Vector3, Vector3)?;
    }

    /// <summary>The screen pixel of a world point, or null (behind the camera).</summary>
    public static Vector2? Project(Vector3 p)
    {
        if (s_player is not { } player) return null;
        s_project ??= AccessTools.Method(player.GetType(), "Project", [typeof(Vector3)]);
        return s_project?.Invoke(player, [p]) as Vector2?;
    }
    static MethodInfo? s_project;

    /// <summary>The game is paused (a press now cannot be timed against the swings: no parry, chamber or combo from it).</summary>
    static bool GamePaused => s_player is not null && s_session?.GetValue(s_player) is WorldSession session && session.Simulation.IsPaused;

    static Vector2 CameraForward()
    {
        if (s_player is null || s_session?.GetValue(s_player) is not WorldSession session) return Vector2.Zero;
        float yaw = session.Camera.Strategy.Yaw;
        return new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
    }

    /// <summary>
    /// The hand-fought character's footwork push from W A S D, camera-relative (forward = away from the camera). Sent when it changes: at once when
    /// a key goes down or up, at most every 3 control ticks while the camera turns with a key held.
    /// </summary>
    static void SendFootwork(ActionState a)
    {
        if (s_controlled.IsNone) return;
        var forward = CameraForward();
        if (forward == Vector2.Zero) return;

        float fwd = (a.Held(InputAction.MoveForward) ? 1 : 0) - (a.Held(InputAction.MoveBack) ? 1 : 0);
        float right = (a.Held(InputAction.MoveRight) ? 1 : 0) - (a.Held(InputAction.MoveLeft) ? 1 : 0);
        var side = new Vector2(-forward.Y, forward.X);   // (cos yaw, -sin yaw)
        var want = fwd != 0 || right != 0 ? Vector2.Normalize(forward * fwd + side * right) : Vector2.Zero;

        bool startStop = (want == Vector2.Zero) != (s_moveSent == Vector2.Zero);
        bool turned = want != Vector2.Zero && Vector2.Dot(want, s_moveSent) < 0.995f && s_controlTick - s_moveSentAt >= 3;
        if (!startStop && !turned) return;

        Send(new ManualMove(s_controlled, want) { Tick = s_ctx.Tick });
        s_moveSent = want;
        s_moveSentAt = s_controlTick;
    }

    /// <summary>
    /// Shows the queued messages under the crosshair (Hud), the newest replacing the last, so a line is never late. Outside fights (manual combat
    /// on or off) there is no crosshair line: those go to the game's message panel.
    /// </summary>
    static void ShowNotices()
    {
        while (ManualCombatMod.NextNotice() is { } text)
        {
            if (Hud.InFight) Hud.Show(text);
            else if (s_player is not null && s_notice is not null)
            {
                long now = Environment.TickCount64;
                if (text == s_lastNotice && now - s_lastNoticeAt < 2000) continue;
                s_lastNotice = text;
                s_lastNoticeAt = now;
                s_notice.Invoke(s_player, [text, false, null]);
            }
        }
    }

    #endregion

    #region The in-fight clicks

    /// <summary>Whether the in-fight controls are live: manual combat on, a hand-fought character, and it is in a fight.</summary>
    static bool Live => Active && !s_controlled.IsNone && Hud.InFight;

    /// <summary>Alt is held (as of the last control tick): the game's own clicks in a fight (loot, doors, talk, orders).</summary>
    static volatile bool s_alt;

    static bool MouseDownPrefix(object __instance, MouseButton button, Vector2 p, ref bool alt, ref bool __result)
    {
        try
        {
            // Alt + right click in a fight: the game's plain right click (open, loot, talk, trade, orders), as without Alt out of a fight.
            if (Live && s_alt && button == MouseButton.Right)
            {
                alt = false;
                return true;
            }
            if (!Live || !ManualCombatMod.Settings.InFightClicks || s_alt || (int)button >= s_taken.Length) return true;
            var role = FightRole(__instance, button);
            if (role == 0) return true;
            if ((bool)s_overHud!.Invoke(__instance, [p])!) return true;   // a window or the HUD: its click
            if (s_menuWindow?.GetValue(__instance) is { } menu && menu.GetType().GetProperty("ContextMenuOpen")?.GetValue(menu) is true) return true;

            s_taken[(int)button] = true;
            __result = true;   // taken: no select, drag or order
            long tick = s_ctx.Tick;
            if (role == 1 && Hud.Ranged && Hud.Empty) s_reloadByMouse = true;   // held on an empty crossbow: the reload (SendReloadHold)
            else if (role == 1 && Hud.Ranged && CrosshairRay(__instance) is { } ray)
                Send(new ManualShot(s_controlled, ray.Origin, ray.Direction) { Tick = tick });
            else if (role == 1) Send(new ManualAttack(s_controlled, AimNow, CameraForward()) { Tick = tick, InPause = GamePaused });
            else if (!s_blockSent)
            {
                int aim = AimNow;
                Send(new ManualBlock(s_controlled, true, aim) { Tick = tick, InPause = GamePaused });
                s_blockSent = true;
                s_blockByMouse = true;
                s_blockSentDir = aim;
            }
            return false;
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("MouseDown prefix", e);
            return true;
        }
    }

    static bool MouseUpPrefix(MouseButton button)
    {
        try
        {
            if ((int)button >= s_taken.Length || !s_taken[(int)button]) return true;
            s_taken[(int)button] = false;
            if (FightRoleOf(button) == 1) s_reloadByMouse = false;
            if (s_blockSent && s_blockByMouse && FightRoleOf(button) == 2)
            {
                Send(new ManualBlock(s_controlled, false, AimNow) { Tick = s_ctx.Tick });
                s_blockSent = false;
                s_blockByMouse = false;
            }
            return false;   // its press was a fight click: no click, drag or order ends here
        }
        catch (Exception e)
        {
            ManualCombatMod.LogException("MouseUp prefix", e);
            return true;
        }
    }

    /// <summary>The roles of the mouse buttons the last fight click saw (1 attack, 2 block), for the release.</summary>
    static readonly int[] s_roles = new int[8];

    static int FightRoleOf(MouseButton button) => (int)button < s_roles.Length ? s_roles[(int)button] : 0;

    /// <summary>1 when <paramref name="button"/> is the game's Select button, 2 its Command button, else 0 (the player's bindings).</summary>
    static int FightRole(object player, MouseButton button)
    {
        if (s_session?.GetValue(player) is not WorldSession session) return 0;
        var b = Binding.Of(button);
        int role = session.Bindings.Get(InputAction.Select).Contains(b) ? 1 : session.Bindings.Get(InputAction.Command).Contains(b) ? 2 : 0;
        if ((int)button < s_roles.Length) s_roles[(int)button] = role;
        return role;
    }

    #endregion

    #region The actions

    static void OnAttack(ModKeyContext k)
    {
        if (!Live) return;
        k.Enqueue(new ManualAttack(s_controlled, AimNow, CameraForward()) { Tick = k.Tick, InPause = GamePaused });
    }

    static void OnBlock(ModKeyContext k)
    {
        if (!Live || s_blockSent) return;
        s_blockByMouse = false;
        int aim = AimNow;
        k.Enqueue(new ManualBlock(s_controlled, true, aim) { Tick = k.Tick, InPause = GamePaused });
        s_blockSent = true;
        s_blockSentDir = aim;
    }

    static void OnDodge(ModKeyContext k)
    {
        if (!Live) return;
        k.Enqueue(new ManualDodge(s_controlled) { Tick = k.Tick });
    }

    static void OnTarget(ModKeyContext k)
    {
        if (!Live) return;
        k.Enqueue(new ManualTarget(s_controlled, CameraForward()) { Tick = k.Tick });
    }

    /// <summary>The reload key is held, not pressed: read each control tick (SendReloadHold).</summary>
    static void OnReload(ModKeyContext k)
    {
    }

    static void OnSwap(ModKeyContext k)
    {
        if (!Live) return;
        k.Enqueue(new ManualSwap(s_controlled) { Tick = k.Tick });
    }

    static void OnLeave(ModKeyContext k)
    {
        if (!Live) return;
        if (s_blockSent) k.Enqueue(new ManualBlock(s_controlled, false, AimNow) { Tick = k.Tick });
        s_blockSent = false;
        s_blockByMouse = false;
        k.Enqueue(new ManualLeave(s_controlled) { Tick = k.Tick });
    }

    static void OnToggle(ModKeyContext k)
    {
        Enabled = !Enabled;
        if (Enabled)
        {
            s_controlled = CharacterId.None;   // the next frame takes the selection again
            ManualCombatMod.Notify(WasdOn ? "Manual combat ON" : "Manual combat ON - turn on WASD movement to use it");
        }
        else
        {
            if (!s_controlled.IsNone) k.Enqueue(new ManualControl(s_controlled, false) { Tick = k.Tick });
            s_controlled = CharacterId.None;
            s_blockSent = false;
            s_reloadSent = s_reloadByMouse = false;
        }
    }

    #endregion

    static void Send(SimCommand c) => s_ctx.Enqueue(c);
}
