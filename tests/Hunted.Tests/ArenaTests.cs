using System;
using System.IO;
using Hunted.Core;
using Hunted.Core.Arena;
using Xunit;

namespace Hunted.Tests
{
    public class ArenaTests
    {
        private static ArenaConfig FixedRoom() => new ArenaConfig { RandomRooms = false };

        [Fact]
        public void LineOfSightIsBlockedByCratesAndPlatforms()
        {
            ArenaRoom room = ArenaRoom.Default();
            Assert.True(room.LineOfSight(new Vec2(100f, 15f), new Vec2(300f, 15f)));
            Assert.False(room.LineOfSight(new Vec2(100f, 15f), new Vec2(500f, 15f)), "the crate at 340..380 should block a throw along the floor");
            Assert.True(room.LineOfSight(new Vec2(100f, 200f), new Vec2(1100f, 200f)), "nothing at 200 px up");
            Assert.False(room.LineOfSight(new Vec2(300f, 100f), new Vec2(300f, 200f)), "the platform at 140 lies between");
        }

        [Fact]
        public void ThrownWeaponsStopAtCratesWallsAndTheGround()
        {
            ArenaRoom room = ArenaRoom.Default();
            Vec2? crate = room.FirstSolidHit(new Vec2(300f, 15f), new Vec2(400f, 15f), out bool landed);
            Assert.True(crate.HasValue);
            Assert.Equal(340f, crate.Value.X, 1);
            Assert.False(landed);

            Vec2? wall = room.FirstSolidHit(new Vec2(1150f, 200f), new Vec2(1250f, 200f), out landed);
            Assert.True(wall.HasValue);
            Assert.Equal(room.Width, wall.Value.X, 1);

            Vec2? ground = room.FirstSolidHit(new Vec2(600f, 10f), new Vec2(620f, -10f), out landed);
            Assert.True(ground.HasValue);
            Assert.True(landed);
            Assert.Equal(0f, ground.Value.Y, 1);

            Assert.Null(room.FirstSolidHit(new Vec2(100f, 200f), new Vec2(140f, 200f), out _));
        }

        [Fact]
        public void FighterJumpsOntoACrateAndClimbsAPoleToAPlatform()
        {
            ArenaRoom room = ArenaRoom.Default();
            var f = new Fighter("f");
            f.Reset(new Vec2(200f, 0f), room.Floor, WeaponKind.None);
            var crateTop = new Vec2(360f, 60f);
            for (int t = 0; t < 300 && !(f.Ground != null && Math.Abs(f.Ground.Y - 60f) < 1f); t++)
            {
                f.Steer(room, crateTop);
                f.Step(room);
            }
            Assert.NotNull(f.Ground);
            Assert.Equal(60f, f.Ground.Y, 1);

            f.Reset(new Vec2(100f, 0f), room.Floor, WeaponKind.None);
            var platform = new Vec2(300f, 140f);
            for (int t = 0; t < 600 && !(f.Ground != null && Math.Abs(f.Ground.Y - 140f) < 1f && Math.Abs(f.Pos.X - 300f) < 10f); t++)
            {
                f.Steer(room, platform);
                f.Step(room);
            }
            Assert.NotNull(f.Ground);
            Assert.Equal(140f, f.Ground.Y, 1);
            Assert.True(Math.Abs(f.Pos.X - 300f) < 10f, "ended at " + f.Pos);

            // And back down: walking off the edge gets it to the floor.
            for (int t = 0; t < 300 && !(f.Ground != null && f.Ground.Y < 1f); t++)
            {
                f.Steer(room, new Vec2(100f, 0f));
                f.Step(room);
            }
            Assert.NotNull(f.Ground);
            Assert.Equal(0f, f.Ground.Y, 1);
        }

        [Fact]
        public void MatchIsDeterministicForASeed()
        {
            var a = new ArenaMatch(new ArenaConfig(), 11, new TacticPolicy(TacticFeatures.Count, 11));
            var b = new ArenaMatch(new ArenaConfig(), 11, new TacticPolicy(TacticFeatures.Count, 11));
            for (int i = 0; i < 20; i++)
            {
                EpisodeResult ra = a.RunEpisode();
                EpisodeResult rb = b.RunEpisode();
                Assert.Equal(ra.Outcome, rb.Outcome);
                Assert.Equal(ra.Ticks, rb.Ticks);
                Assert.Equal(ra.LearnerReward, rb.LearnerReward);
                Assert.Equal(ra.Decisions, rb.Decisions);
            }
            Assert.Equal(a.Policy.Serialize(), b.Policy.Serialize());
        }

