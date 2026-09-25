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
        /// <summary>Encounters each policy plays with exploration off to measure it after a round.</summary>
        public int EvalEpisodes = 100;
        public int Seed = 1;
        public int Threads = Environment.ProcessorCount;
        /// <summary>A serialized policy to continue training from (every instance starts from a copy), or null for fresh policies.</summary>
        public string StartFrom;
        /// <summary>The setup every policy is evaluated on, and trained on unless a round says otherwise.</summary>
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
        public int Timeouts;
        public float MeanReward;
        public float MeanTicks;

        public float WinRate => Episodes > 0 ? (float)Wins / Episodes : 0f;
        public float DeathRate => Episodes > 0 ? (float)Deaths / Episodes : 0f;

        public override string ToString()
        {
            return "wins " + Percent(Wins) + "  deaths " + Percent(Deaths) + "  trades " + Percent(Trades) + "  draws " + Percent(Timeouts)
                + "  reward/encounter " + MeanReward.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        private string Percent(int n)
        {
            return (Episodes > 0 ? 100f * n / Episodes : 0f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    /// <summary>The state of a run after a round: every instance's record, the control, and which policy is best.</summary>
    public sealed class ArenaReport
    {
        public int Round;
        /// <summary>What the round was trained on, for the summary.</summary>
        public string TrainingSetup;
        public IReadOnlyList<ArenaInstance> Instances;
        /// <summary>The Stage 2 rules playing the learner's seat: what "no learning" scores against the same opponent.</summary>
        public EvalResult Control;
        public ArenaInstance Best;
        public double Seconds;

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
            sb.AppendLine("Control (Stage 2 rules in the learner's seat): " + Control);
            foreach (ArenaInstance inst in Instances)
            {
                sb.AppendLine("Instance " + inst.Index + " (seed " + inst.Seed + ", " + inst.TotalEpisodes.ToString(CultureInfo.InvariantCulture) + " encounters, " + inst.Policy.Decisions.ToString(CultureInfo.InvariantCulture) + " decisions): " + inst.Eval + (ReferenceEquals(inst, Best) ? "   <- best" : ""));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Trains several Stage 3 policies at once, each in its own arena against the Stage 2
    /// rules, and after every round measures each one with exploration off and keeps the
    /// best. Instances are independent: they never share a policy, so a round is
    /// reproducible from the seed no matter how the threads are scheduled. A round can be
    /// trained on a different setup than the one policies are judged on, so a long run can
    /// rotate through gear and opponents (a curriculum) while the judge stays fixed.
    /// </summary>
    public sealed class ArenaTrainer
    {
        public readonly ArenaRunOptions Options;
        private readonly List<ArenaInstance> instances = new List<ArenaInstance>();
        private int round;

        public IReadOnlyList<ArenaInstance> Instances => instances;

        public ArenaTrainer(ArenaRunOptions options)
        {
            Options = options ?? new ArenaRunOptions();
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
        /// Plays <paramref name="episodes"/> encounters in every instance on <paramref name="trainingConfig"/>
        /// (the options' config when null), then evaluates them all on the options' config.
        /// </summary>
        public ArenaReport RunRound(int episodes, ArenaConfig trainingConfig = null, Action<ArenaInstance, EpisodeResult> progress = null)
        {
            DateTime started = DateTime.UtcNow;
            round++;
            ArenaConfig training = trainingConfig ?? Options.Config;
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.Threads) };
            Parallel.For(0, instances.Count, parallel, i =>
            {
                ArenaInstance inst = instances[i];
                inst.Episodes.Clear();
                inst.Match.Config = training;
                for (int e = 0; e < episodes; e++)
                {
                    EpisodeResult result = inst.Match.RunEpisode();
                    result.Instance = inst.Index;
                    inst.Episodes.Add(result);
                    inst.TotalEpisodes++;
                    progress?.Invoke(inst, result);
                }
                inst.Match.Config = Options.Config;
            });
            EvalResult control = null;
            Parallel.For(-1, instances.Count, parallel, i =>
            {
                if (i < 0)
                {
                    control = Evaluate(Options.Config, null, Options.EvalEpisodes, Options.Seed * 7919 + round);
                    return;
                }
                ArenaInstance inst = instances[i];
                inst.Eval = Evaluate(Options.Config, inst.Policy.Serialize(), Options.EvalEpisodes, Options.Seed * 7919 + round);
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
            return new ArenaReport
            {
                Round = round,
                TrainingSetup = Describe(training),
                Instances = instances,
                Control = control,
                Best = best,
                Seconds = (DateTime.UtcNow - started).TotalSeconds,
            };
        }

        public static string Describe(ArenaConfig c)
        {
            return c.LearnerGear.ToString().ToLowerInvariant() + " vs " + c.OpponentGear.ToString().ToLowerInvariant()
                + (c.OpponentDodgeChance > 0f ? " (dodge " + c.OpponentDodgeChance.ToString("0.###", CultureInfo.InvariantCulture) + ")" : "")
                + ", " + c.SpareSpears + " spare spears, " + (c.RandomRooms ? "random rooms" : "fixed room");
        }

        /// <summary>
        /// Plays <paramref name="episodes"/> encounters with a copy of the policy that neither
        /// explores nor learns (or with the Stage 2 rules when <paramref name="policyText"/> is
        /// null) and reports how it did. Same seed, same encounters, so policies are compared
        /// on equal terms.
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
                else result.Timeouts++;
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

        /// <summary>One round from scratch: the simplest way to use the trainer.</summary>
        public static ArenaReport Run(ArenaRunOptions options, Action<ArenaInstance, EpisodeResult> progress = null)
        {
            return new ArenaTrainer(options).RunRound(options.Episodes, null, progress);
        }
    }
}
