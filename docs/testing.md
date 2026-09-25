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
