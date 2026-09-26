using System;
using System.Collections.Generic;
using System.IO;
using Hunted.Core;
using Hunted.Core.Arena;
using Xunit;

namespace Hunted.Tests
{
    public class ArenaTests
    {
        private static ArenaConfig FixedRoom() => new ArenaConfig { RandomRooms = false };

        private static TacticPolicy ParseConstant(Tactic t, int seed)
        {
            Assert.True(TacticPolicy.TryParse(ArenaTrainer.ConstantPolicy(t), TacticFeatures.Count, seed, out TacticPolicy p));
            p.FixedEpsilon = 0f;
            p.LearningRate = 0f;
            return p;
        }

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
        public void AThrownSpearDropsLikeTheGames()
        {
            // Weapon.Thrown: 40 px per tick; Spear.Update while thrown: 0.9 gravity with 0.45 added back.
            var p = new Projectile(WeaponKind.Spear, new Fighter("t"), new Vec2(0f, 100f), new Vec2(Projectile.Speed, 0f));
            for (int i = 0; i < 8; i++)
            {
                p.Advance();
            }
            Assert.Equal(320f, p.Pos.X, 1);
            Assert.InRange(100f - p.Pos.Y, 14f, 17f);   // about 13 px over 300 px, 38 over 520
            for (int i = 0; i < 5; i++)
            {
                p.Advance();
            }
            Assert.InRange(100f - p.Pos.Y, 38f, 42f);
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
        public void ThePureDuelIsEvenAndFightsEnd()
        {
            // Stage 2 rules on both sides, no player-like rules: the arena's fairness check.
            var pure = new ArenaConfig { OpponentAvoidsExposure = false, OpponentPatienceTicks = 0 };
            EvalResult duel = ArenaTrainer.Evaluate(pure, null, 2000, 5);
            Assert.InRange(duel.WinRate - duel.DeathRate, -0.06f, 0.06f);
            Assert.True(duel.Draws < 400, duel.Draws + " of 2000 encounters were draws");
            Assert.True(duel.MeanTicks < 2000f, "encounters average " + duel.MeanTicks + " ticks");
        }

        [Fact]
        public void TheControlIsTheAlwaysThrowPolicy()
        {
            // The Stage 2 rules in the learner's seat and a policy that always picks Throw are the
            // same fighter, so the control is scored on the same scale as every learned policy.
            EvalResult control = ArenaTrainer.Evaluate(new ArenaConfig(), null, 500, 7);
            EvalResult throwAlways = ArenaTrainer.Evaluate(new ArenaConfig(), ArenaTrainer.ConstantPolicy(Tactic.Throw), 500, 7);
            Assert.True(control.SameOutcomesAs(throwAlways), control + " vs " + throwAlways);
            Assert.NotEqual(0f, control.MeanReward);
        }

        [Fact]
        public void TwoPoliciesOnOneSeedMeetTheSameEncounters()
        {
            var a = new ArenaMatch(new ArenaConfig(), 31, null);
            var b = new ArenaMatch(new ArenaConfig(), 31, ParseConstant(Tactic.Wait, 31));
            for (int i = 0; i < 10; i++)
            {
                a.RunEpisode();
                b.RunEpisode();
                Assert.Equal(a.Room.Width, b.Room.Width);
                Assert.Equal(a.Room.Surfaces.Count, b.Room.Surfaces.Count);
                for (int k = 0; k < a.Room.Surfaces.Count; k++)
                {
                    Assert.Equal(a.Room.Surfaces[k].X0, b.Room.Surfaces[k].X0);
                    Assert.Equal(a.Room.Surfaces[k].Y, b.Room.Surfaces[k].Y);
                }
            }
        }

        [Fact]
        public void NobodyThrowingEndsTheEncounterWhenTheOpponentLosesPatience()
        {
            var config = new ArenaConfig { RandomRooms = false, LearnerGear = WeaponKind.None, OpponentGear = WeaponKind.None, SpareSpears = 0, OpponentPatienceTicks = 200 };
            var match = new ArenaMatch(config, 2, null);
            EpisodeResult r = match.RunEpisode();
            Assert.Equal(EpisodeOutcome.OpponentLeft, r.Outcome);
            Assert.Equal(200, r.Ticks);
            config.OpponentPatienceTicks = 0;
            r = match.RunEpisode();
            Assert.Equal(EpisodeOutcome.Timeout, r.Outcome);
            Assert.Equal(config.MaxTicks, r.Ticks);
        }

        [Fact]
        public void TheOpponentNeverClimbsTowardAnArmedLearnerAbove()
        {
            // The learner waits on the high platform of the fixed room; the opponent starts on the
            // floor under it. With the player-like rule it holds at the foot of the pole; without
            // it (the pure Stage 2 rules) it climbs.
            foreach (bool avoids in new[] { true, false })
            {
                var config = new ArenaConfig { RandomRooms = false, OpponentAvoidsExposure = avoids, OpponentPatienceTicks = 0, MaxTicks = 600, SpareSpears = 0 };
                var match = new ArenaMatch(config, 40, ParseConstant(Tactic.Wait, 40));
                bool climbed = false;
                match.OnTick = m =>
                {
                    if (m.Tick == 1)
                    {
                        ArenaRoom room = m.Room;
                        m.Learner.Reset(new Vec2(600f, 260f), room.SurfaceAt(600f, 260f), WeaponKind.Spear);
                        m.Opponent.Reset(new Vec2(560f, 0f), room.Floor, WeaponKind.Spear);
                    }
                    if (m.Opponent.OnPole != null || (m.Opponent.Ground == null && m.Opponent.Vel.Y > 0f))
                    {
                        climbed = true;
                    }
                };
                match.RunEpisode();
                Assert.Equal(!avoids, climbed);
            }
        }

        [Fact]
        public void ContactHoldsForAHundredTicksLikeTheGamesTracker()
        {
            // Both stand on the floor of the fixed room with a crate between them (no sight), stunned
            // so nobody moves or throws. The encounter opens with a fix (contact), which the tracker
            // keeps for 100 ticks before looking again; from then on since_seen counts up from zero,
            // and 400 ticks after that the Pursuer's Sense gives a fresh fix through the crate.
            var config = new ArenaConfig { RandomRooms = false, LearnerGear = WeaponKind.None, OpponentGear = WeaponKind.None, SpareSpears = 0, OpponentPatienceTicks = 0, MaxTicks = 700 };
            var match = new ArenaMatch(config, 3, null);
            var seenAt = new Dictionary<int, bool>();
            var sinceAt = new Dictionary<int, int>();
            match.OnTick = m =>
            {
                if (m.Tick == 1)
                {
                    m.Learner.Reset(new Vec2(300f, 0f), m.Room.Floor, WeaponKind.None);
                    m.Opponent.Reset(new Vec2(420f, 0f), m.Room.Floor, WeaponKind.None);   // the crate at 340..380 hides it
                    m.Learner.Stun = 700;
                    m.Opponent.Stun = 700;
                }
                seenAt[m.Tick] = m.LearnerBrain.Seen;
                sinceAt[m.Tick] = m.LearnerBrain.TicksSinceSeen;
            };
            match.RunEpisode();
            Assert.True(seenAt[50], "contact should still hold at tick 50");
            Assert.Equal(0, sinceAt[50]);
            Assert.True(seenAt[100], "contact should still hold at tick 100");
            Assert.False(seenAt[102], "after 100 ticks the tracker looks again and finds the crate in the way");
            Assert.Equal(0, sinceAt[100]);
            Assert.Equal(50, sinceAt[150]);
            Assert.Equal(300, sinceAt[400]);
            Assert.True(seenAt[502], "the sense fix should have set contact");
            Assert.Equal(0, sinceAt[502]);
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
            Assert.Equal(15, s.Length);
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
            Assert.True(s[12] == 0f || s[12] == 1f);   // on the ground, free to start a move
            Assert.True(s[13] == 0f || s[13] == 1f);   // a spear flying at it
            Assert.True(s[14] == 0f || s[14] == 1f);   // target below
            Assert.Equal(policy.Decisions, r.Decisions);
        }

        [Fact]
        public void ArmedLearnerSeesItsWeaponValueOverThree()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 4);
            var config = new ArenaConfig { RandomRooms = false, SpareSpears = 0, LearnerGear = WeaponKind.ExplosiveSpear, OpponentGear = WeaponKind.None };
            var match = new ArenaMatch(config, 4, policy);
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
        public void LearnerBeatsTheControlAndEveryFixedTactic()
        {
            // Four instances, 30 000 encounters each, judged over 4000 encounters with exploration off.
            // The bar a baseline must clear to be shipped: better than the scored control and better
            // than every always-one-tactic policy by more than the noise floor. With ten tactics, six
            // of them moves, this only holds because moves the body cannot start are masked out of the
            // choice: offered anyway and run as Throw, the move arms learned Throw's airborne values,
            // the margin over always-Wait was -0.11 here at 30 000, and it took 100 000 to clear.
            var options = new ArenaRunOptions { Instances = 4, Episodes = 30000, EvalEpisodes = 4000, Seed = 22, Threads = 4 };
            ArenaReport report = ArenaTrainer.Run(options);
            EvalResult learned = report.Best.Eval;
            Assert.True(learned.MeanReward > report.Control.MeanReward + 0.3f, "learned " + learned + " vs control " + report.Control);
            for (int t = 0; t < report.Yardsticks.Length; t++)
            {
                Assert.True(learned.MeanReward > report.Yardsticks[t].MeanReward, "learned " + learned + " vs always " + (Tactic)t + " " + report.Yardsticks[t]);
            }
            Assert.True(report.ClearsBar, "margin over always-" + report.BestYardstick + " is " + report.MarginOverYardsticks + ", noise floor " + report.NoiseFloor);
        }

