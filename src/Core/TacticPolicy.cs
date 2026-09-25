using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hunted.Core
{
    /// <summary>What the Pursuer can do with a weapon in hand while it has the player in sight.</summary>
    public enum Tactic
    {
        /// <summary>Take a throwing position and throw as soon as the line is good (the pre-learning behaviour).</summary>
        Throw = 0,
        /// <summary>Give up the current throwing position for a fresh one, without throwing on the way.</summary>
        Reposition = 1,
        /// <summary>Walk straight at the player.</summary>
        CloseIn = 2,
        /// <summary>Hold position and throw only if the player walks into the line.</summary>
        Wait = 3,
    }

    /// <summary>
    /// A contextual bandit over <see cref="Tactic"/>: a <see cref="TinyNet"/> estimates the
    /// reward of each tactic for the current situation, the policy picks the best one most
    /// of the time and a random one otherwise, and every decision is trained exactly once,
    /// when it leaves the reward window, toward the credit it collected in that window
    /// (zero when nothing happened). One-step Q-learning, which is enough for a game where
    /// a throw's consequence is known within a second.
    /// </summary>
    public sealed class TacticPolicy
    {
        public const int TacticCount = 4;
        public const int Version = 1;

        /// <summary>How long after a decision a reward still counts for it, in ticks.</summary>
        public int RewardWindowTicks = 80;
        public float Epsilon = 0.15f;
        public float LearningRate = 0.01f;

        public int Features { get; }
        public int Decisions { get; private set; }
        public int Rewards { get; private set; }
        public float TotalReward { get; private set; }

        private readonly TinyNet net;
        private readonly Random rng;
        private readonly float[] scores = new float[TacticCount];
        private readonly List<Decision> pending = new List<Decision>();
        private Decision lastThrow;

        private sealed class Decision
        {
            public float[] Features;
            public int Tactic;
            public int Tick;
            public float Credit;
        }

        public TacticPolicy(int features, int seed) : this(new TinyNet(features, 16, TacticCount, seed), seed)
        {
        }

        private TacticPolicy(TinyNet net, int seed)
        {
            this.net = net;
            Features = net.Inputs;
            rng = new Random(seed);
        }

        /// <summary>Estimated reward per tactic for these features (a buffer reused between calls).</summary>
        public float[] Evaluate(float[] features)
        {
            return net.Forward(features, scores);
        }

        /// <summary>Picks a tactic for the situation and remembers the decision so later rewards can train it.</summary>
        public Tactic Choose(float[] features, int tick)
        {
            if (features == null || features.Length != Features)
            {
                throw new ArgumentException("Expected " + Features + " features", nameof(features));
            }
            int choice;
            if (rng.NextDouble() < Epsilon)
            {
                choice = rng.Next(TacticCount);
            }
            else
            {
                Evaluate(features);
                choice = 0;
                for (int i = 1; i < TacticCount; i++)
                {
                    if (scores[i] > scores[choice])
                    {
                        choice = i;
                    }
                }
            }
            pending.Add(new Decision { Features = (float[])features.Clone(), Tactic = choice, Tick = tick });
            Decisions++;
            Expire(tick);
            return (Tactic)choice;
        }

        /// <summary>Records that the most recent decision is the one that threw, so throw outcomes go to it alone.</summary>
        public void NoteThrow()
        {
            if (pending.Count > 0)
            {
                lastThrow = pending[pending.Count - 1];
            }
        }

        /// <summary>
        /// Adds <paramref name="value"/> to the credit of every decision made within the reward
        /// window before <paramref name="tick"/>, discounted by age. A throw outcome (a hit, a
        /// spear in a wall) is credited only to the decision that threw.
        /// </summary>
        public void Reward(float value, int tick, bool throwOutcome = false)
        {
            Rewards++;
            TotalReward += value;
            if (throwOutcome)
            {
                if (lastThrow != null && pending.Contains(lastThrow))
                {
                    lastThrow.Credit += value;
                }
            }
            else
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    int age = tick - pending[i].Tick;
                    if (age >= 0 && age <= RewardWindowTicks)
                    {
                        pending[i].Credit += value * (1f - (float)age / RewardWindowTicks);
                    }
                }
            }
            Expire(tick);
        }

        /// <summary>Trains and drops every decision whose window has closed.</summary>
        private void Expire(int tick)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (tick - pending[i].Tick > RewardWindowTicks)
                {
                    Learn(pending[i]);
                    pending.RemoveAt(i);
                }
            }
        }

        /// <summary>Trains every pending decision now, for example before the session ends.</summary>
        public void Flush()
        {
            foreach (Decision d in pending)
            {
                Learn(d);
            }
            pending.Clear();
        }

        private void Learn(Decision d)
        {
            net.Train(d.Features, d.Tactic, d.Credit, LearningRate);
            if (ReferenceEquals(d, lastThrow))
            {
                lastThrow = null;
            }
        }

        /// <summary>One line for the overlay: the four estimates for the last evaluated situation.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < TacticCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(((Tactic)i).ToString()).Append(':').Append(scores[i].ToString("0.00", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public string Serialize()
        {
            return "v=" + Version
                + "|d=" + Decisions.ToString(CultureInfo.InvariantCulture)
                + "|r=" + Rewards.ToString(CultureInfo.InvariantCulture)
                + "|t=" + TotalReward.ToString("R", CultureInfo.InvariantCulture)
                + "|net=" + net.Serialize();
        }

        /// <summary>Parses a saved policy; rejects one whose network does not match <paramref name="features"/> inputs.</summary>
        public static bool TryParse(string text, int features, int seed, out TacticPolicy policy)
        {
            policy = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            TinyNet net = null;
            int decisions = 0, rewards = 0;
            float total = 0f;
            bool versionOk = false;
            foreach (string part in text.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0)
                {
                    return false;
                }
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                switch (key)
                {
                    case "v": versionOk = value == Version.ToString(CultureInfo.InvariantCulture); break;
                    case "d": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out decisions); break;
                    case "r": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rewards); break;
                    case "t": float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out total); break;
                    case "net":
                        if (!TinyNet.TryParse(value, out net))
                        {
                            return false;
                        }
                        break;
                }
            }
            if (!versionOk || net == null || net.Inputs != features || net.Outputs != TacticCount)
            {
                return false;
            }
            policy = new TacticPolicy(net, seed) { Decisions = decisions, Rewards = rewards, TotalReward = total };
            return true;
        }
    }
}
