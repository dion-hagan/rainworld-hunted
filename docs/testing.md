# Testing the Pursuer without playing whole cycles

Every rule of the mod can be exercised from inside one cycle. Enable **Remix →
Hunted → Testing → Enable testing hotkeys** (on by default) and use these keys in
any story game. Watch the red tracker line at the top of the screen and
`BepInEx/LogOutput.log` (every event is logged with a `[Hunted]` prefix).

| Key (default) | What it does |
|---|---|
| **F5** Summon | Marks the Pursuer as arrived in your region and spawns it two rooms away from you, with no grace period. It starts pathing to your room immediately. |
| **F6** Bring | Makes the Pursuer enter your current room through a pipe (spawning it first if needed). Use this to test combat. |
| **F7** Advance | Applies one cycle of offscreen tracking right now, as if you had just slept in the nearest shelter: it moves the configured number of shelters, rolls offscreen gear, and spawns into the region if it arrived. Press it repeatedly to watch the countdown. |
| **F8** Gear | Cycles its loadout: nothing → rock → spear → explosive spear → spear + scavenger bomb. Re-spawns the creature where it is so the new gear is in its hands. |
| **F9** Kill | Kills it where it stands. It drops its gear; the death/respawn rules apply at the next sleep. |
| **F10** Overlay | Toggles the tracker line. |
| **F11** Reset | Despawns it and places it far away again, exactly like the start of a new campaign. |
| **F12** Forget | Throws away the tactics the slugcat body has learned about you on this save slot and slugcat (the Remix tab has a button that clears every slot). |

The keys can be rebound in the same Remix tab.

## Which body

**Remix → Hunted → Slugcat body** picks Stage 2 (an AI-driven adult slugcat with
Hunter's stats, default) or Stage 1 (the elite scavenger). The switch applies the
next time the Pursuer is spawned, so F11 then F5 is the quickest way to compare
them. Without Downpour the scavenger is always used.

With the slugcat body the overlay shows the AI's mode: *Travel* (heading for
your room), *Search* (going to where it last saw you), *Engage* (lined up to
throw, or closing in), *Scavenge* (going for a better weapon), *Flee* (a
predator has the upper hand) and *EscapeRain*. It holds one item; F8 cycles
rock, spear, explosive spear and bomb.

While it is armed and engaging, the mode also shows the **tactic** it picked
(*Throw*, *Reposition*, *CloseIn* or *Wait*). With **Remix → Hunted → Adaptive
tactics** on, a small network chooses the tactic every half second from the
situation (distance, height, whether you are armed or moving, its own weapon and
threat) and is trained by what happens next: a hit on you is +1 (+2 for heavy
damage), killing you +3, a throw into a wall -0.2 (hits and wall throws are credited only to the decision that threw), getting hurt -1, dying -3. Decisions that nothing followed are trained toward zero when their two-second window closes.

A share of decisions is random so it keeps exploring: 30% on a fresh file, halving
every 300 decisions down to a 5% floor. When outcomes keep surprising its estimates
(for example, you changed how you fight) exploration reopens by itself and settles
again once it has adapted; a single unlucky hit does not. Every reward
is logged as `Tactic reward`. What it learns is saved per save slot and slugcat in
`RainWorld_Data/StreamingAssets/ModConfigs/dion_hunted_tactics_<slot>_<slugcat>.txt`, outside the death-
persistent save, so it survives deaths, quits and new campaigns on that slot: it
describes you, not the run. F12 resets it, and **Remix → Hunted → Forget learned
tactics** deletes every slot. With testing hotkeys on, the overlay also shows the
four estimated rewards behind the choice, so you can watch them move. With the option
off it always throws, which is the old behaviour.

A slot with nothing learned yet starts from `dion_hunted_baseline_tactics.txt` in the
same folder if that file exists: a policy trained headless against the Stage 2 rules by
the [arena](arena.md). F12 and the Remix button go back to it rather than to random
weights; delete the file to start truly fresh. The log says which it loaded.

## Reading the overlay

```
HUNTED  traveling  GW/GW_S04  5 shelters away  gear: Spear
HUNTED  HUNTING  in SU_A22 (offscreen)  3 rooms from you  behavior: Travel  gear: Spear, Rock
HUNTED  dead  respawns at cycle 14 (now 11)
```

- *traveling*: tracked as data only. The shelter shown is where it will start from.
- *HUNTING*: it exists in this region as a creature. *offscreen* means its room is not loaded; *visible* means the room is realized.
- *arrived (not spawned)*: it is due in this region but could not be placed yet (for example you are in a different region than it expected).

## Checking the rules quickly

1. **Countdown**: start a game, note "N shelters away", press **F7** and check it drops by 2 (3 if you were starving).
2. **Arrival**: keep pressing **F7** until it says HUNTING. The creature spawns at least two rooms away from you and waits out the grace period before moving.
3. **Hunting**: with it HUNTING, walk to another room and watch "rooms from you" fall as it migrates toward you. **F6** skips the walk.
4. **Ecosystem**: bring it (F6) into a room with a lizard: the lizard attacks it like any scavenger, and it fights back or flees.
5. **Scavengers**: bring it near a scavenger squad: they treat it as hostile and it does not join them.
6. **Death and respawn**: **F9**, then sleep. The overlay reports the respawn cycle. Sleeping past that cycle (or **F7** enough times) respawns it far away, unarmed unless hard mode is on.
7. **Retreat**: let it kill you (or die to anything). On the next cycle it is at least the configured retreat distance away and still has its gear.
8. **Rain**: survive until the rain warning: it stops hunting and heads for cover (offscreen creatures stop moving; a realized one goes to its den).
9. **Save/load**: quit to the menu and continue: the state is restored from the save file. Killing it and quitting without sleeping reverts, like the rest of the world.

## Where the state lives

The Pursuer's state is one `HUNTED` entry inside the death-persistent part of
the vanilla save string, so it is saved and reverted exactly when the game saves
karma: on a successful sleep (advance), on death (retreat) and on a starved sleep
(advance by one extra shelter). Removing the mod leaves the entry ignored.