        [Fact]
        public void TheBaselineOnlyAdvancesWhenARoundBeatsTheIncumbent()
        {
            var options = new ArenaRunOptions { Instances = 1, Episodes = 200, EvalEpisodes = 200, Seed = 15, Threads = 1 };
            var trainer = new ArenaTrainer(options);
            ArenaReport first = trainer.RunRound(200);
            Assert.True(first.Improved);
            Assert.Equal(1, first.BestSoFarRound);
            string incumbent = first.BestSoFarPolicy;
            // A sabotaged round: one-tick encounters with an unarmed learner make no decisions, so
            // the policy cannot change and its judged score is identical, which is not an improvement.
            ArenaReport second = trainer.RunRound(200, new[] { new ArenaConfig { MaxTicks = 1, LearnerGear = WeaponKind.None, SpareSpears = 0 } });
            Assert.False(second.Improved);
            Assert.Equal(1, second.BestSoFarRound);
            Assert.Equal(incumbent, second.BestSoFarPolicy);
            Assert.Equal(first.BestSoFar.MeanReward, second.BestSoFar.MeanReward);
            Assert.NotNull(second.Yardsticks);
            Assert.Equal(TacticPolicy.TacticCount, second.Yardsticks.Length);
            Assert.NotNull(second.PureDuel);
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
            var options = new ArenaRunOptions { Instances = 1, Episodes = 4, EvalEpisodes = 20, Seed = 12, Threads = 1 };
            var trainer = new ArenaTrainer(options);
            var lesson = new ArenaConfig { MaxTicks = 1, OpponentGear = WeaponKind.None };
            ArenaReport first = trainer.RunRound(4, new[] { lesson });
            foreach (EpisodeResult e in first.Instances[0].Episodes)
            {
                Assert.Equal(1, e.Ticks);
            }
            Assert.Contains("spear vs none", first.TrainingSetup);
            Assert.True(first.Control.MeanTicks > 1f, "the judge must use the options' setup, not the lesson");
            Assert.Equal(20, first.Control.Episodes);
            ArenaReport second = trainer.RunRound(4);
            Assert.Equal(2, second.Round);
            Assert.Equal(8, second.Instances[0].TotalEpisodes);
            Assert.Equal(4, second.Instances[0].Episodes.Count);
            Assert.True(second.Instances[0].Episodes[0].Ticks > 1);
        }