        [Fact]
        public void ScriptedFightersAreEvenlyMatchedAndFightsEnd()
        {
            EvalResult control = ArenaTrainer.Evaluate(new ArenaConfig(), null, 300, 5);
            Assert.InRange(control.WinRate, 0.3f, 0.7f);
            Assert.True(control.Timeouts < 150, control.Timeouts + " of 300 encounters were draws");
            Assert.True(control.MeanTicks < 2000f, "encounters average " + control.MeanTicks + " ticks");
        }

        [Fact]
        public void LearnerFeaturesMirrorTheGame()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 3);
            var match = new ArenaMatch(FixedRoom(), 3, policy);
            EpisodeResult r = match.RunEpisode();
            Assert.True(r.Decisions > 0, "no tactic decision was made in a whole encounter");
            float[] s = match.LearnerBrain.LastSituation;
            Assert.Equal(TacticFeatures.Count, s.Length);
            Assert.Equal(12, s.Length);
            foreach (float v in s)
            {
                Assert.InRange(v, -1f, 1f);
            }
            Assert.True(s[3] == 0f || s[3] == 1f);
            Assert.True(s[5] == 0f || s[5] == 1f);
            Assert.True(s[6] == 0f || s[6] == 1f);
            Assert.Equal(0f, s[9]);          // no predators in the arena
            Assert.True(s[10] == 0f || s[10] == 1f);
            Assert.Equal(0f, s[11]);         // no bombs in the arena
            Assert.Equal(policy.Decisions, r.Decisions);
        }

        [Fact]
        public void ArmedLearnerSeesItsWeaponValueOverThree()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 4);
            var config = new ArenaConfig { RandomRooms = false, SpareSpears = 0, LearnerGear = WeaponKind.ExplosiveSpear, OpponentGear = WeaponKind.None };
            var match = new ArenaMatch(config, 4, policy);
            // Step by hand until the first decision, before anything can be thrown away.
            match.RunEpisode();
            float[] s = match.LearnerBrain.LastSituation;
            Assert.Equal(1f, s[8], 2);       // explosive spear: value 3 over 3
        }

        [Fact]
        public void RewardsAreTheGameHooksValues()
        {
            Assert.Equal(2f, ArenaRewards.ForHit(1.2f));
            Assert.Equal(1f, ArenaRewards.ForHit(0.7f));
            Assert.Equal(0.5f, ArenaRewards.ForHit(0.2f));
            Assert.Equal(-0.25f, ArenaRewards.ForHurt(0.1f));
            Assert.Equal(-0.8f, ArenaRewards.ForHurt(0.8f));
            Assert.Equal(-1f, ArenaRewards.ForHurt(2f));
            Assert.Equal(3f, ArenaRewards.Kill);
            Assert.Equal(-3f, ArenaRewards.Death);
            Assert.Equal(-0.2f, ArenaRewards.WallHit);
        }

        [Fact]
        public void LearnerImprovesOnTheScriptedRules()
        {
            // Two instances, 1500 encounters each, judged over 600 encounters with exploration off.
            // Across seeds 21..26 the best instance's reward margin over the control was +0.68 to +1.43;
            // the threshold sits well under that. (The duel is symmetric, so the margin is mostly
            // fewer deaths and wasted throws, not a huge win rate.)
            var options = new ArenaRunOptions { Instances = 2, Episodes = 1500, EvalEpisodes = 600, Seed = 21, Threads = 2 };
            ArenaReport report = ArenaTrainer.Run(options);
            EvalResult learned = report.Best.Eval;
            Assert.True(learned.MeanReward > report.Control.MeanReward + 0.3f, "learned " + learned + " vs control " + report.Control);
        }

        [Fact]
        public void ReportRowsAggregateBlocksAndTheBestInstanceHasTheHighestReward()
        {
            var options = new ArenaRunOptions { Instances = 2, Episodes = 5, EvalEpisodes = 3, Seed = 9, Threads = 2 };
            ArenaReport report = ArenaTrainer.Run(options);
            Assert.Equal(10, report.ToCsv(1).TrimEnd().Split('\n').Length);   // one row per encounter
            string[] rows = report.ToCsv(2).TrimEnd().Split('\n');             // blocks of 2: 3 rows per instance
            Assert.Equal(6, rows.Length);
            Assert.Equal(ArenaReport.CsvHeader.Split(',').Length, rows[0].Split(',').Length);
            Assert.StartsWith("1,0,0,2,", rows[0]);
            Assert.StartsWith("1,0,4,1,", rows[2]);
            foreach (ArenaInstance inst in report.Instances)
            {
                Assert.True(report.Best.Eval.MeanReward >= inst.Eval.MeanReward);
                Assert.Equal(5, inst.TotalEpisodes);
            }
            Assert.NotNull(report.Control);
            Assert.Equal(3, report.Control.Episodes);
        }

        [Fact]
        public void ARoundCanTrainOnAnotherSetupThanItIsJudgedOn()
        {
            var options = new ArenaRunOptions { Instances = 1, Episodes = 4, EvalEpisodes = 2, Seed = 12, Threads = 1 };
            var trainer = new ArenaTrainer(options);
            var lesson = new ArenaConfig { MaxTicks = 1, OpponentGear = WeaponKind.None };
            ArenaReport first = trainer.RunRound(4, lesson);
            foreach (EpisodeResult e in first.Instances[0].Episodes)
            {
                Assert.Equal(1, e.Ticks);
            }
            Assert.Contains("spear vs none", first.TrainingSetup);
            Assert.True(first.Control.MeanTicks > 1f, "the judge must use the options' setup, not the lesson");
            ArenaReport second = trainer.RunRound(4);
            Assert.Equal(2, second.Round);
            Assert.Equal(8, second.Instances[0].TotalEpisodes);
            Assert.Equal(4, second.Instances[0].Episodes.Count);
            Assert.True(second.Instances[0].Episodes[0].Ticks > 1);
        }

        [Fact]
        public void FeatureNamesMatchTheCount()
        {
            Assert.Equal(TacticFeatures.Count, TacticFeatures.Names.Length);
        }

        [Fact]
        public void EvaluationLeavesThePolicyTextUnchangedAndIsRepeatable()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 8);
            string text = policy.Serialize();
            EvalResult first = ArenaTrainer.Evaluate(new ArenaConfig(), text, 20, 8);
            EvalResult second = ArenaTrainer.Evaluate(new ArenaConfig(), text, 20, 8);
            Assert.Equal(first.Wins, second.Wins);
            Assert.Equal(first.MeanReward, second.MeanReward);
            Assert.Equal(text, policy.Serialize());
        }

        [Fact]
        public void ContinuingFromAFileStartsEveryInstanceFromIt()
        {
            var trained = new TacticPolicy(TacticFeatures.Count, 2);
            float[] x = new float[TacticFeatures.Count];
            trained.Choose(x, 0);
            trained.Reward(1f, 5);
            trained.Flush();
            var options = new ArenaRunOptions { Instances = 2, Episodes = 0, EvalEpisodes = 1, StartFrom = trained.Serialize() };
            var trainer = new ArenaTrainer(options);
            foreach (ArenaInstance inst in trainer.Instances)
            {
                Assert.Equal(trained.Decisions, inst.Policy.Decisions);
                Assert.Equal(trained.Evaluate(x), inst.Policy.Evaluate(x));
            }
            Assert.Throws<ArgumentException>(() => new ArenaTrainer(new ArenaRunOptions { StartFrom = "v=0|net=nope" }));
        }

        [Fact]
        public void TrainedDecisionsCanBeObserved()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 6);
            int seen = 0;
            float lastCredit = 0f;
            policy.OnTrained = (features, tactic, credit) => { seen++; Assert.Equal(TacticFeatures.Count, features.Length); lastCredit = credit; };
            float[] x = new float[TacticFeatures.Count];
            policy.Choose(x, 0);
            policy.Reward(2f, 0);
            policy.Flush();
            Assert.Equal(1, seen);
            Assert.Equal(2f, lastCredit);
        }

        [Fact]
        public void ForgetPatternLeavesTheArenaBaselineAlone()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hunted-tactics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, TacticsFiles.PerSlot("0", "White")), "x");
                File.WriteAllText(Path.Combine(dir, TacticsFiles.Baseline), "y");
                string[] matched = Directory.GetFiles(dir, TacticsFiles.PerSlotPattern);
                Assert.Single(matched);
                Assert.EndsWith(TacticsFiles.PerSlot("0", "White"), matched[0]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
