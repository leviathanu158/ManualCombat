**Mod API: `SpawnPlatoon` member cap (`maxMembers`)**

Adds an optional `maxMembers` argument to `PopulationSystem.SpawnPlatoon` (default 0 = no cap, so existing callers are unaffected). A capped spawn has at most that many members, and keeps that size whenever its squad is made again.

**Why:** a quest mod (Radiant Quests) spawns targets for "Hunt 2 Antelope"-style quests. With content mods, the only local template for a creature may plan 6 to 8, so a quest for 2 gets a whole herd. Trimming the group afterwards isn't safe for a mod:
- An unloaded platoon has no characters yet.
- `CharacterTable.Remove` bypasses the squad, `Platoon.Size` and the saved members.
- A stand-in that unloads may be planned again from its key, which would bring the extras back.

The cap has to live in the plan.

**What changes:**
- `SquadPlan.Capped(int max)`: keeps the first `max` members in creation order (the leader first), then whole slave squads while they fit. With 0, or a plan already within the cap, it returns the plan unchanged.
- `Platoon.MemberCap` is set by `SpawnPlatoon`. The plan is cut by it in `SpawnPlatoon` and wherever the platoon's squad is built (`BuildRoamer` → `BuildOne`), and in `SaveLoader.SquadOf` for an older stand-in with no count.
- **Saved:** as the PLATOON key `meitou mod cap` (int), only for a mod spawn with a cap.
- **Hashed:** only when set, so worlds without a capped spawn hash as before.
- **Docs:** `docs/modding.md` (SpawnPlatoon paragraph) and `docs/formats/save.md` (the code-mod PLATOON keys).

**Unmodded play and determinism:**
- **Unmodded play:** nothing changes without a mod calling `SpawnPlatoon` with a cap.
- **Determinism:** the cap is part of the hashed platoon state, and capping is a pure function of the plan.

**Test** (`ModHookTests.A_capped_spawn_has_at_most_its_cap_and_keeps_it_through_a_save`):
- A cap of 2 on the 3-member test template gives `Size` 2.
- `Capped(0)` and `Capped(5)` return the plan unchanged, and `Capped(2)` keeps the first two members.
- The cap is written to the save and read back, with the size kept.

**Test run:**
- Build: no warnings.
- All `ModHookTests` pass.
- On the full run (Windows, with a heavily modded Kenshi install), 6 tests fail. All 6 fail the same way on current master without this change, so this PR doesn't cause them:
  - `GoldenHashTests.Scripted_scenarios_hash_as_pinned` (crime, arrest, production, wild hashes)
  - `MyGuiTests.AllInstalledLayoutsAndHudResourcesCanBeRead`
  - `FaithfulInteractionTests.EveryWidgetTheFaithfulModeFillsExistsInTheOriginalLayout`
  - `HubNavmeshTests.The_hub_paths_keep_their_exact_points`
  - two `KidnapInstallTests`

Written by Claude (Anthropic's AI) for the Radiant Quests mod. Happy to adjust naming or placement to taste.