        [Fact]
        public void LessonsAreMixedEncounterByEncounter()
        {
            var options = new ArenaRunOptions { Instances = 1, Episodes = 6, EvalEpisodes = 2, Seed = 13, Threads = 1 };
            var trainer = new ArenaTrainer(options);
            var lessons = new[] { new ArenaConfig { MaxTicks = 1 }, new ArenaConfig { MaxTicks = 2 }, new ArenaConfig { MaxTicks = 3 } };
            ArenaReport report = trainer.RunRound(6, lessons);
            List<EpisodeResult> episodes = report.Instances[0].Episodes;
            Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, new[] { episodes[0].Ticks, episodes[1].Ticks, episodes[2].Ticks, episodes[3].Ticks, episodes[4].Ticks, episodes[5].Ticks });
            Assert.StartsWith("a mix of ", report.TrainingSetup);
        }

        [Fact]
        public void FeatureNamesMatchTheCount()
        {
            Assert.Equal(TacticFeatures.Count, TacticFeatures.Names.Length);
            Assert.Equal(TacticPolicy.TacticCount, Enum.GetValues(typeof(Tactic)).Length);
            Assert.True(Tactics.IsMove(Tactic.Slide) && Tactics.IsMove(Tactic.FlipThrow) && !Tactics.IsMove(Tactic.Wait));
        }

