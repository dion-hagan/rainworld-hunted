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
        public float LearningRate = 0.01f;

        /// <summary>Exploration at a fresh file: this share of decisions is random.</summary>
        public float InitialEpsilon = 0.3f;
        /// <summary>Exploration halves every this many decisions...</summary>
        public int EpsilonHalfLife = 300;
        /// <summary>...but never drops below this, so a change in the player's habits can still be noticed.</summary>
        public float MinEpsilon = 0.05f;
        /// <summary>Tests pin exploration here; null means the schedule applies.</summary>
        public float? FixedEpsilon;

        /// <summary>
        /// Exploration right now: a schedule that decays with experience, reopened when recent
        /// outcomes surprise the estimates more than they usually do (the player changed style).
        /// </summary>
        public float Epsilon
        {
            get
            {
                if (FixedEpsilon.HasValue)
                {
                    return FixedEpsilon.Value;
                }
                float scheduled = Math.Max(MinEpsilon, InitialEpsilon * (float)Math.Pow(0.5, (double)Decisions / EpsilonHalfLife));
                // Dead zone: recent surprise must be more than double what is normal for this player
                // before exploration reopens, so one unlucky hit does not, but a changed player does.
                float surprise = Math.Max(0f, (RecentSurprise - BaselineSurprise) / (BaselineSurprise + 0.1f) - 1f);
                float reopened = MinEpsilon + Math.Min(1f, surprise) * (InitialEpsilon - MinEpsilon);
                return Math.Max(scheduled, reopened);
            }
        }

        /// <summary>Average |credit - estimate| over roughly the last ten trained decisions.</summary>
        public float RecentSurprise { get; private set; }
        /// <summary>The same over roughly the last hundred: what "normal" surprise looks like for this player.</summary>
        public float BaselineSurprise { get; private set; }

        public int Features { get; }
        public int Decisions { get; private set; }
        /// <summary>Called with (features, tactic, credit) each time a decision is trained; the arena tool logs a dataset with it. Not part of the game's surface.</summary>
        internal Action<float[], int, float> OnTrained;
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
            public float Predicted;
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
            Evaluate(features);
            int choice;
            if (rng.NextDouble() < Epsilon)
            {
                choice = rng.Next(TacticCount);
            }
            else
            {
                choice = 0;
                for (int i = 1; i < TacticCount; i++)
                {
                    if (scores[i] > scores[choice])
                    {
                        choice = i;
                    }
                }
            }
            pending.Add(new Decision { Features = (float[])features.Clone(), Tactic = choice, Tick = tick, Predicted = scores[choice] });
            if (Decisions < int.MaxValue)
            {
                Decisions++; // saturates: a headless run can make billions of decisions
            }
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
            if (Rewards < int.MaxValue)
            {
                Rewards++;
            }
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
            float surprise = Math.Abs(d.Credit - d.Predicted);
            if (BaselineSurprise == 0f)
            {
                // First trained decision (or a file saved before surprise was tracked): start both
                // averages from real data so the fast one cannot outrun the slow one from zero.
                RecentSurprise = BaselineSurprise = surprise;
            }
            RecentSurprise += (surprise - RecentSurprise) * 0.1f;
            BaselineSurprise += (surprise - BaselineSurprise) * 0.01f;
            net.Train(d.Features, d.Tactic, d.Credit, LearningRate);
            OnTrained?.Invoke(d.Features, d.Tactic, d.Credit);
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
                + "|rs=" + RecentSurprise.ToString("R", CultureInfo.InvariantCulture)
                + "|bs=" + BaselineSurprise.ToString("R", CultureInfo.InvariantCulture)
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
            float total = 0f, recent = 0f, baseline = 0f;
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
                    case "rs": float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out recent); break;
                    case "bs": float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline); break;
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
            policy = new TacticPolicy(net, seed) { Decisions = decisions, Rewards = rewards, TotalReward = total, RecentSurprise = recent, BaselineSurprise = baseline };
            return true;
        }
    }
}
