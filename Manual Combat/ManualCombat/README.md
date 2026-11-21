# Manual Combat for Meitou (v0.9.12)

Fight with your own hands, Mount & Blade style. Built for Meitou **0.22.2-nightly.20261121**. **Needs PZA's WASD & Controller Support**: manual combat is only on while its WASD movement is on (with WASD off, or without PZA's mod, your characters fight the normal Kenshi way).

## Install / update

1. One time: install the **.NET 11 SDK (x64)** from https://dotnet.microsoft.com/download/dotnet/11.0 .
2. Unzip this folder (over the old one when updating) and double-click **build.bat**. It builds the mod and copies it into `Kenshi\mods\ManualCombat`.
   If it says **BUILD FAILED**, send the text in the chat.
3. In the Meitou launcher, tick **ManualCombat** and press Play.

## Controls

Manual combat follows your **first selected character** (the one PZA's mod steers). The controls below only apply **while that character is in a fight**. Outside fights your mouse and keys work as usual.

| Input | What it does in a fight |
|---|---|
| **Flick the mouse** | Picks the side of your swing or guard: **left / right**, **up = overhead**, **down = thrust**. The last flick sticks until the next. |
| **Left click** | **Attack** toward the aimed side. |
| **Right click** (hold) | **Block.** The guard goes up at once and stays up as long as you hold it, swing or no swing. Pressing it just before a hit lands is a **parry**, which staggers the attacker. |
| **Mouse button 4** | **Dodge**, any time in a fight, swing coming or not. |
| **Mouse button 5** | **Switch target** to the enemy nearest where the camera points. |
| **W A S D** | **Step** around your enemy while facing it. |
| **X** | **Swap** between your crossbow and your melee weapon. |
| *(unbound)* | **Reload crossbow** (left click on an empty crossbow reloads too). |
| **Alt + right click** | **Use things in a fight**: does what a plain right click does out of a fight (open, loot, talk, trade, give orders at the crosshair). Your character still gets hit while it does them. Alt + left click is the game's own too. |
| **U** | **Leave the fight**: your character drops the fight and you get normal clicks back, so you can run. |
| **O** | Manual combat off/on. |

Left and right click are the game's own *Select* and *Command* buttons, so they follow whatever those are bound to. Everything else is under **Settings > Controls**, where you can also bind attack and block to extra keys.

Start fights as usual: right-click an enemy (or use PZA's attack button), or get attacked.

## How swings and blocks behave

- **A click always swings, the way you aimed.** Out of reach, you whiff, and Kenshi's longer pause after a miss leaves you open, so judge your distance. Kenshi's limit on how many fighters may attack one target at once doesn't hold you back. (Setting *Swing even out of reach*; off, the swing waits while you step in.) Only if your weapon has no swing for the aimed side at all does the game pick one of its others.
- **Aiming right swings from your right**, as in Mount & Blade. Kenshi names swings by the side they land on, so the mod mirrors them (setting *Mirror attack sides*).
- **You attack what you look at.** An attack turns on the enemy within reach nearest your crosshair.
- **A click swings now or not at all.** A press waits at most 0.4 s for a chance to swing (the *Attack buffer* setting), so your character doesn't swing later on its own.
- **Feints.** Pressing block or dodge early in your own swing (the first half of the wind-up) cancels it, and your next attack waits a moment (0.3 s). Later in the swing you're committed, and the guard comes when the swing ends. Right after your blow lands, you can't block or dodge for 0.2 s. All three are settings.
- **Blocking is directional.** Your guard faces the side you aim, and only a guard on the swing's side stops it.
- **A good block can still be beaten**, as in Kenshi: when the attacker's skill is well above your defence, a blow may get past a guard on the right side. It happens half as often as in Kenshi (setting *Block beaten chance*). A **parry always blocks**.
- **Group fights:** with 3 or more enemies on you, your held guard turns to each swing's side by itself (setting *Auto-block from this many attackers*). You still hold right click to block.
- **Your target** gets Kenshi's hover outline, in red.
- A guard raised late still counts if it's up when the blow lands.

## Combos and counters

- **Riposte:** your first swing within 1.5 s of a parry plays faster.
- **Combos:** when your swing hits, attack from a *different side* in the short timing just after the hit (0.05 s to 0.25 s after it, by default). Your swing is cut right there, Kenshi's pause after a hit is skipped, and the next swing comes out at once, a little faster. A combo is two swings at most; after it your next ordinary swing waits a short rest (0.8 s by default) once the combo ends, while blocking and dodging stay free. Too early or too late, or from the same side, and it's a normal swing after the pause.
- **Clashing:** if your swing and an enemy's swing at you come from the **same side** (both from their right, both overhead...) and land at almost the same moment, they meet in the air. Your swing and that one enemy's stop, sparks fly and the parry sound plays; in a group fight nobody else's swing is touched. The stronger fighter (Strength, weapon skill and weapon weight) may **crush through** and land the blow anyway: "Crushed through!" when it's you, "Overpowered!" when it's them.
- **Chambering:** attack from the **same direction** as an enemy's blow just before it lands, aimed as you would block it (a thrust against a thrust, a swing from your left against a blow at your left), and your swing turns it aside while it carries on. Your weapon has to really swing that way. It works against enemies in front of you or at your side, not from well behind. A chamber that lands keeps your swing safe: nothing staggers you until it ends, and the rest of that enemy's swing misses too. Their blow misses (and they get Kenshi's pause after a miss), with the parry sound.

## Fair enemy pacing (manual combat only)

Kenshi's fighters take turns: one that loses the initiative roll waits half a second for the winner to swing. Kenshi starts that wait when the winner's block ends, and your held block never ends, so enemies used to stand and stare while you blocked. Now their wait is always Kenshi's half second. And since your character never takes its turn by itself, a much weaker enemy could lose the roll again and again and just stand there: one that has kept losing for 2.5 s swings anyway. How many enemies may swing at you at once is Meitou's own **attack slots** setting. Settings: **Fair enemy pacing**, **Longest enemy wait**.

## Pausing

A block or attack pressed while the game is paused still happens when it resumes, but it can't be a parry, a chamber or a combo: those need the timing done live.

## Hyper armor

While you swing, your character has a little **poise**: 1 per 40 Toughness (none below 40, 2 at 80). A hit that would stagger you mid-swing is shrugged off instead ("Held firm") for 1 poise if light, 2 if heavy; poise refills after 5 s without paying for a hit. When a stagger does go through, nothing staggers you again for 0.5 s after it ends, so no stun-locks. All of it is in the **Staggering** settings.

## Weapons missing a side

When your weapon has no swing at all from the side you aim, it swings from another side and *No Attack For That Direction* shows under the crosshair. Animation mods that give every weapon all four directions make this go away.

## Crossbows

With your crossbow out in a fight a reticle shows: a dot at the crosshair and a ring for how far a bolt may stray, from your character's Crossbows and Perception skills (the **Spread** setting scales it). **Left click fires** along the crosshair; on an empty crossbow left click **reloads** (a "Reload crossbow" key can be bound too). **Hold right click to steady** the aim: the ring draws in. **X** swaps to your melee weapon and back. The enemy nearest the crosshair counts as your target: when they are beyond your crossbow's range you get "Out of range". While you hold the crossbow out, an enemy closing in no longer makes your character put it away.

Under the dot, **1:1** means loaded and **0:1** (red) empty (bolts loaded : bolts a reload holds).

Aim in PZA's over-the-shoulder view (**Z**): in the normal view the crosshair sits on your own character. Holding right click **zooms in** as the aim steadies, in PZA's over-the-shoulder and first-person views (setting *Zoom when steadied*; 1 = no zoom). **W A S D step** you with the crossbow out too, while you aim and reload, with the fight's walking animation (the aim pose comes back when you stop); with no key held you stand. **Out of a fight an empty crossbow reloads by itself** (bolts carried, both arms working), taking as long as a reload in a fight; a fight that starts meanwhile carries the reload on. No reload animation plays out of a fight.

## On-screen arrows

While fighting, faint arrows show at the centre of the screen (PZA's crosshair in its views), under the game's windows:

- **gold**: the side your next swing goes
- **blue**: your guard, while blocking
- **red, pointing in**: the side the enemy's swing is coming from

## Settings (Settings > Mods > Manual Combat)

- **Aim with**: mouse flick (default) or movement keys, and **mouse flick sensitivity**.
- **Left/right click fight in a fight**, **Direction arrows**, **Show the incoming swing's side**.
- **Attack what you look at**, **Swing even out of reach**, **Mirror attack sides**, **No falling dodges** (leaves out Kenshi's dodges that throw you to the ground).
- **W A S D footwork in fights**, **Fight-mode hint** (a small line at the top right while you fight), **Footwork speed**, **Close in by itself**.
- **Show the guard at once**, **Mirror left and right guards**.
- **Block and dodge cancel your swing (feint)**, with **Feint window**, **Locked after your blow** and **Attack lockout after a feint**.
- **Auto-block from this many attackers** (0 = never), **Outline your target**, **Arrow opacity**, **Hint height**.
- **Combos and counters**: **Combos**, **Riposte speed**, **Combo speed per step**, **Combo timing starts**, **Combo timing length**, **Rest after a combo**, **Clash and chamber sounds**, **Clashing** and **Clash window**, **Chambering** and **Chamber window**.
- **Crossbows**: **Manual crossbow**, **Spread** (1.25 by default: a little wider than Kenshi's), **Steadied spread**, **Time to steady**, **Zoom when steadied**.
- **Parry** and **Parry window**, **Block beaten chance**, **Attack buffer**, **Skip the initiative roll**.
- **Debug**: **Log details** writes every attack, block, parry, block roll and hit taken to `log.txt`, for testing and bug reports. Leave it off to play: off, the mod doesn't even put those lines together.
- **On-screen messages**: short lines like *Parry!* show under the crosshair for a moment in a fight, the newest replacing the last.

## Animation mods

The mod never looks moves up by name. It picks attacks, blocks and dodges by their tags in the game data (direction, block, dodge), so moves added by animation mods are used like Kenshi's own. A weapon with no dodge of its own dodges with the unarmed (Martial Arts) dodges.

## Known limits

- A swing roots your character in place while it plays (that's Kenshi: a swing moves the body by its animation only).
- The arrows are drawn with the Vulkan renderer (Windows and Linux), not with the Metal renderer on macOS.
- The mod reaches into Meitou's internal code. A Meitou update can break it. When that happens it turns itself **off** with a line in `log.txt`, and the game plays normally.

Licence: GPLv3 or later (it builds on Meitou, which is GPLv3).


## For testing (developers)

The mod has a test channel for scripted tests through Meitou's agent channel. It is off unless the environment variable `MANUALCOMBAT_TEST_DIR` names a folder; then the mod reads command lines from `<folder>\mc.cmd` each control tick (`aim <dir>`, `attack [dir]`, `block on [dir]` / `block off`, `dodge`, `target`, `swap`, `reload`, `leave`, `report`) and appends results to `<folder>\mc.out`. See `src/TestChannel.cs`.