        [Fact]
        public void AMaskedTacticIsNeverChosen()
        {
            var policy = new TacticPolicy(TacticFeatures.Count, 41);
            float[] x = new float[TacticFeatures.Count];
            var allowed = new bool[TacticPolicy.TacticCount];
            for (int a = 0; a < allowed.Length; a++)
            {
                allowed[a] = !Tactics.IsMove((Tactic)a);
            }
            foreach (float eps in new[] { 1f, 0f, 0.5f })
            {
                policy.FixedEpsilon = eps;
                for (int i = 0; i < 300; i++)
                {
                    Assert.False(Tactics.IsMove(policy.Choose(x, i * 100, allowed)), "a move came back with exploration " + eps);
                }
            }
            // With everything allowed the random draw reaches every arm.
            policy.FixedEpsilon = 1f;
            var seen = new HashSet<Tactic>();
            for (int i = 0; i < 300; i++)
            {
                seen.Add(policy.Choose(x, 100000 + i * 100));
            }
            Assert.Equal(TacticPolicy.TacticCount, seen.Count);
            Assert.Throws<ArgumentException>(() => policy.Choose(x, 0, new bool[3]));
        }

        [Fact]
        public void TheIncomingPredicateIsTheSharedOne()
        {
            // dx, dy: the body relative to the weapon; vx, vy: the weapon's velocity.
            Assert.True(TacticFeatures.Incoming(200f, 0f, 40f, 0f));       // level, approaching
            Assert.False(TacticFeatures.Incoming(200f, 0f, -40f, 0f));     // receding
            Assert.False(TacticFeatures.Incoming(300f, 0f, 40f, 0f));      // out of range (230)
            Assert.False(TacticFeatures.Incoming(100f, 80f, 40f, 0f));     // too far above the body
            Assert.True(TacticFeatures.Incoming(0f, -50f, 0f, -40f));      // straight down onto it, within the band
            Assert.Equal(230f, TacticFeatures.IncomingRangePx);
        }

        [Fact]
        public void FlipThrowDirectionIsTheSharedRule()
        {
            Assert.Equal(-1, Tactics.FlipThrowY(10f, -120f, true));
            Assert.Equal(-1, Tactics.FlipThrowY(10f, -120f, false));
            Assert.Equal(1, Tactics.FlipThrowY(-30f, 100f, true));
            Assert.Equal(0, Tactics.FlipThrowY(-30f, 100f, false));   // upward throws off in the game: level
            Assert.Equal(0, Tactics.FlipThrowY(200f, -120f, true));   // not in the column
            Assert.Equal(0, Tactics.FlipThrowY(10f, 0f, true));
        }

        [Fact]
        public void AnOlderTacticsFileIsRejectedWithItsVersionInTheReason()
        {
            string v1 = "v=1|d=5|net=" + new TinyNet(12, 16, 4, 1).Serialize();
            Assert.False(TacticPolicy.TryParse(v1, TacticFeatures.Count, 1, out _));
            Assert.Contains("version 1", TacticPolicy.RejectionReason(v1, TacticFeatures.Count));
            string wrongShape = "v=" + TacticPolicy.Version + "|net=" + new TinyNet(12, 16, TacticPolicy.TacticCount, 1).Serialize();
            Assert.Contains("inputs", TacticPolicy.RejectionReason(wrongShape, TacticFeatures.Count));
            Assert.Null(TacticPolicy.RejectionReason(new TacticPolicy(TacticFeatures.Count, 1).Serialize(), TacticFeatures.Count));
        }

