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
- Meitou install: `C:\Kenshi_Meitou`. Current version: `versions\0.22.2-nightly.20261119.2\` (commit 69d2688bc69d47b8374f8c7e203d50d66f092710). He runs the Vulkan renderer.
- .NET SDK 11.0.100 (10.0.100 also installed).
- Game log: `C:\Kenshi_Meitou\versions\0.22.2-nightly.20261119.2\log.txt`. Mod lines start with `mod       manualcombat:`, and a session starts at the last `manualcombat: ready`.
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
- **Chamber**: attack from the same side within 0.2 s before an incoming blow lands.
- **Hyper armor**:
  - Only while swinging. Poise = floor(Toughness/40 × scale).
  - A light hit costs 1 poise and a heavy hit 2. Poise refills after 5 s without a hit.
  - 0.5 s grace after any stagger.
- **Enemy pacing**: a lost roll's yield is capped at 0.5 s, and at most 2 enemies swing at you at once (setting 1–6).
- **Pause**: presses made while paused can't parry, chamber or combo (the test channel is exempt).
- **Crossbow**:
  - Reticle with a spread ring; LMB fires along the crosshair ray, with a deterministic roll.
  - LMB on empty reloads. Holding RMB steadies to 35% over 1 s.
  - No zoom yet.
- If the weapon lacks the aimed direction, it swings another side and shows "No Attack For That Direction".
- Log details setting: default OFF (it caused stutters).

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
- The mctest save (safe spot, WASD ON, weapon) is being made by him. The old "quicksave" had WASD OFF.

## PZA collaboration
PZA plans an API in his WASD mod and will patch Manual Combat to use it. It will replace the Driver.On reflection, FightHold, Resolve priority, Alt handling and the camera/crosshair reads. **When his patched version comes back, it becomes the base: read his API and build on it.** Controller support is his.

## Open / next
- First automated test once mctest exists, e.g. 3 bandits. It should cover the max-2-swinging rule, parry timing, clash, chamber, hyper armor and crossbow. He liked scenario tests and fuzzing via the test channel.
- Later: crossbow zoom, melee weapon swap, a handoff note for PZA, and a Steam Workshop page. Check whether Meitou code mods can ship as Workshop mods.
