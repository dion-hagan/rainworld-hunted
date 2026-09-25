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
        public void PolicyPrefersTheRewardedTacticInThatSituation()
        {
            var policy = new TacticPolicy(2, 3) { Epsilon = 0f, LearningRate = 0.05f };
            float[] near = { 1f, 0f };
            float[] far = { 0f, 1f };
            int tick = 0;
            for (int i = 0; i < 300; i++)
            {
                // near the player, closing in gets punished and waiting pays off
                Tactic chosen = policy.Choose(near, tick);
                policy.Reward(chosen == Tactic.Wait ? 1f : -1f, tick + 10);
                tick += 100;
                // far away, throwing pays off
                chosen = policy.Choose(far, tick);
                policy.Reward(chosen == Tactic.Throw ? 1f : -1f, tick + 10);
                tick += 100;
                // an unrewarded tactic must still be explored: rotate through them while learning
                if (i < 40)
                {
                    policy.Epsilon = 1f;
                }
                else
                {
                    policy.Epsilon = 0f;
                }
            }
            policy.Epsilon = 0f;
            Assert.Equal(Tactic.Wait, policy.Choose(near, tick));
            Assert.Equal(Tactic.Throw, policy.Choose(far, tick + 100));
        }

        [Fact]
        public void RewardsOnlyReachDecisionsInsideTheWindow()
        {
            var policy = new TacticPolicy(1, 5) { Epsilon = 0f, LearningRate = 0.5f, RewardWindowTicks = 80 };
            float[] x = { 1f };
            float[] before = (float[])policy.Evaluate(x).Clone();
            policy.Choose(x, 0);
            policy.Reward(5f, 1000); // far outside the window
            float[] after = (float[])policy.Evaluate(x).Clone();
            Assert.Equal(before, after);

            policy.Choose(x, 1000);
            policy.Reward(5f, 1040); // inside
            float[] trained = policy.Evaluate(x);
            Assert.NotEqual(after, trained);
        }

        [Fact]
        public void PolicyRoundTripsThroughText()
        {
            var policy = new TacticPolicy(4, 9);
            float[] x = { 0.1f, 0.5f, 0.9f, 0.3f };
            policy.Choose(x, 0);
            policy.Reward(1f, 5);
            float[] before = (float[])policy.Evaluate(x).Clone();

            string text = policy.Serialize();
            Assert.True(TacticPolicy.TryParse(text, 9, out TacticPolicy back), text);
            Assert.Equal(1, back.Decisions);
            Assert.Equal(1, back.Rewards);
            Assert.Equal(before, back.Evaluate(x));
            Assert.False(TacticPolicy.TryParse("v=0|net=nope", 9, out _));
        }
    }
}
