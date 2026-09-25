using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Hunted.Core;
using Hunted.Core.Arena;

namespace Hunted.Arena
{
    /// <summary>
    /// Command line for the headless arena. Trains several Stage 3 policies in parallel
    /// against the Stage 2 rules, round after round, and after every round writes the
    /// best policy as the baseline tactics file plus a report. See docs/arena.md.
    /// </summary>
    public static class Program
    {
        private const string Usage = @"Hunted arena: trains the Stage 3 Pursuer's tactics against the Stage 2 rules, headless.

usage: dotnet run --project tools/Hunted.Arena -c Release -- [options]

  --instances N     arenas (and policies) trained at the same time      [4]
  --episodes N      encounters per instance per round                   [300]
  --eval N          encounters per policy per evaluation                [100]
  --rounds N        rounds to run; 0 = until --hours or a STOP file     [1]
  --hours H         stop after the round that passes this many hours    [0 = no limit]
  --seed N          base seed; instance i uses seed*1000+i              [1]
  --threads N       parallel threads                                    [cores]
  --out DIR         output folder                                       [arena-out]
  --from FILE       continue training from this tactics file
  --resume          continue from DIR/dion_hunted_baseline_tactics.txt if it exists
  --gear G          learner's gear: none, rock, spear, explosive        [spear]
  --opponent-gear G opponent's gear                                     [spear]
  --spares N        spare spears in the room                            [3]
  --dodge P         opponent chance per tick to jump a thrown weapon    [0]
  --max-ticks N     ticks per encounter before a draw                   [2400]
  --fixed-room      use the fixed test room instead of random layouts
  --curriculum      rotate gear and a dodging opponent from round to round
                    (evaluation stays on the setup above)
  --block N         encounters per row of report.csv                    [500]
  --log-decisions   also write decisions.csv (features, tactic, credit)
  --install [DIR]   copy the baseline into the game's ModConfigs folder
  --quiet           no progress lines during a round

Writes DIR/dion_hunted_baseline_tactics.txt (best policy), DIR/instances/instance_N.txt,
DIR/report.csv (one row per instance per block, appended every round) and
DIR/summary.txt (per round). Create DIR/STOP to end a long run after the current round.";

        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("arena: " + e.Message);
                Console.Error.WriteLine(e.StackTrace);
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            var options = new ArenaRunOptions();
            int rounds = 1;
            double hours = 0;
            string outDir = "arena-out";
            string from = null;
            bool resume = false;
            bool curriculum = false;
            int block = 500;
            bool logDecisions = false;
            bool install = false;
            string installDir = null;
            bool quiet = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                switch (a)
                {
                    case "--instances": options.Instances = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--episodes": options.Episodes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--eval": options.EvalEpisodes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--rounds": rounds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--hours": hours = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seed": options.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--threads": options.Threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--out": outDir = Next(); break;
                    case "--from": from = Next(); break;
                    case "--resume": resume = true; break;
                    case "--gear": options.Config.LearnerGear = ArenaConfig.ParseGear(Next()); break;
                    case "--opponent-gear": options.Config.OpponentGear = ArenaConfig.ParseGear(Next()); break;
                    case "--spares": options.Config.SpareSpears = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--dodge": options.Config.OpponentDodgeChance = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--max-ticks": options.Config.MaxTicks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--fixed-room": options.Config.RandomRooms = false; break;
                    case "--curriculum": curriculum = true; break;
                    case "--block": block = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--log-decisions": logDecisions = true; break;
                    case "--install":
                        install = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            installDir = args[++i];
                        }
                        break;
                    case "--quiet": quiet = true; break;
                    case "-h": case "--help": case "/?": Console.WriteLine(Usage); return 0;
                    default: Console.Error.WriteLine("Unknown option " + a); Console.Error.WriteLine(Usage); return 1;
                }
            }

            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "instances"));
            string baselinePath = Path.Combine(outDir, TacticsFiles.Baseline);
            string reportPath = Path.Combine(outDir, "report.csv");
            string stopPath = Path.Combine(outDir, "STOP");
            if (File.Exists(stopPath))
            {
                File.Delete(stopPath);
            }
            if (resume && from == null && File.Exists(baselinePath))
            {
                from = baselinePath;
            }
            if (from != null)
            {
                options.StartFrom = File.ReadAllText(from).Trim();
                Log("Continuing from " + from);
            }

            Log("Arena: " + options.Instances + " instances x " + options.Episodes + " encounters per round, eval " + options.EvalEpisodes + ", seed " + options.Seed + ", threads " + options.Threads
                + ", judged on " + ArenaTrainer.Describe(options.Config) + (curriculum ? ", trained on a rotating curriculum" : "") + ", output " + Path.GetFullPath(outDir));
            Log(rounds > 0 ? "Rounds: " + rounds : "Rounds: until " + (hours > 0 ? hours.ToString(CultureInfo.InvariantCulture) + " h or " : "") + "a STOP file appears");

            var trainer = new ArenaTrainer(options);
            List<ArenaConfig> lessons = curriculum ? Curriculum(options.Config) : null;
            StreamWriter decisions = null;
            if (logDecisions)
            {
                decisions = new StreamWriter(Path.Combine(outDir, "decisions.csv"), false, new UTF8Encoding(false));
                decisions.WriteLine("instance," + string.Join(",", TacticFeatures.Names) + ",tactic,credit");
                foreach (ArenaInstance inst in trainer.Instances)
                {
                    int index = inst.Index;
                    inst.Policy.OnTrained = (features, tactic, credit) =>
                    {
                        var sb = new StringBuilder();
                        sb.Append(index);
                        foreach (float f in features)
                        {
                            sb.Append(',').Append(f.ToString("0.###", CultureInfo.InvariantCulture));
                        }
                        sb.Append(',').Append(((Tactic)tactic).ToString()).Append(',').Append(credit.ToString("0.###", CultureInfo.InvariantCulture));
                        lock (decisions)
                        {
                            decisions.WriteLine(sb.ToString());
                        }
                    };
                }
            }
            if (!File.Exists(reportPath))
            {
                File.WriteAllText(reportPath, ArenaReport.CsvHeader + Environment.NewLine);
            }

            DateTime started = DateTime.UtcNow;
            int round = 0;
            int progressEvery = Math.Max(1, options.Episodes / 10);
            try
            {
                while (true)
                {
                    round++;
                    ArenaConfig lesson = lessons != null ? lessons[(round - 1) % lessons.Count] : null;
                    Log("Round " + round + " starting" + (lesson != null ? " on " + ArenaTrainer.Describe(lesson) : "") + ".");
                    ArenaReport report;
                    try
                    {
                        report = trainer.RunRound(options.Episodes, lesson, (inst, result) =>
                        {
                            if (!quiet && inst.Episodes.Count % progressEvery == 0)
                            {
                                List<ArenaReport.Block> blocks = ArenaReport.Blocks(inst, progressEvery);
                                ArenaReport.Block b = blocks[blocks.Count - 1];
                                Log("instance " + inst.Index + ": " + inst.Episodes.Count + " encounters, last " + b.Count + ": wins " + (100f * b.Wins / b.Count).ToString("0", CultureInfo.InvariantCulture) + "%  reward " + b.MeanReward.ToString("+0.00;-0.00", CultureInfo.InvariantCulture));
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        Log("Round " + round + " failed; stopping. " + e);
                        return 2;
                    }

                    foreach (ArenaInstance inst in trainer.Instances)
                    {
                        File.WriteAllText(Path.Combine(outDir, "instances", "instance_" + inst.Index + ".txt"), inst.Policy.Serialize());
                    }
                    File.WriteAllText(baselinePath, report.Best.Policy.Serialize());
                    File.AppendAllText(reportPath, report.ToCsv(block));
                    var summary = new StringBuilder();
                    summary.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + report.Summary().TrimEnd());
                    int curveBlock = Math.Max(1, options.Episodes / 10);
                    foreach (ArenaInstance inst in trainer.Instances)
                    {
                        summary.AppendLine("  instance " + inst.Index + " over this round (blocks of " + curveBlock + "):");
                        foreach (string line in ArenaReport.Curve(inst, curveBlock))
                        {
                            summary.AppendLine("    " + line);
                        }
                    }
                    summary.AppendLine();
                    File.AppendAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
                    Console.WriteLine(summary.ToString());
                    Log("Best policy (instance " + report.Best.Index + ") written to " + baselinePath);
                    decisions?.Flush();

                    if (install)
                    {
                        Install(baselinePath, installDir);
                    }

                    double elapsedHours = (DateTime.UtcNow - started).TotalHours;
                    if (rounds > 0 && round >= rounds)
                    {
                        break;
                    }
                    if (hours > 0 && elapsedHours >= hours)
                    {
                        Log("Time limit reached after " + elapsedHours.ToString("0.0", CultureInfo.InvariantCulture) + " h.");
                        break;
                    }
                    if (File.Exists(stopPath))
                    {
                        Log("STOP file found; ending after this round.");
                        break;
                    }
                }
            }
            finally
            {
                decisions?.Dispose();
            }
            Log("Done: " + round + " round(s), " + (DateTime.UtcNow - started).TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min.");
            return 0;
        }

        /// <summary>
        /// The rotation a long run trains on: the judged setup first, then the same with an
        /// opponent that sometimes jumps a throw, then rocks on either side, an explosive
        /// spear, and an unarmed opponent that has to scavenge. The learner keeps its policy
        /// across rounds, so it meets every situation the game's features can describe.
        /// </summary>
        private static List<ArenaConfig> Curriculum(ArenaConfig judged)
        {
            var list = new List<ArenaConfig>();
            ArenaConfig With(Action<ArenaConfig> change)
            {
                ArenaConfig c = judged.Clone();
                change(c);
                return c;
            }
            list.Add(judged.Clone());
            list.Add(With(c => c.OpponentDodgeChance = 0.03f));
            list.Add(With(c => c.OpponentGear = WeaponKind.Rock));
            list.Add(With(c => c.LearnerGear = WeaponKind.Rock));
            list.Add(With(c => c.LearnerGear = WeaponKind.ExplosiveSpear));
            list.Add(With(c => c.OpponentGear = WeaponKind.None));
            return list;
        }

        private static void Install(string baselinePath, string dir)
        {
            if (dir == null)
            {
                string game = Environment.GetEnvironmentVariable("RAINWORLD_PATH");
                if (string.IsNullOrEmpty(game))
                {
                    game = @"C:\Program Files (x86)\Steam\steamapps\common\Rain World";
                }
                dir = Path.Combine(game, "RainWorld_Data", "StreamingAssets", "ModConfigs");
            }
            if (!Directory.Exists(dir))
            {
                Log("Not installed: " + dir + " does not exist (set RAINWORLD_PATH or pass --install DIR).");
                return;
            }
            string target = Path.Combine(dir, TacticsFiles.Baseline);
            File.Copy(baselinePath, target, true);
            Log("Installed the baseline to " + target);
        }

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
        }
    }
}
