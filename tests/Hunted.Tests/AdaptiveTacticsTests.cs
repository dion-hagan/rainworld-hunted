using Hunted.Core;
using Xunit;

namespace Hunted.Tests
{
    public class AdaptiveTacticsTests
    {
        [Fact]
        public void TinyNetLearnsXor()
        {
            var net = new TinyNet(2, 8, 1, 7);
            float[][] inputs = { new[] { 0f, 0f }, new[] { 0f, 1f }, new[] { 1f, 0f }, new[] { 1f, 1f } };
            float[] targets = { 0f, 1f, 1f, 0f };
            for (int epoch = 0; epoch < 4000; epoch++)
            {
                for (int i = 0; i < 4; i++)
                {
                    net.Train(inputs[i], 0, targets[i], 0.1f);
                }
            }
            var output = new float[1];
            for (int i = 0; i < 4; i++)
            {
                net.Forward(inputs[i], output);
                Assert.True(System.Math.Abs(output[0] - targets[i]) < 0.25f, "input " + i + " gave " + output[0]);
            }
        }

        [Fact]
        public void TinyNetRoundTripsThroughText()
        {
            var net = new TinyNet(3, 5, 2, 42);
            float[] input = { 0.2f, -0.7f, 1f };
            var before = new float[2];
            net.Forward(input, before);

            string text = net.Serialize();
            Assert.DoesNotContain("|", text);
            Assert.True(TinyNet.TryParse(text, out TinyNet back), text);
            var after = new float[2];
            back.Forward(input, after);
            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[1], after[1]);
        }

        [Fact]
        public void TinyNetRejectsGarbage()
        {
            Assert.False(TinyNet.TryParse("", out _));
            Assert.False(TinyNet.TryParse("2,2,1,1,0.5", out _));
            Assert.False(TinyNet.TryParse("2,2,1,9," + new TinyNet(2, 2, 1, 1).Serialize().Substring(8), out _));
        }

        [Fact]
        public void PolicyLearnsFromSparseRewards()
        {
            // As in the game: most decisions get nothing, and only some situations ever pay.
            var policy = new TacticPolicy(2, 3) { LearningRate = 0.05f, RewardWindowTicks = 80 };
            float[] near = { 1f, 0f };
            float[] far = { 0f, 1f };
            int tick = 0;
            for (int i = 0; i < 400; i++)
            {
                policy.FixedEpsilon = i < 100 ? 1f : 0.2f;   // explore first, then mostly exploit
                Tactic chosen = policy.Choose(near, tick);
                if (chosen == Tactic.Wait && i % 2 == 0)
                {
                    policy.Reward(1f, tick + 10);          // waiting near the player sometimes pays
                }
                else if (chosen == Tactic.CloseIn)
                {
                    policy.Reward(-1f, tick + 10);         // closing in gets you speared
                }
                tick += 100;                                // the previous decision expires untouched otherwise
                chosen = policy.Choose(far, tick);
                if (chosen == Tactic.Throw && i % 3 == 0)
                {
                    policy.Reward(1f, tick + 10);          // throwing from far away sometimes hits
                }
                tick += 100;
            }
            policy.FixedEpsilon = 0f;
            Assert.Equal(Tactic.Wait, policy.Choose(near, tick));
            Assert.Equal(Tactic.Throw, policy.Choose(far, tick + 100));
        }

        [Fact]
        public void UnrewardedTacticsConvergeToZeroInsteadOfStayingRandom()
        {
            var policy = new TacticPolicy(1, 11) { FixedEpsilon = 1f, LearningRate = 0.1f, RewardWindowTicks = 80 };
            float[] x = { 1f };
            int tick = 0;
            for (int i = 0; i < 600; i++)
            {
                Tactic chosen = policy.Choose(x, tick);
                if (chosen == Tactic.Throw)
                {
                    policy.Reward(-0.2f, tick + 5, throwOutcome: false);   // only Throw ever gets a signal, and a bad one
                }
                tick += 100;
            }
            policy.Flush();
            float[] scores = policy.Evaluate(x);
            Assert.True(scores[(int)Tactic.Throw] < -0.1f, "throw " + scores[(int)Tactic.Throw]);
            for (int t = 1; t < TacticPolicy.TacticCount; t++)
            {
                Assert.True(System.Math.Abs(scores[t]) < 0.05f, ((Tactic)t) + " stayed at " + scores[t]);
            }
        }