        // ---------------------------------------------------------------- scripted moves

        private static Fighter OnFloor(ArenaRoom room, float x)
        {
            var f = new Fighter("f");
            f.Reset(new Vec2(x, 0f), room.Floor, WeaponKind.Spear);
            return f;
        }

        private static int RunMove(Fighter f, ArenaRoom room, Tactic kind, int dir, Action<Fighter> each = null)
        {
            Assert.True(f.StartMove(kind, dir));
            int ticks = 0;
            while (f.Move.HasValue && ticks < 400)
            {
                f.Step(room);
                ticks++;
                each?.Invoke(f);
            }
            Assert.Null(f.Move);
            return ticks;
        }

        [Fact]
        public void ASlideIsLowFastAndAboutSixTiles()
        {
            var room = new ArenaRoom(1200f, 500f);
            Fighter f = OnFloor(room, 200f);
            int lowTicks = 0;
            int ticks = RunMove(f, room, Tactic.Slide, 1, x => { if (x.Low) lowTicks++; });
            Assert.InRange(f.Pos.X - 200f, 90f, 140f);
            Assert.Equal(Fighter.SlideTicks, lowTicks);
            Assert.True(f.Recover > 0, "no recovery after the slide");
            Assert.False(f.Low);
            Assert.NotNull(f.Ground);
        }

        [Theory]
        [InlineData(150f, false)]
        [InlineData(300f, true)]
        public void ALevelThrowPassesOverASlidingBodyOnlyAtCloseRange(float gap, bool hits)
        {
            // A level throw leaves chest height (27 px) and drops at the game's rate; a flat body's
            // chunks lie at 9 px. From 150 px the spear is still above them; from 300 px it has
            // dropped onto them. This is why the incoming range is 230 px and not the throw range.
            var room = new ArenaRoom(1200f, 500f);
            Fighter slider = OnFloor(room, 100f);
            slider.StartMove(Tactic.Slide, 1);
            for (int i = 0; i <= Fighter.CrouchTicks; i++)
            {
                slider.Step(room);
            }
            Assert.True(slider.Low);
            Fighter thrower = OnFloor(room, 100f + gap);
            thrower.Facing = -1;
            Projectile p = thrower.Throw(-1);
            bool hit = false;
            for (int t = 0; t < 30 && !hit && p.Pos.X > 0f; t++)
            {
                Vec2 prev = p.Advance();
                hit = ArenaMatch.HitsBody(prev, p.Pos, slider);
            }
            Assert.Equal(hits, hit);

            // A standing body is hit from either distance.
            Fighter stander = OnFloor(room, 100f);
            Projectile q = thrower.Throw(-1);
            thrower.Held = WeaponKind.Spear;
            bool hitStanding = false;
            for (int t = 0; t < 30 && !hitStanding && q.Pos.X > 0f; t++)
            {
                Vec2 prev = q.Advance();
                hitStanding = ArenaMatch.HitsBody(prev, q.Pos, stander);
            }
            Assert.True(hitStanding);
        }

        [Fact]
        public void AChargedPounceCrawlsChargesAndFliesAboutEightTiles()
        {
            var room = new ArenaRoom(1200f, 500f);
            Fighter f = OnFloor(room, 200f);
            bool flew = false;
            int ticks = RunMove(f, room, Tactic.Pounce, 1, x => flew |= x.Ground == null);
            Assert.True(flew);
            Assert.True(ticks > Fighter.CrawlTicks + Fighter.ChargeTicks + 10, "no flight: " + ticks + " ticks");
            Assert.InRange(f.Pos.X - 200f, 150f, 230f);
            Assert.NotNull(f.Ground);
        }

        [Fact]
        public void ASlidePounceGoesAboutThirteenTilesAndARollAddsSeven()
        {
            var room = new ArenaRoom(1200f, 500f);
            Fighter f = OnFloor(room, 100f);
            RunMove(f, room, Tactic.SlidePounce, 1);
            float pounce = f.Pos.X - 100f;
            Assert.InRange(pounce, 230f, 300f);
            Assert.NotNull(f.Ground);

            f = OnFloor(room, 100f);
            int lowAfterLanding = 0;
            bool flew = false;
            bool landedOnce = false;
            RunMove(f, room, Tactic.Roll, 1, x =>
            {
                if (x.Ground == null) flew = true;
                if (flew && x.Ground != null) landedOnce = true;
                if (landedOnce && x.Low) lowAfterLanding++;
            });
            Assert.Equal(Fighter.RollTicks, lowAfterLanding);
            Assert.InRange(f.Pos.X - 100f - pounce, 120f, 160f);
        }

