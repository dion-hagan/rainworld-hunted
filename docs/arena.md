# The arena: training the Stage 3 Pursuer without a player

The slugcat Pursuer's adaptive tactics (the "Stage 3" layer, see [testing.md](testing.md))
learn from fights against you, at maybe twenty encounters an hour. The arena is a
headless copy of that fight: the Stage 3 learner against the Stage 2 rules, in a
simplified room, thousands of encounters a second, several arenas at once. Its
output is a **baseline tactics file** that the mod loads for any save slot that has
nothing learned yet, so a fresh Pursuer starts from something better than random
weights and keeps adapting to you from there.

Nothing in the arena touches the game. It is built from the mod's game-independent
core (`src/Core`), like the tests are, so it runs anywhere .NET runs.

## Running it

```powershell
dotnet run --project tools/Hunted.Arena -c Release -- --instances 6 --episodes 2000 --rounds 0 --hours 8
```

| Option | Meaning | Default |
|---|---|---|
| `--instances N` | Arenas (each with its own policy) trained at the same time, one thread each. | 4 |
| `--episodes N` | Encounters per instance per round. | 300 |
| `--eval N` | Encounters each policy plays with exploration off after a round, to measure it. | 100 |
| `--rounds N` | Rounds to run; `0` means until `--hours` passes or a `STOP` file appears in the output folder. | 1 |
| `--hours H` | Stop after the round that passes this many hours. | no limit |
| `--seed N` | Base seed; instance *i* uses `seed*1000+i`. Same seed, same run. | 1 |
| `--out DIR` | Output folder. | `arena-out` |
| `--from FILE` / `--resume` | Continue training from a tactics file (every instance starts from a copy) / from the baseline already in the output folder. | fresh |
| `--gear`, `--opponent-gear` | `none`, `rock`, `spear` or `explosive` for each side. | spear |
| `--spares N` | Spare spears lying in the room. | 3 |
| `--dodge P` | Chance per tick that the opponent jumps a thrown weapon. `0` is the Stage 2 AI as it is. | 0 |
| `--max-ticks N` | Ticks per encounter before it is a draw (40 per second). | 2400 |
| `--fixed-room` | The fixed test room instead of a random layout per encounter. | random |
| `--curriculum` | Rotate what a round trains on: the judged setup, then a dodging opponent, rocks on either side, an explosive spear, an unarmed opponent. Evaluation always uses the judged setup. | off |
| `--block N` | Encounters per row of `report.csv`. | 500 |
| `--log-decisions` | Also write every trained decision (features, tactic, credit) to `decisions.csv` (short runs only: it is one row per decision). | off |
| `--install [DIR]` | Copy the baseline into the game's `ModConfigs` folder after each round. | off |

A thread plays a few thousand encounters a second, so a round of 100 000 encounters per
instance takes under a minute. Each round ends with an evaluation
of every instance's policy over `--eval` encounters with exploration and learning off,
plus a **control**: the Stage 2 rules in the learner's seat, which is what "no learning"
scores against the same opponent. The instance with the best reward per encounter is
written as the baseline.

## What it writes

In the output folder:

- `dion_hunted_baseline_tactics.txt`: the best policy of the last round, in the same
  format as the per-slot files the game writes. Copy it to
  `Rain World\RainWorld_Data\StreamingAssets\ModConfigs\` (or pass `--install`) and
  every slot without a `dion_hunted_tactics_<slot>_<slugcat>.txt` starts from it.
  `scripts/inspect_tactics.py <file>` prints what it would do in typical situations.
- `instances/instance_N.txt`: every instance's policy, to continue from or compare.
- `summary.txt`: appended each round: what the round trained on, the control, every
  instance's evaluation, and each instance's curve over the round in ten blocks.
- `report.csv`: appended each round, one row per instance per block of `--block`
  encounters (round, instance, first episode, encounters, wins, deaths, trades, draws,
  mean reward, mean ticks, throws and hits per encounter, exploration), for plotting.
- `decisions.csv` with `--log-decisions`: the dataset the bandit trained on, one row per
  decision, for offline experiments with other models (guide §8.7).

Create a file named `STOP` in the output folder to end a long run after the current round.

## How faithful it is

The learner in the arena is the real thing: `TacticPolicy` and `TinyNet` from the mod,
the same twelve features in the same order and on the same scales as
`PursuerAI.ChooseTactic`, a decision every twenty ticks while armed and engaging, and the
same rewards as the game hooks (hit +2/+1/+0.5 to the decision that threw, wall hit
-0.2, hurt -0.25..-1, kill +3, death -3, with the two-second window). The opponent and the
learner's own body use a port of the Stage 2 decision rules: a throwing position level with
the target at throwing range with line of sight, a throw the moment it is lined up, picking
up the nearest better weapon when unarmed, remembering a lost target for thirty seconds and
getting a fix on it every eight.

What is simplified: the room (a floor, one-way platforms reached by poles or jumps, crates
for cover, no water, pipes or beams), the body (a point that walks, jumps and climbs; no
slides, pounces, wall jumps or crawling), throws (horizontal, straight for 560 px then
dropping; a miss loses the spear as it does for the AI in the game), damage (a spear does
0.6 to 1.4 of a slugcat's one point of health, a rock 0.2 and a long stun), and the
opponent (the Stage 2 rules never dodge unless `--dodge` says so). There are no predators
and no bombs, so the threat and bomb features are always zero here and the policy learns
nothing about them until it meets them in the game. That is fine: the baseline is a prior,
and the in-game learner's surprise-driven re-exploration reopens exploration when the game
keeps contradicting it.

The duel is symmetric, and the Stage 2 rules throw the tick they are lined up, so the
learner's edge is mostly fewer deaths and fewer wasted throws rather than a big win rate:
expect a reward per encounter around +1 against the control's 0, with a win rate a few
points above it and a death rate a few points below.

## Reading the numbers

```
Round 3 (12 s)
Control (Stage 2 rules in the learner's seat): wins 37%  deaths 37%  trades 6%  draws 21%  reward/encounter +0.00
Instance 0 (seed 1000, 6000 encounters, 148230 decisions): wins 44%  deaths 30%  trades 6%  draws 21%  reward/encounter +1.01   <- best
```

- *trades*: both died in the same tick (they lined up on each other and threw together).
- *draws*: nobody died within `--max-ticks`, usually because every spear was thrown and lost.
- The control's numbers are the arena's fairness check: wins and deaths should be close.
- A learning curve that climbs and then flattens has converged for that seed; one that
  swings after flattening is the exploration reopening on surprise (see testing.md).

## Continuing overnight

```powershell
./scripts/arena_overnight.ps1 -Instances 6 -Hours 8
```

builds the tool and runs it with `--rounds 0 --curriculum`, 400 000 encounters per instance
per round (under a minute each), for eight hours, rewriting the baseline and the summary after every round and
logging to `arena-outrena.log`. Add `-Resume` to pick up from the baseline already there,
`-Install` to copy each round's baseline into the game. To watch it:

```powershell
Get-Content arena-out\summary.txt -Tail 30
```

The policy's `d=` counter (decisions) in the baseline will be in the hundreds of millions
after a night. In the game that puts the exploration schedule at its 5% floor from the
start; the surprise-driven reopening (testing.md) takes over when the real game keeps
contradicting what the arena taught.