        [Fact]
        public void ThrowOutcomesGoOnlyToTheDecisionThatThrew()
        {
            // Twin policies share seed, so they start identical and only differ by what was credited.
            float[] a = { 1f };
            float[] b = { 0f };
            TacticPolicy Make() => new TacticPolicy(1, 5) { FixedEpsilon = 1f, LearningRate = 0.5f, RewardWindowTicks = 80 };

            var nobodyThrew = Make();            // a throw outcome with no recorded throw credits nobody
            nobodyThrew.Choose(a, 0); nobodyThrew.Choose(b, 20);
            nobodyThrew.Reward(2f, 30, throwOutcome: true);
            nobodyThrew.Flush();

            var noReward = Make();
            noReward.Choose(a, 0); noReward.Choose(b, 20);
            noReward.Flush();
            Assert.Equal(noReward.Evaluate(a), nobodyThrew.Evaluate(a));

            var firstThrew = Make();             // credited to the decision made at tick 0 (situation a)
            firstThrew.Choose(a, 0); firstThrew.NoteThrow(); firstThrew.Choose(b, 20);
            firstThrew.Reward(2f, 30, throwOutcome: true);
            firstThrew.Flush();

            var secondThrew = Make();            // credited to the decision made at tick 20 (situation b)
            secondThrew.Choose(a, 0); secondThrew.Choose(b, 20); secondThrew.NoteThrow();
            secondThrew.Reward(2f, 30, throwOutcome: true);
            secondThrew.Flush();

            Assert.NotEqual(noReward.Evaluate(a), firstThrew.Evaluate(a));
            Assert.NotEqual((float[])firstThrew.Evaluate(a).Clone(), secondThrew.Evaluate(a));
        }

        [Fact]
        public void RewardsOnlyReachDecisionsInsideTheWindow()
        {
            var policy = new TacticPolicy(1, 5) { FixedEpsilon = 0f, LearningRate = 0.5f, RewardWindowTicks = 80 };
            float[] x = { 1f };
            float[] before = (float[])policy.Evaluate(x).Clone();
            policy.Choose(x, 0);
            policy.Reward(5f, 1000); // far outside the window: the decision expires with zero credit
            float[] after = (float[])policy.Evaluate(x).Clone();
            Assert.NotEqual(before, after);   // trained toward 0, not toward 5

            policy.Choose(x, 1000);
            policy.Reward(5f, 1040); // inside
            policy.Flush();
            float[] trained = policy.Evaluate(x);
            Assert.NotEqual(after, trained);
        }

        [Fact]
        public void ExplorationDecaysWithExperienceButKeepsAFloor()
        {
            var policy = new TacticPolicy(1, 2);
            Assert.Equal(0.3f, policy.Epsilon, 3);
            float[] x = { 1f };
            int tick = 0;
            for (int i = 0; i < 300; i++) { policy.Choose(x, tick); tick += 100; }
            Assert.Equal(0.15f, policy.Epsilon, 2);          // one half-life
            for (int i = 0; i < 1500; i++) { policy.Choose(x, tick); tick += 100; }
            Assert.Equal(0.05f, policy.Epsilon, 3);          // the floor
        }