        [Fact]
        public void ABackflipRunsUpThenLandsNearWhereItStarted()
        {
            var room = new ArenaRoom(1200f, 500f);
            Fighter f = OnFloor(room, 300f);
            float farthest = 300f;
            int airborne = 0;
            RunMove(f, room, Tactic.Backflip, 1, x => { farthest = Math.Max(farthest, x.Pos.X); if (x.Ground == null) airborne++; });
            Assert.InRange(farthest - 300f, 45f, 60f);   // the run-up
            Assert.InRange(airborne, 15f, 25f);          // the flip
            Assert.True(f.Pos.X < farthest - 30f, "the flip did not carry the body back");
            Assert.InRange(f.Pos.X - 300f, -20f, 40f);

            // Already running that way: the run-up is skipped.
            f = OnFloor(room, 300f);
            for (int i = 0; i < 15; i++)
            {
                f.MoveX = 1;
                f.Step(room);
            }
            float start = f.Pos.X;
            farthest = start;
            RunMove(f, room, Tactic.Backflip, 1, x => farthest = Math.Max(farthest, x.Pos.X));
            Assert.InRange(farthest - start, 0f, 10f);
        }

        [Fact]
        public void AMoveNeedsTheGroundAndEndsOnAHit()
        {
            var room = new ArenaRoom(1200f, 500f);
            Fighter f = OnFloor(room, 300f);
            f.Jump = true;
            f.Step(room);
            Assert.Null(f.Ground);
            Assert.False(f.StartMove(Tactic.Slide, 1));
            while (f.Ground == null)
            {
                f.Step(room);
            }
            Assert.True(f.StartMove(Tactic.Slide, 1));
            Assert.False(f.StartMove(Tactic.Pounce, 1)); // one at a time
            f.Hurt(0.2f, 30);
            f.Step(room);
            Assert.Null(f.Move);
            Assert.False(f.Low);
        }

        [Fact]
        public void AFlipThrowDownGoesThroughThePlatform()
        {
            ArenaRoom room = ArenaRoom.Default();  // platform 180..480 at 140
            var from = new Vec2(300f, 167f);
            var to = new Vec2(300f, 100f);
            Assert.NotNull(room.FirstSolidHit(from, to, out bool landed));
            Assert.True(landed);
            Assert.Null(room.FirstSolidHit(from, to, out _, throughPlatforms: true));
            Assert.NotNull(room.FirstSolidHit(from, new Vec2(300f, -10f), out landed, throughPlatforms: true)); // the floor still stops it
            Assert.True(landed);

            var f = new Fighter("f");
            f.Reset(new Vec2(300f, 140f), room.SurfaceAt(300f, 140f), WeaponKind.Spear);
            Projectile p = f.ThrowVertical(-1);
            Assert.True(p.ThroughPlatforms);
            Assert.Equal(WeaponKind.None, f.Held);
        }

