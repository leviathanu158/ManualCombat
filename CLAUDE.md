# Manual Combat (Meitou code mod)

Full background is in `ManualCombat-handover.txt` (the original claude.ai handover). This file has the parts every session needs.

## Project
- Mount & Blade style melee for **MeitouClient**, the open-source C#/.NET reimplementation of Kenshi (github.com/VlcekM/MeitouClient). It adds directional attacks aimed by mouse flick, hold-to-block, parries, feints, combos, clashing, chambering, hyper armor and manual crossbows.
- Works together with PZA's "WASD & Controller Support" mod (github.com/PZA-Mods/PZAMeitouWASDController). Manual combat is active only while PZA's WASD is on.
- License: GPLv3. Current version: **0.9.0**.
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
  - don't use `is var (a,b)` on nullable tuples.
- All world changes go through SimCommands, and ALL mod state is hashed (IStateHashed), for determinism and future multiplayer.

## His PC
- Meitou install: `C:\Kenshi_Meitou`. Current version: `versions\0.22.2-nightly.20261121\` (commit 802c1ca3fcec7250d0a22c7be3dd647013cdab2a, since 2026-11-21; before: 0.22.2-nightly.20261119.2, 69d2688). He runs the Vulkan renderer.
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

## Open / next
- Tested 2026-11-20/21 through the test channel: blocks, parries, combos, clash and crush-through, chamber, hyper armor, dodge, crossbow (swap, reload, shot, steadying). He liked scenario tests and fuzzing via the test channel.
- After testing: one commit that makes the tested settings the mod's defaults, so it plays well out of the box. His tuned values so far: Chamber window 0.5, Stagger grace 0.5, Hint height 44 (that last one was lost with the stray test settings file).
- Later: crossbow zoom, melee weapon swap, a handoff note for PZA, and a Steam Workshop page. Check whether Meitou code mods can ship as Workshop mods.
