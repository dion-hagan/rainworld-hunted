using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Hunted.Core.Arena
{
    /// <summary>How a training run is set up. Everything the command line accepts lives here.</summary>
    public sealed class ArenaRunOptions
    {
        /// <summary>Independent arenas, each with its own policy, run at the same time.</summary>
        public int Instances = 4;
        /// <summary>Encounters per instance per round.</summary>
        public int Episodes = 300;
        /// <summary>Encounters each policy plays with exploration off to measure it after a round (reward per encounter has a standard error of about 0.06 at 4000).</summary>
        public int EvalEpisodes = 4000;
        public int Seed = 1;
        public int Threads = Environment.ProcessorCount;
        /// <summary>A serialized policy to continue training from (every instance starts from a copy), or null for fresh policies.</summary>
        public string StartFrom;
        /// <summary>The setup every policy is judged on, and trained on unless a round mixes in lessons.</summary>
        public ArenaConfig Config = new ArenaConfig();
    }

    /// <summary>One arena and the policy it trains. <see cref="Episodes"/> holds the current round only.</summary>
    public sealed class ArenaInstance
    {
        public int Index;
        public int Seed;
        public ArenaMatch Match;
        public TacticPolicy Policy;
        /// <summary>The encounters of the round being run or just finished.</summary>
        public readonly List<EpisodeResult> Episodes = new List<EpisodeResult>();
        /// <summary>Encounters played over every round.</summary>
        public long TotalEpisodes;
        public EvalResult Eval;
    }

    /// <summary>A policy's record over evaluation encounters with exploration off.</summary>
    public sealed class EvalResult
    {
        public int Episodes;
        public int Wins;
        public int Deaths;
        /// <summary>Encounters where both died in the same tick.</summary>
        public int Trades;
        /// <summary>Encounters nobody won: time ran out or the opponent lost patience.</summary>
        public int Draws;
        public float MeanReward;
        public float MeanTicks;

        public float WinRate => Episodes > 0 ? (float)Wins / Episodes : 0f;
        public float DeathRate => Episodes > 0 ? (float)Deaths / Episodes : 0f;

        public override string ToString()
        {
            return "wins " + Percent(Wins) + "  deaths " + Percent(Deaths) + "  trades " + Percent(Trades) + "  draws " + Percent(Draws)
                + "  reward/encounter " + MeanReward.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        private string Percent(int n)
        {
            return (Episodes > 0 ? 100f * n / Episodes : 0f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public bool SameOutcomesAs(EvalResult other)
        {
            return other != null && Episodes == other.Episodes && Wins == other.Wins && Deaths == other.Deaths && Trades == other.Trades && Draws == other.Draws
                && Math.Abs(MeanReward - other.MeanReward) < 0.0001f && Math.Abs(MeanTicks - other.MeanTicks) < 0.01f;
        }
    }

    /// <summary>The state of a run after a round: every instance's record, the control, and which policy is best.</summary>
    public sealed class ArenaReport
    {
        public int Round;
        /// <summary>What the round was trained on, for the summary.</summary>
        public string TrainingSetup;
        public IReadOnlyList<ArenaInstance> Instances;
        /// <summary>The Stage 2 rules playing the learner's seat: what "no learning" scores against the same opponent, on the same encounters.</summary>
        public EvalResult Control;
        /// <summary>The Stage 2 rules against the pure Stage 2 rules (no player-like behaviour on either side): the arena's fairness check, which should come out even.</summary>
        public EvalResult PureDuel;
        /// <summary>This round's best instance by reward per encounter.</summary>
        public ArenaInstance Best;
        /// <summary>True when this round's best beat every earlier round's best on the judged encounters, so the baseline should be replaced.</summary>
        public bool Improved;
        /// <summary>The best policy over every round so far, and how it scored.</summary>
        public string BestSoFarPolicy;
        public EvalResult BestSoFar;
        public int BestSoFarRound;
        /// <summary>The four fixed-tactic policies on the judged encounters: what a policy that never looks at the situation scores.</summary>
        public EvalResult[] Yardsticks;
        /// <summary>The best-so-far reward minus the best yardstick's.</summary>
        public float MarginOverYardsticks;
        /// <summary>
        /// Two standard errors of a reward-per-encounter mean at the judged size (the per-encounter
        /// spread is about 4.1): a margin below this is noise.
        /// </summary>
        public float NoiseFloor;
        /// <summary>True when the best-so-far beats every fixed tactic by more than the noise floor: only then is it worth shipping as a prior.</summary>
        public bool ClearsBar => MarginOverYardsticks > NoiseFloor;
        public double Seconds;

        /// <summary>The fixed tactic that scores highest on the judged encounters.</summary>
        public Tactic BestYardstick
        {
            get
            {
                int best = 0;
                for (int t = 1; t < Yardsticks.Length; t++)
                {
                    if (Yardsticks[t].MeanReward > Yardsticks[best].MeanReward)
                    {
                        best = t;
                    }
                }
                return (Tactic)best;
            }
        }

        /// <summary>
        /// The round's encounters aggregated per instance in blocks of <paramref name="blockSize"/>
        /// (1 is one row per encounter), without a header so rounds can be appended to one file.
        /// </summary>
        public string ToCsv(int blockSize)
        {
            var sb = new StringBuilder();
            foreach (ArenaInstance inst in Instances)
            {
                foreach (Block b in Blocks(inst, blockSize))
                {
                    sb.Append(Round).Append(',').Append(inst.Index).Append(',').Append(b.FirstEpisode).Append(',').Append(b.Count).Append(',')
                      .Append(b.Wins).Append(',').Append(b.Deaths).Append(',').Append(b.Trades).Append(',').Append(b.Draws).Append(',')
                      .Append(b.MeanReward.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.MeanTicks.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Throws.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Hits.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Epsilon.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine();
                }
            }
            return sb.ToString();
        }

        public const string CsvHeader = "round,instance,first_episode,encounters,wins,deaths,trades,draws,mean_reward,mean_ticks,throws_per_encounter,hits_per_encounter,epsilon";

        public sealed class Block
        {
            public long FirstEpisode;
            public int Count;
            public int Wins;
            public int Deaths;
            public int Trades;
            public int Draws;
            public float MeanReward;
            public float MeanTicks;
            public float Throws;
            public float Hits;
            public float Epsilon;
        }

        /// <summary>The round's encounters of one instance in blocks, to see whether it is still improving.</summary>
        public static List<Block> Blocks(ArenaInstance inst, int blockSize)
        {
            var blocks = new List<Block>();
            blockSize = Math.Max(1, blockSize);
            long before = inst.TotalEpisodes - inst.Episodes.Count;
            for (int start = 0; start < inst.Episodes.Count; start += blockSize)
            {
                int end = Math.Min(inst.Episodes.Count, start + blockSize);
                var b = new Block { FirstEpisode = before + start, Count = end - start };
                for (int i = start; i < end; i++)
                {
                    EpisodeResult e = inst.Episodes[i];
                    if (e.Won) b.Wins++;
                    else if (e.Outcome == EpisodeOutcome.LearnerDied) b.Deaths++;
                    else if (e.Outcome == EpisodeOutcome.BothDied) b.Trades++;
                    else b.Draws++;
                    b.MeanReward += e.LearnerReward;
                    b.MeanTicks += e.Ticks;
                    b.Throws += e.LearnerThrows;
                    b.Hits += e.LearnerHits;
                }
                b.MeanReward /= b.Count;
                b.MeanTicks /= b.Count;
                b.Throws /= b.Count;
                b.Hits /= b.Count;
                b.Epsilon = inst.Episodes[end - 1].Epsilon;
                blocks.Add(b);
            }
            return blocks;
        }

        /// <summary>The learning curve of an instance over the round as text lines, one per block.</summary>
        public static List<string> Curve(ArenaInstance inst, int blockSize)
        {
            var lines = new List<string>();
            foreach (Block b in Blocks(inst, blockSize))
            {
                lines.Add("encounters " + b.FirstEpisode.ToString(CultureInfo.InvariantCulture).PadLeft(9) + " +" + b.Count.ToString(CultureInfo.InvariantCulture).PadRight(6)
                    + "  wins " + Pct(b.Wins, b.Count) + "  deaths " + Pct(b.Deaths, b.Count) + "  trades " + Pct(b.Trades, b.Count) + "  draws " + Pct(b.Draws, b.Count)
                    + "  reward " + b.MeanReward.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "  exploration " + (100f * b.Epsilon).ToString("0", CultureInfo.InvariantCulture) + "%");
            }
            return lines;
        }

        private static string Pct(int n, int of)
        {
            return (100f * n / Math.Max(1, of)).ToString("0", CultureInfo.InvariantCulture).PadLeft(3) + "%";
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Round " + Round + " (" + Seconds.ToString("0", CultureInfo.InvariantCulture) + " s)" + (TrainingSetup != null ? ", trained on " + TrainingSetup : ""));
            sb.AppendLine("Control (Stage 2 rules in the learner's seat, same encounters): " + Control);
            if (PureDuel != null)
            {
                sb.AppendLine("Fairness check (Stage 2 rules on both sides, no player-like rules): " + PureDuel);
            }
            if (Yardsticks != null)
            {
                for (int t = 0; t < Yardsticks.Length; t++)
                {
                    sb.AppendLine("Always " + ((Tactic)t).ToString().PadRight(10) + ": " + Yardsticks[t]);
                }
            }
            foreach (ArenaInstance inst in Instances)
            {
                sb.AppendLine("Instance " + inst.Index + " (seed " + inst.Seed + ", " + inst.TotalEpisodes.ToString(CultureInfo.InvariantCulture) + " encounters, " + inst.Policy.Decisions.ToString(CultureInfo.InvariantCulture) + " decisions): " + inst.Eval + (ReferenceEquals(inst, Best) ? "   <- best this round" : ""));
            }
            sb.AppendLine("Best so far: round " + BestSoFarRound + ", " + BestSoFar + (Improved ? "   (improved this round)" : "   (kept)"));
            if (Yardsticks != null)
            {
                sb.AppendLine("Margin over the best fixed tactic (always " + BestYardstick + "): " + MarginOverYardsticks.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)
                    + " (noise floor " + NoiseFloor.ToString("0.00", CultureInfo.InvariantCulture) + "): " + (ClearsBar ? "clears the bar, worth shipping" : "does not clear the bar yet, not written as the baseline"));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Trains several Stage 3 policies at once, each in its own arena against the Stage 2
    /// rules, and after every round measures each one with exploration off on the same
    /// judged encounters (fixed seed) and keeps the best over all rounds. Instances are
    /// independent: they never share a policy, so a round is reproducible from the seed no
    /// matter how the threads are scheduled. A round can mix lessons (other gear, a dodging
    /// or unarmed opponent) into what it trains on, encounter by encounter, while the judge
    /// stays fixed.
    /// </summary>
    public sealed class ArenaTrainer
    {
        public readonly ArenaRunOptions Options;
        private readonly List<ArenaInstance> instances = new List<ArenaInstance>();
        private readonly int judgeSeed;
        private int round;
        private string bestSoFarPolicy;
        private EvalResult bestSoFar;
        private int bestSoFarRound;
        private EvalResult[] yardsticks;

        public IReadOnlyList<ArenaInstance> Instances => instances;

        public ArenaTrainer(ArenaRunOptions options)
        {
            Options = options ?? new ArenaRunOptions();
            judgeSeed = unchecked(Options.Seed * 7919 + 1);
            for (int i = 0; i < Options.Instances; i++)
            {
                int seed = Options.Seed * 1000 + i;
                TacticPolicy policy;
                if (Options.StartFrom != null)
                {
                    if (!TacticPolicy.TryParse(Options.StartFrom, TacticFeatures.Count, seed, out policy))
                    {
                        throw new ArgumentException("The policy to start from could not be parsed");
                    }
                }
                else
                {
                    policy = new TacticPolicy(TacticFeatures.Count, seed);
                }
                instances.Add(new ArenaInstance { Index = i, Seed = seed, Policy = policy, Match = new ArenaMatch(Options.Config, seed, policy) });
            }
        }

        /// <summary>
        /// Plays <paramref name="episodes"/> encounters in every instance, cycling through
        /// <paramref name="lessons"/> encounter by encounter (the options' config when null),
        /// then judges every policy on the options' config over the same encounters.
        /// </summary>
        public ArenaReport RunRound(int episodes, IList<ArenaConfig> lessons = null, Action<ArenaInstance, EpisodeResult> progress = null)
        {
            DateTime started = DateTime.UtcNow;
            round++;
            if (lessons == null || lessons.Count == 0)
            {
                lessons = new[] { Options.Config };
            }
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.Threads) };
            Parallel.For(0, instances.Count, parallel, i =>
            {
                ArenaInstance inst = instances[i];
                inst.Episodes.Clear();
                for (int e = 0; e < episodes; e++)
                {
                    inst.Match.Config = lessons[e % lessons.Count];
                    EpisodeResult result = inst.Match.RunEpisode();
                    result.Instance = inst.Index;
                    inst.Episodes.Add(result);
                    inst.TotalEpisodes++;
                    progress?.Invoke(inst, result);
                }
                inst.Match.Config = Options.Config;
            });
            EvalResult control = null;
            EvalResult pureDuel = null;
            bool needYardsticks = yardsticks == null;
            if (needYardsticks)
            {
                yardsticks = new EvalResult[TacticPolicy.TacticCount];
            }
            // The judged encounters never change, so the yardsticks are computed once per run.
            int extra = 2 + (needYardsticks ? TacticPolicy.TacticCount : 0);
            Parallel.For(-extra, instances.Count, parallel, i =>
            {
                if (i == -1)
                {
                    control = Evaluate(Options.Config, null, Options.EvalEpisodes, judgeSeed);
                    return;
                }
                if (i == -2)
                {
                    ArenaConfig pure = Options.Config.Clone();
                    pure.OpponentAvoidsExposure = false;
                    pure.OpponentPatienceTicks = 0;
                    pureDuel = Evaluate(pure, null, Options.EvalEpisodes, judgeSeed);
                    return;
                }
                if (i < -2)
                {
                    int t = -i - 3;
                    yardsticks[t] = Evaluate(Options.Config, ConstantPolicy((Tactic)t), Options.EvalEpisodes, judgeSeed);
                    return;
                }
                ArenaInstance inst = instances[i];
                inst.Eval = Evaluate(Options.Config, inst.Policy.Serialize(), Options.EvalEpisodes, judgeSeed);
            });
            // Best by reward per encounter, which is what the policy is trained on and which
            // counts kills, deaths, hits and misses together; win rate breaks ties.
            ArenaInstance best = null;
            foreach (ArenaInstance inst in instances)
            {
                if (best == null || inst.Eval.MeanReward > best.Eval.MeanReward || (inst.Eval.MeanReward == best.Eval.MeanReward && inst.Eval.WinRate > best.Eval.WinRate))
                {
                    best = inst;
                }
            }
            bool improved = bestSoFar == null || best.Eval.MeanReward > bestSoFar.MeanReward;
            if (improved)
            {
                bestSoFarPolicy = best.Policy.Serialize();
                bestSoFar = best.Eval;
                bestSoFarRound = round;
            }
            var setup = new StringBuilder();
            for (int i = 0; i < lessons.Count; i++)
            {
                setup.Append(i > 0 ? " / " : "").Append(Describe(lessons[i]));
            }
            float bestYardstick = float.MinValue;
            foreach (EvalResult y in yardsticks)
            {
                bestYardstick = Math.Max(bestYardstick, y.MeanReward);
            }
            return new ArenaReport
            {
                Round = round,
                TrainingSetup = (lessons.Count > 1 ? "a mix of " : "") + setup,
                Instances = instances,
                Control = control,
                PureDuel = pureDuel,
                Yardsticks = yardsticks,
                Best = best,
                Improved = improved,
                BestSoFarPolicy = bestSoFarPolicy,
                BestSoFar = bestSoFar,
                BestSoFarRound = bestSoFarRound,
                MarginOverYardsticks = bestSoFar.MeanReward - bestYardstick,
                NoiseFloor = 2f * RewardSpread / (float)Math.Sqrt(Math.Max(1, Options.EvalEpisodes)),
                Seconds = (DateTime.UtcNow - started).TotalSeconds,
            };
        }

        /// <summary>The standard deviation of one encounter's reward, measured at about 4.1 on the judged setup.</summary>
        public const float RewardSpread = 4.1f;

        public static string Describe(ArenaConfig c)
        {
            return c.LearnerGear.ToString().ToLowerInvariant() + " vs " + c.OpponentGear.ToString().ToLowerInvariant()
                + (c.OpponentDodgeChance > 0f ? " (dodge " + c.OpponentDodgeChance.ToString("0.###", CultureInfo.InvariantCulture) + ")" : "")
                + ", " + c.SpareSpears + " spare spears, " + (c.RandomRooms ? "random rooms" : "fixed room");
        }

        /// <summary>
        /// Plays <paramref name="episodes"/> encounters with a copy of the policy that neither
        /// explores nor learns (or with the Stage 2 rules when <paramref name="policyText"/> is
        /// null) and reports how it did. The rooms, start positions and spare spears of
        /// encounter N depend only on the seed and N, so two policies judged on the same seed
        /// meet the same encounters; what they do in them, and the damage rolls, still differ.
        /// </summary>
        public static EvalResult Evaluate(ArenaConfig config, string policyText, int episodes, int seed)
        {
            TacticPolicy policy = null;
            if (policyText != null)
            {
                if (!TacticPolicy.TryParse(policyText, TacticFeatures.Count, seed, out policy))
                {
                    throw new ArgumentException("The policy to evaluate could not be parsed");
                }
                policy.FixedEpsilon = 0f;
                policy.LearningRate = 0f;
            }
            var match = new ArenaMatch(config, seed, policy);
            var result = new EvalResult { Episodes = episodes };
            for (int e = 0; e < episodes; e++)
            {
                EpisodeResult r = match.RunEpisode();
                if (r.Won) result.Wins++;
                else if (r.Outcome == EpisodeOutcome.LearnerDied) result.Deaths++;
                else if (r.Outcome == EpisodeOutcome.BothDied) result.Trades++;
                else result.Draws++;
                result.MeanReward += r.LearnerReward;
                result.MeanTicks += r.Ticks;
            }
            if (episodes > 0)
            {
                result.MeanReward /= episodes;
                result.MeanTicks /= episodes;
            }
            return result;
        }

        /// <summary>The text of a policy whose network always scores <paramref name="tactic"/> highest: a fixed-tactic yardstick.</summary>
        public static string ConstantPolicy(Tactic tactic)
        {
            var net = new TinyNet(TacticFeatures.Count, 1, TacticPolicy.TacticCount, 0);
            string[] parts = net.Serialize().Split(',');
            // Layer sizes and version first, then every weight: zero them all except the chosen output's bias.
            for (int i = 4; i < parts.Length; i++)
            {
                parts[i] = "0";
            }
            parts[parts.Length - TacticPolicy.TacticCount + (int)tactic] = "1";
            return "v=" + TacticPolicy.Version + "|net=" + string.Join(",", parts);
        }

        /// <summary>One round from scratch: the simplest way to use the trainer.</summary>
        public static ArenaReport Run(ArenaRunOptions options, Action<ArenaInstance, EpisodeResult> progress = null)
        {
            return new ArenaTrainer(options).RunRound(options.Episodes, null, progress);
        }
    }
}