        [Fact]
        public void TheBrainStartsMovesOnlyWhenFreeAndDecidesAgainWhenTheyEnd()
        {
            var config = new ArenaConfig { RandomRooms = false, SpareSpears = 0, OpponentGear = WeaponKind.None, OpponentPatienceTicks = 0 };
            var match = new ArenaMatch(config, 31, ParseConstant(Tactic.SlidePounce, 31));
            int started = 0;
            int decisionsMidMove = 0;
            int promptDecisions = 0;
            int lastDecisions = 0;
            int endedTick = -1;
            bool wasMoving = false;
            match.OnTick = m =>
            {
                if (m.Tick == 1)
                {
                    endedTick = -1; // a new encounter: the clock restarted and the body was reset
                    wasMoving = false;
                }
                bool moving = m.Learner.Move.HasValue;
                int d = m.Policy.Decisions;
                if (d != lastDecisions && Tactics.IsMove(m.LearnerBrain.Tactic))
                {
                    Assert.True(moving, "a move was chosen at tick " + m.Tick + " but the body is not running one"); // the mask: a move is only offered when it can start
                }
                if (moving && !wasMoving) started++;
                if (moving && wasMoving && d != lastDecisions) decisionsMidMove++;
                if (!moving && wasMoving) endedTick = m.Tick;
                // The tick after a move ends, an armed, engaging, unstunned learner decides again at once.
                if (endedTick == m.Tick - 1 && m.LearnerBrain.CurrentMode == ArenaBrain.Mode.Engage && m.Learner.Held != WeaponKind.None && m.Learner.Stun == 0)
                {
                    Assert.True(d > lastDecisions, "no decision the tick after a move ended (tick " + m.Tick + ")");
                    promptDecisions++;
                    endedTick = -1;
                }
                wasMoving = moving;
                lastDecisions = d;
            };
            for (int e = 0; e < 5; e++)
            {
                match.RunEpisode();
            }
            Assert.True(started > 0, "the learner never pounced");
            Assert.True(promptDecisions > 0, "no move ended while still engaging");
            Assert.Equal(0, decisionsMidMove);
        }

        [Fact]
        public void AThrowAtTheLearnerTriggersADecisionAtOnce()
        {
            var config = new ArenaConfig { RandomRooms = false, SpareSpears = 0, OpponentPatienceTicks = 0 };
            var policy = new TacticPolicy(TacticFeatures.Count, 33);
            var match = new ArenaMatch(config, 33, policy);
            int incomingTick = -1;
            int decisionsAtIncoming = 0;
            int decisionsSoon = -1;
            match.OnTick = m =>
            {
                if (incomingTick < 0 && m.WeaponFlyingAt(m.Learner) && m.LearnerBrain.CurrentMode == ArenaBrain.Mode.Engage && m.Learner.Held != WeaponKind.None && !m.Learner.Move.HasValue)
                {
                    incomingTick = m.Tick;
                    decisionsAtIncoming = m.Policy.Decisions;
                }
                else if (incomingTick >= 0 && decisionsSoon < 0 && m.Tick == incomingTick + 1)
                {
                    decisionsSoon = m.Policy.Decisions;
                }
            };
            for (int e = 0; e < 20 && incomingTick < 0; e++)
            {
                match.RunEpisode();
            }
            Assert.True(incomingTick >= 0, "the opponent never threw at an armed, engaging learner");
            Assert.True(decisionsSoon > decisionsAtIncoming, "no decision within a tick of the spear coming in");
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
        public void ABaselineShedsItsCountersWhenLoadedAsAPrior()
        {
            var trained = new TacticPolicy(TacticFeatures.Count, 5);
            float[] x = new float[TacticFeatures.Count];
            for (int i = 0; i < 2000; i++)
            {
                trained.Choose(x, i * 100);
                trained.Reward(0.5f, i * 100 + 5);
            }
            trained.Flush();
            Assert.True(trained.Epsilon < 0.06f);
            string prior = TacticsFiles.ForBaselineLoad(trained.Serialize());
            Assert.DoesNotContain("|d=", prior);
            Assert.DoesNotContain("|rs=", prior);
            Assert.True(TacticPolicy.TryParse(prior, TacticFeatures.Count, 5, out TacticPolicy loaded), prior);
            Assert.Equal(0.3f, loaded.Epsilon, 3);
            Assert.Equal(0, loaded.Decisions);
            Assert.Equal(0f, loaded.RecentSurprise);
            Assert.Equal(0f, loaded.BaselineSurprise);
            Assert.Equal(trained.Evaluate(x), loaded.Evaluate(x));
            Assert.Equal("", TacticsFiles.ForBaselineLoad(""));
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

        [Fact]
        public void DocsContainNoControlBytes()
        {
            string docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs"));
            if (!Directory.Exists(docs))
            {
                return; // not running from the repo layout
            }
            foreach (string file in Directory.GetFiles(docs, "*.md"))
            {
                foreach (char c in File.ReadAllText(file))
                {
                    Assert.False(c < ' ' && c != '\n' && c != '\r' && c != '\t', file + " contains a control byte " + (int)c);
                }
            }
        }
    }
}
