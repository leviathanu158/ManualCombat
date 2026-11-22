# Manual Combat (Meitou code mod)

Full background is in `ManualCombat-handover.txt` (the original claude.ai handover). This file has the parts every session needs.

## Project
- Mount & Blade style melee for **MeitouClient**, the open-source C#/.NET reimplementation of Kenshi (github.com/VlcekM/MeitouClient). It adds directional attacks aimed by mouse flick, hold-to-block, parries, feints, combos, clashing, chambering, hyper armor and manual crossbows.
- Works together with PZA's "WASD & Controller Support" mod (github.com/PZA-Mods/PZAMeitouWASDController). Manual combat is active only while PZA's WASD is on.
- License: GPLv3. Current version: **0.9.26**.
- Source: `Manual Combat\ManualCombat\` (src\*.cs, ManualCombat.csproj, meitou.json, build.bat/build.ps1, README.md).
- Meitou reference source (read-only, outside this repo): `C:\KenshiRef\MeitouClient` (git checkout at the commit of the installed Meitou build).

## The user (Leviathanu): how to work with him
- He has no coding knowledge. Claude writes ALL code; he tests in-game and gives feedback.
- Use plain words and compact replies, and keep feature lists short.
- He makes the design decisions. Ask him about real design choices. Otherwise pick a sensible default and say what you picked.
- Editing this project folder is fine. **Don't write into his Meitou install folders (C:\Kenshi_Meitou\...) unless he asks.** build.bat installing into Kenshi\mods\ManualCombat is expected.

## Standing rules
- Everything the mod changes applies **only to the hand-fought character** (first selected) and **only while manual combat (O) and PZA's WASD are both on**. With either off, plain Kenshi/Meitou behaviour takes over for everyone. Every new feature must check `ManualCombatSystem.Controls` (the bound character), which is released when either goes off.
- `meitou.json` gameVersion must be `">=0.22.2"` with **no upper cap** (never `"<0.23.0"`).
- Use only current Meitou information (no more than a week old). **Always read the Meitou source that matches his installed version** before using or reflecting into any Meitou API.
- **Version bump on every build** he gets: meitou.json `"version"`, ManualCombat.csproj `<Version>`, and the README `(vX.Y.Z)`.
- Before handing over a build:
  - check brace and paren balance;
  - check every reflected name and signature against the Meitou source for his version;
  - don't create duplicate static field names (this has broken the build twice);
  - don't use C# collection expressions in ternaries (`cond ? [] : x`);
  - don't use `is var (a,b)` on nullable tuples;
  - key action labels (AddKeyAction) are at most 48 characters on one line, or Meitou refuses to load the whole mod (0.9.13's first build).
- After every build, start the game once and check log.txt for `manualcombat: ready`; "not loaded: failed to start" means the mod is off.
- All world changes go through SimCommands, and ALL mod state is hashed (IStateHashed), for determinism and future multiplayer.

## His PC
- Meitou install: `C:\Kenshi_Meitou`. Current version: `versions\0.22.2-nightly.20261122.2\` (commit d0fa4495d1a0689aab9e640f5c96ae15729ff0a4, since 2026-11-22; before: 0.22.2-nightly.20261121, 802c1ca). The build's commit is stamped in MeitouClient.dll ("nightly.<date>+<sha>"); C:\KenshiRef\MeitouClient is checked out at it. He runs the Vulkan renderer.
- .NET SDK 11.0.100 (10.0.100 also installed).
- Game log: `C:\Kenshi_Meitou\versions\0.22.2-nightly.20261121\log.txt` (next to the game exe; follows the version). Mod lines start with `mod       manualcombat:`, and a session starts at the last `manualcombat: ready`.
- Kenshi: `C:\Steam Games\steamapps\common\Kenshi`. The mod installs to `Kenshi\mods\ManualCombat`.
- Workshop mods: `C:\Steam Games\steamapps\workshop\content\233860`. He runs a large mod list that includes the animation mods Great Anims Mod (2725549481), GROUNDED2 (3289699139) and More Combat Animation (1691422256). With his full list, every weapon swings from all four directions.
- Launcher config: `%LOCALAPPDATA%\Meitou\launcher.json` (InstallFolder, KenshiPath). Saves: `%LOCALAPPDATA%\Meitou\save\` (the test save is `mctest`).
- Test scripts: `C:\Kenshi_Meitou\test\` (start-test-game.bat, check-channel.bat). Control dir: `C:\Kenshi_Meitou\ctl\`. `meitou-tools.exe` (probe, mod-check) is in the version folder.
- `kenshi-workshop-pipeline\` (beside this file) is a separate SteamCMD upload tool. It is not part of the mod repo.

## Building
- Run `Manual Combat\ManualCombat\build.bat`, which calls build.ps1. The script finds the .NET SDK, reads launcher.json and finds `versions\*<wantVersion>*\MeitouClient.dll`. **When he moves to a newer Meitou, update `$wantVersion` in build.ps1.**
- It builds net11.0 (falling back to net10.0), stages `out\ManualCombat` (dll, meitou.json, empty ManualCombat.mod) and installs to Kenshi\mods\ManualCombat. Use `-NoInstall` to skip the install.
- It references MeitouClient, Meitou.Simulation, Meitou.Core, Meitou.Data and 0Harmony with Private=false.

## Architecture (src/)
- **ManualCombatMod.cs**: Initialize (settings, key actions, AddSystem after StandardParts.Combat, AddCombatMove, Harmony), Notify, Log, and rate-limited LogException.
- **ManualCombatSystem.cs**: ITickSystem + IStateHashed + ICombatHitFilter. SimCommands are handled in Inputs. Writes happen in Schedule, which runs after combat's Schedule and writes CombatSystem._write. Handles combos, clash and crush-through, chamber (HitLanding), hyper armor poise, crossbow, held guard, feints and leaving fights.
- **CombatPatches.cs**: reflection-by-name into Meitou internals. The mod turns itself off if anything is missing. It patches:
  - attacks and defence: StartAttack, TechniqueChooser.ChooseAttack, ChooseDefence, StartDefence, ApplyBlocked (parry to Stagger);
  - initiative and slots: HasInitiative, MayJoin/HasSlot;
  - movement, blocking and combo timing: CombatSteering.Velocity, ReactionEnds, PlayRate;
  - crossbow: Aim/Reload/ImmediateThreat/ChooseMode, ApplyFire/LineOfFire;
  - hyper armor: Stagger.
- **InputWatch.cs**: mouse flick aim; PlayerInterface MouseDown/MouseUp prefixes (LMB = Select, RMB = Command, only in fights and not over the HUD); Resolve postfix (priority First, before PZA); auto-bind of the selected character; PZA check via `PZA.WASDControl.Meitou.Driver.On`; Alt handling; pause flag.
- **FightHold.cs**: CommandQueue.TakeDue postfix. It drops MoveOrder/StopCommand that name ONLY the manual character while it fights, because a Kenshi move order ends a fight.
- **Hud.cs**: DrawInterface prefix/postfix (arrows, crossbow reticle, fight hint, message line) and the red outline on the locked target.
- **Targets.cs**: enemy picking (crosshair cone of 35°, reach). **Commands.cs**: SimCommand records. **ManualCombatSettings.cs**: all settings. **TestChannel.cs**: file-driven test input.

Kenshi facts:
- Blow direction tags are DEFENDER-relative: 1 overhead, 2 left, 3 right, 4 thrust. The mod mirrors L/R.
- The loser of an initiative roll yields 0.5 s, starting when the winner's block ends.
- HasSlot gives a target only 1 attack slot while it swings.
- Hit stagger is CombatSystem.Stagger.

## Current behaviour (0.9.0)
- **Controls**:
  - LMB attack, RMB hold block, flick aim (gold arrow).
  - M4 dodge, M5 switch target, U leave fight, O toggles manual combat, X swaps crossbow/melee.
  - WASD footwork. Alt+RMB is the game's plain right click.
- **Parry**: block within 0.3 s of the hit. The attacker staggers, and the riposte is x1.35 speed within 1.5 s.
- **Feints**: cancel only in the first 50% of the wind-up, then a 0.2 s recovery lock and 0.3 s lockout.
- **Combos**: after a hit, attack from a different side 0.05–0.25 s later. The swing is cut and the next one is +10% speed. Max 2 swings, then 0.8 s rest.
- **Clash**:
  - Both swings come from the same side and land within 0.12 s; only you and one enemy.
  - Crush-through power is Str + skill + 2×weight (cap 40).
  - Chance is (s−w)/(s+w)×1.5 (max 90%).
- **Chamber**: attack within the chamber window before an incoming blow lands, from the same direction, aimed as a block against that blow would be (0.9.9: was the mirrored attack side, wrong for left/right), and the weapon must really swing that way.
- **Hyper armor**:
  - Only while swinging. Poise = floor(Toughness/40 × scale).
  - A light hit costs 1 poise and a heavy hit 2. Poise refills after 5 s without a hit.
  - 0.5 s grace after any stagger.
- **Enemy pacing**: a lost roll's yield is capped at 0.5 s, and an enemy that keeps losing Kenshi's initiative roll swings anyway after the Longest enemy wait (default 2.5 s, his choice; added 0.9.9). How many swing at you at once is **Meitou's attack slots** setting (his decision; the mod's own cap was removed in 0.9.9).
- **Chamber safety (0.9.12)**: a landed chamber means no stagger until the chambering swing ends (ChamberSafeUntil, StaggerPrefix) and the same enemy swing's later blows are cancelled. Chamber window default stays 0.2 s; his own 0.5 was removed from his settings file at his request.
- **Crossbow aim (0.9.14)**: the bolt flies from the barrel to the point under the crosshair: the first living body (upright cylinder: race footprint radius, 18 units x scale) or the ground (TerrainHeight sampled every 2 units) along the camera ray (CombatPatches.CrosshairHit). Before, it aimed at the fight target's distance, so close up the barrel-camera offset (parallax) threw it off.
- **Animals (0.9.17, his choice)**: never taken on (ManualCombatSystem.IsAnimal via CharacterFacts.Animal on ManualControl): plain Kenshi fighting, "Manual combat: not for animals". Zagan in mctest is armed (not a fist test); whether he is an animal was not confirmed.
- **Fists (0.9.16, his design)**: fists can't block (Kenshi: Fighter.CanBlock false), so held right click dodges. Kenshi's dodge roll (Martial Arts EffectiveDefence vs attack skill) with the beaten chance scaled like blocks (BlockHolds / KenshiBlockChance). A perfect dodge (pressed within the Martial Arts parry window) always works and gives a riposte (his choice 2026-11-21: not an instant cut, the full attack animation, faster): the next swing within 1.5 s plays at the Riposte speed (PerfectDodge sets _riposteUntil). No parry and no chamber with fists. The dodge key (Mouse 4) gives the same perfect dodge for everyone (0.9.19, his request): pressed within the parry window before a blow lands and its dodge avoiding it (at 0.1–0.98 of the clip), "Perfect dodge!" and the riposte; not from a press made while paused. Letting go of right click lowers only a block, never a dodge under way (0.9.18 fix: before, a quick tap's release cancelled the dodge and the blow landed).
- **Windows by skill (0.9.15, his design; replaces the fixed 0.9.13 parry 0.2 / chamber 0.3)**: parry window from Defence, chamber window from Attack (Martial Arts for both with fists), effective skill as the stats panel shows it: under 10 none, 10–29 0.2 s, 30–49 0.3 s, 50–79 0.4 s, 80+ 0.5 s (ManualCombatSystem.SkillWindow, worked out each tick). The Parry window and Chamber window settings were removed.
- **Crossbow reload (0.9.13, his design)**: held and standing still, in and out of fights (no automatic reload any more). Hold left click on an empty crossbow in a fight, Mouse 4 (the dodge key) out of fights, or the unbound reload key. Let go or step (W A S D) and it stops: progress of a third or more is kept at a third, less is lost (ManualCombatSystem.ReloadControl; in fights Meitou's Reload counts only while ReloadGoes). A two-step reload bar under the crosshair (notch at a third) replaced "1:1"; out of fights it shows while reloading or with a kept part.
- **Crossbow HUD and zoom (0.9.12)**: "1:1"/"0:1" under the reticle (replaced by the reload bar in 0.9.13). Zoom while steadying in PZA's shoulder and first-person views (Zoom.cs, SandboxScenario.FollowCamera postfix Priority.Last, keeps PZA's ViewLens.s_ours in step by reflection; setting Zoom when steadied, default 0.5). Spread default 1.25 (his "slightly more"). Walking with the crossbow: the whole-body aim/reload clip is left out while the keys push (AnimationSystem.TechniqueOf postfix), so the fight walk plays; an upper-body aim over walking legs would need Meitou's clip layering changed (possible later).
- **Crossbow footwork and reload (0.9.9)**: W A S D step the character with the crossbow out (Meitou steers no crossbowman; PursuitSystem.Steer postfix + Velocity prefix); no key = stand. Out of fights an empty crossbow reloads by itself on the fight state's own ReloadLeft timer (his choice: automatic), no animation.
- **Pause**: presses made while paused can't parry, chamber or combo (the test channel is exempt).
- **Crossbow** (needs PZA's 3rd-person shoulder view, Z: in the Normal view the screen centre sits on your own character):
  - Reticle with a spread ring; LMB fires along the crosshair ray, with a deterministic roll.
  - LMB on empty reloads. Holding RMB steadies to 35% over 1 s.
  - No zoom yet.
- If the weapon lacks the aimed direction, it swings another side and shows "No Attack For That Direction".
- Log details setting: default OFF (it caused stutters).

## Decided 2026-11-20: block chance
- A held block in the right direction is NOT guaranteed. Kenshi's own block roll (Defence vs the attacker's Attack) still decides, so players can't beat huge stat gaps, but it is more generous than base Kenshi. Default: the chance of a block being beaten is Kenshi's × 0.5 (a setting).
- A **parry** (block pressed within the parry window before the hit) **always blocks**. Whether it always fully staggers the attacker is a later choice.
- 0.9.0 gave held blocks a fixed 100% (ChooseDefence prefix); replaced in 0.9.4 (ManualCombatSystem.BlockHolds).
- **Chambering** works against attackers in front or at the side, not from too far behind: up to 120° off the character's heading (ManualCombatSystem.ChamberArcDegrees, 0.9.6).
- **Blocking is always directional**: no setting (the user: "an integral part of the mod"). Removed the Directional blocking option in 0.9.4.
- Test runs must start Meitou with the working directory `%LOCALAPPDATA%\Meitou` (as the launcher does): Meitou reads `meitou.user.json` from the working directory, so starting in the version folder uses an almost empty settings file there instead of his.

## Rejected: don't re-propose
- U as Passive stance, and a "disengaged" mode (abusable). The chosen fix is Alt+right click.
- Filtering katana multi-hit moves, or refusing to swing when a direction is missing.
- Combos ×3 or chaining on any swing; hyper armor at any time with big pools; "enemies never wait".
- Melee-to-melee weapon swap: deferred (there is no simple inventory command).

## Settled investigations
- Hub Ninja Guards staring after you attack a Drifter: NOT the mod. It is a Meitou bug and he reported it.
- Odd idle pose after a fight (left arm forward): unconfirmed. Suspects are the held-block reaction or the crossbow pose surviving the fight. Needs a log with Log details on.

## Testing
- His build has `--control-dir` but NOT `--headless`. The game window must stay visible, or loading stalls. A load takes about 4–5 min.
- `C:\Kenshi_Meitou\test\start-test-game.bat` starts `MeitouClient.exe --windowed --load %LOCALAPPDATA%\Meitou\save\mctest --control-dir C:\Kenshi_Meitou\ctl`. It also sets KENSHI_PATH and MANUALCOMBAT_TEST_DIR.
- **Agent channel**:
  - Write `<id> <command>` to `ctl\cmd` atomically (temp file + rename), then read JSON lines from `ctl\out` (skip "event" lines).
  - Commands: state, observe --brief, select, spawn-at x z [record] --aggro, order attack, pause, step n, resume, heal, kill, tp, screenshot, quit.
  - The guide is Meitou's docs/agent-playtest.md.
- **Mod test channel**:
  - Write lines to `ctl\mc.cmd`: `aim <overhead|left|right|thrust>`, `attack [dir]`, `block on [dir]` / `block off`, `dodge`, `target`, `swap`, `reload`, `leave`, `report`.
  - Results go to `ctl\mc.out`.
  - Presses count as live while paused, so pause + step can time parries exactly.
- `meitou-tools mod-check <save>` runs code mods headless and compares state hashes (the determinism check).
- The mctest save: Su (c:0.0, all skills 100) and Zagan (c:1.0). WASD comes on by itself (PZA's "On at start" is ticked). The first spot was near a Shek town and its guards joined fights (they knocked out test bandits), so he is moving it somewhere secluded.
- The test channel can't press keys or move PZA's view. To use PZA's panel, click its "+" beside "WASD ON" (2538,153 at 2560x1351); Shoulder camera is at (1277,532) and the close button at (1612,291). `camera set` has no effect in PZA's shoulder view, so crossbow aiming there needs a human.
- Give items with the record ID (names with spaces get split). Give the crossbow before the bolts: bolts fill the free space one per square.
- Scripts (session scratchpad, may be gone): c.ps1 (agent channel), mc.ps1 (mod channel), start.ps1, timed.ps1 (press N ticks before a blow lands: block/attack/other/dodge).

## PZA collaboration
PZA plans an API in his WASD mod and will patch Manual Combat to use it. It will replace the Driver.On reflection, FightHold, Resolve priority, Alt handling and the camera/crosshair reads. **When his patched version comes back, it becomes the base: read his API and build on it.** Controller support is his.

## 0.9.20 (2026-11-22, his choices; all tested through the test channel)
- **Hold to ready**: left click held (ManualAttack Held, ManualAttackRelease on MouseUp) stops the swing at Ready point (0.7 of the way to blow 1) by moving AttackStart/AttackEnd on each tick; release strikes (the chamber is timed from the release); block while readied always feints. Enemies block readied swings with Kenshi's roll and animation (verified: 6/6 "Blocked" against a maxed bandit).
- **Cut the recovery**: after the last blow + RecoveryLock, block/dodge/step/attack ends the follow-through (AttackTech -1, CancelTick); ReadyTick = now + Kenshi's post-swing pause by outcome.
- **Blocking turns you** (TurnToBlocked): a held guard meets a blow from behind with the front guard for the swing's own side (FrontSide, only if it lands a tick later or more), sets ReactedSlot so Meitou's DefenceAim/LookAt turn the character, and retargets to that attacker (Retarget "blocked"; from ChooseDefence via BlockingFrom).
- **Animals**: wind-up slowed by Rescale in Schedule one tick after the swing starts (AttackStart == now-1, target the character, Fighter.IsAnimal or CharacterFacts.Animal: data-driven, so modded creatures count), back to full speed after the first blow, on a retarget, or on release. Bigger, still see-through red arrow (AnimalWarning).
- **PlayRate is inlined by the JIT**: the PlayRate postfix never fired, so ripostes/combos were never faster before 0.9.20. Speed changes now go through ManualCombatSystem.Rescale (keeps the clip's progress); the postfix stays and marks RateApplied so nothing doubles. Don't rely on patching small Meitou methods.
- **Slow motion (optional, PZA's SlowMo, SlowMo.cs)**: SlowMoApi found by name (Hold/Release on the host thread, from InputWatch's Resolve postfix). A short moment on parry and on perfect dodge (0.3x, 0.4 s real), the riposte swing at 0.5x until its blow + 0.2 s. All toggles/speeds/lengths are settings, shown only when SlowMo is loaded (SlowMo.Installed).

## 0.9.21 (2026-11-22, his requests; tested through the test channel, screenshot checked)
- **Faster held wind-up** (HeldWindup 1.6): a held swing is Rescaled at its start, undone on release (_fastWindupSeq).
- **Switch sides while holding**: a flick while left click is held sends ManualAttackAim; the held swing is cut and a swing of the new side starts at once (no feint lockout), jumped half way to the ready point. A switched riposte stays a riposte.
- **Walk while holding** (UpperBody.cs): upper-body copies of every whole-body humanoid technique (279 with his mods: animation mods' attacks included, nothing named) are appended to the world's AnimationLibrary list in place from an AnimationSystem constructor prefix (the library is built BEFORE code mods start, so a library-constructor patch is too late). AnimationMask.Find prefix gives "<name> [upper]" the original's mask plus the below-waist bones, no root motion. TechniqueOf postfix shows the copy while ManualCombatSystem.UpperSwing (held swing, NextBlow 1, W A S D pushing); Steer/Velocity patches let the footwork run during it at ReadiedWalkSpeed (0.5). Release = the whole-body swing from the same progress. Game tables sized by the library count read with bounds checks (audio, splashes); per-character Plays is sized on first animation.
- Test channel: `attack hold [dir]`, `reaim <dir>`, `release`, `move <forward|back|left|right|off>`. Agent `camera focus c:0.0` + `camera set --zoom 40 --pitch 12` + `screenshot <path>` shows the pose.

## 0.9.22 (2026-11-22, bug fixes from his play test)
- **Attacking while walking with WASD lost the fight**: Meitou queues the player's attack order (the fight starts a few ticks later), and PZA's steering MoveOrder/StopCommand in that gap replaced it, so the attacked squad (which had already reacted) was left with nobody to fight. FightHold now also holds those single-character commands from the tick an AttackOrder naming the character is taken until its fight shows (at most 2 s). Reproduced and verified on the starving bandit mob near -45570,-2665 (it is in mctest too; teleport ONTO the group, a teleport elsewhere there can leave Su under the ground).
- **Leaving (U) while chased**: the leave window now lasts while W A S D keep pushing (plus 1 s), so PZA's run orders keep going through; an attack or block ends it. Standing still with enemies on you, Kenshi fights back as usual.
- Known, not fixed: wind-ups that drop into a low stance look off over walking legs (the upper body keeps the crouch, the legs stand).

## 0.9.23 (2026-11-22)
- **Pointer hidden in fights** (HidePointerInFights, default on): PZA's views pin Meitou's own pointer at the centre (their crosshair spot), so its action icon sat on the crosshair. A prefix on Meitou.Game.Ui.PointerView.DrawPointer/DrawPointerBeside skips it while in a fight, manual combat active and PZA's mouse look on (PZA.WASDControl.Meitou.Look.On, by reflection). Alt, windows and menus end PZA's mouse look, so the pointer shows then. Agent screenshots never include the pointer: check it by hand. Hardware mouse (Windows cursor) is not covered (he has it off).

## 0.9.24 (2026-11-22, his requests; built, NOT yet tested in game: a test run was stopped)
- **Chamber from a held swing**: letting go of a held attack always resets the chamber timing (_attackPressTick = release), so switching to the blow's side while held and letting go in the window chambers; a swing frozen at the ready point (not let go) never chambers.
- **Readied swing vs enemy AI**: a readied swing counts as a coming blow only for HeldGuardTime (0.4 s, his design: a well-timed side switch catches them guarding the wrong side). After it, enemies guarding against it drop the guard (DefendReadyTick pushed so they do not re-block at once) and HasInitiative lets them swing (HeldReady). Before this a held swing kept every enemy blocking forever.
- **Stagger during a held swing**: already worked (verified in a run: poise 1/2, 0/2, then "staggered"); Su has 2 poise (Toughness 100), refilled after 5 s without a hit.
- **Crossbow walking**: upper-body copies now include the RANGED category records (aim, shot, the crossbows' reload records; 286 copies). In fights: aiming/firing walks at CrossbowWalkSpeed 0.35, reloading at ReloadWalkSpeed 0.1 (his numbers: reload ~10%, aim slower than a held attack). Reload while walking (setting) in and out of fights. Out of fights the reload clip now plays (TechniqueOfPostfix, OutOfFightReload progress; upper-body copy while walking) - check how it looks with the crossbow on the back.

## 0.9.25 (2026-11-22, his design; tested through the test channel)
- **Enemies vs a held attack** (his rules: small window, not abusable, AI still attacks through spam-switching): HeldGuardTime 0.3 s counted from when the hold started (a side switch does not restart it). After it, an enemy guarding against the held attack, or kept waiting by the end of such a guard (each switch makes Kenshi end the guard and start the after-block wait), is let off once per 0.3 s (_guardSince/_guardFreed per slot) to take its turn; HasInitiative allows it (HeldReady); an enemy already swinging keeps its swing (DefendReadyTick pushed: Kenshi would abort it, a held swing waits near its blow). One that cannot attack guards again at once: no free hit. Verified: spam-switching every 6 ticks, the maxed bandit still started 3 swings in 4 s.
- **Switch + release in one moment** still switches (_releasedNow); chamber 2/2 by switching to the blow's side and letting go 6-8 ticks before it lands, 0/2 when kept holding.
- **Crossbow**: walking while aiming 7.1 u/s, while reloading 1.4 u/s with the reload going on (4% -> 17%); out of fights the reload clip plays and the crossbow shows in hand (World.FillIdentity postfix setting the snapshot's WeaponDrawn/RangedDrawn; FillIdentity is a World member, not a WorldPublish class).
- Reload walking speed raised to 0.2 in 0.9.26 (his choice): at 0.1 the leg clips (tied to the real speed) played so slowly it looked like standing.
- Released held swings are left to Kenshi: in tests vs a maxed bandit, blocked when it guarded, hit when it was mid-swing itself (a trade), missed when out of reach.
- **Enemy block rolls vs a held attack** (his rule: blocks follow their stats): an enemy's first defence decision per (swing, held/let go) stands (ChooseDefence postfix, FirstDefence/DefenceKey, per-slot _defenceSeq/_defenceOk); a lost roll is not re-rolled while the same swing is held. A side switch (new swing) or the release gets a fresh roll. Built and determinism-checked, not tested in game (test runs were stopped).

## Open / next
- Tested 2026-11-20/21 through the test channel: blocks, parries, combos, clash and crush-through, chamber, hyper armor, dodge, crossbow (swap, reload, shot, steadying). He liked scenario tests and fuzzing via the test channel.
- After testing: one commit that makes the tested settings the mod's defaults, so it plays well out of the box. His tuned values so far: Chamber window 0.5, Stagger grace 0.5, Hint height 44 (that last one was lost with the stray test settings file).
- Later: crossbow zoom, melee weapon swap, a handoff note for PZA, and a Steam Workshop page. Check whether Meitou code mods can ship as Workshop mods.