        [Fact]
        public void ExplorationReopensWhenTheRewardsStopMatchingTheEstimates()
        {
            var policy = new TacticPolicy(1, 4) { LearningRate = 0.05f, EpsilonHalfLife = 50 };
            float[] x = { 1f };
            int tick = 0;
            // A stable world: Wait pays, everything else does not. Long enough to reach the floor.
            for (int i = 0; i < 800; i++)
            {
                Tactic chosen = policy.Choose(x, tick);
                if (chosen == Tactic.Wait) policy.Reward(1f, tick + 10);
                tick += 100;
            }
            policy.Flush();
            float settled = policy.Epsilon;
            Assert.True(settled < 0.08f, "settled at " + settled);

            // The player changes habits: now Wait is punished and Throw pays.
            float peak = settled;
            for (int i = 0; i < 200; i++)
            {
                Tactic chosen = policy.Choose(x, tick);
                if (chosen == Tactic.Wait) policy.Reward(-1f, tick + 10);
                if (chosen == Tactic.Throw) policy.Reward(1f, tick + 10);
                tick += 100;
                peak = System.Math.Max(peak, policy.Epsilon);
            }
            Assert.True(peak > settled + 0.1f, "exploration only reached " + peak);

            // Once it has adapted, exploration settles again and it picks the new best tactic.
            for (int i = 0; i < 1500; i++)
            {
                Tactic chosen = policy.Choose(x, tick);
                if (chosen == Tactic.Wait) policy.Reward(-1f, tick + 10);
                if (chosen == Tactic.Throw) policy.Reward(1f, tick + 10);
                tick += 100;
            }
            policy.Flush();
            Assert.True(policy.Epsilon < settled + 0.03f, "still exploring at " + policy.Epsilon);
            policy.FixedEpsilon = 0f;
            Assert.Equal(Tactic.Throw, policy.Choose(x, tick));
        }

        [Fact]
        public void OneBadOutcomeAtTheFloorDoesNotReopenExploration()
        {
            // A realistic stationary world is noisy: Wait pays only half the time, so the estimates
            // are always somewhat wrong and the baseline surprise is well above zero.
            var policy = new TacticPolicy(1, 8) { LearningRate = 0.05f, EpsilonHalfLife = 50 };
            var world = new System.Random(3);
            float[] x = { 1f };
            int tick = 0;
            for (int i = 0; i < 800; i++)
            {
                Tactic chosen = policy.Choose(x, tick);
                if (chosen == Tactic.Wait && world.Next(2) == 0) policy.Reward(1f, tick + 10);
                tick += 100;
            }
            policy.Flush();
            Assert.True(policy.Epsilon < 0.08f, "settled at " + policy.Epsilon);

            // One hurt lands on the last few decisions, as in a fight (decisions 20 ticks apart, window 80).
            for (int i = 0; i < 4; i++) { policy.Choose(x, tick); tick += 20; }
            policy.Reward(-1f, tick);
            float peak = 0f;
            for (int i = 0; i < 30; i++)
            {
                policy.Choose(x, tick); tick += 100;
                peak = System.Math.Max(peak, policy.Epsilon);
            }
            Assert.True(peak < 0.1f, "one bad outcome pushed exploration to " + peak);
        }

        [Fact]
        public void FilesWithoutSurpriseAveragesStillLoad()
        {
            var policy = new TacticPolicy(2, 6);
            string text = policy.Serialize().Replace("|rs=0|bs=0", "");
            Assert.DoesNotContain("rs=", text);
            Assert.True(TacticPolicy.TryParse(text, 2, 6, out TacticPolicy back), text);
            Assert.Equal(0f, back.BaselineSurprise);
            // the first trained decision seeds both averages instead of leaving the baseline at zero
            float[] x = { 0.5f, 0.5f };
            back.Choose(x, 0);
            back.Reward(1f, 10);
            back.Flush();
            Assert.True(back.BaselineSurprise > 0f);
            Assert.Equal(back.RecentSurprise, back.BaselineSurprise);
        }

        [Fact]
        public void PolicyRoundTripsThroughText()
        {
            var policy = new TacticPolicy(4, 9);
            float[] x = { 0.1f, 0.5f, 0.9f, 0.3f };
            policy.Choose(x, 0);
            policy.Reward(1f, 5);
            policy.Flush();
            float[] before = (float[])policy.Evaluate(x).Clone();

            string text = policy.Serialize();
            Assert.True(TacticPolicy.TryParse(text, 4, 9, out TacticPolicy back), text);
            Assert.Equal(1, back.Decisions);
            Assert.Equal(1, back.Rewards);
            Assert.Equal(policy.RecentSurprise, back.RecentSurprise);
            Assert.Equal(policy.BaselineSurprise, back.BaselineSurprise);
            Assert.Equal(before, back.Evaluate(x));
            Assert.False(TacticPolicy.TryParse(text, 5, 9, out _), "a network with the wrong input count must be rejected");
            Assert.False(TacticPolicy.TryParse("v=0|net=nope", 4, 9, out _));
        }
    }
}
