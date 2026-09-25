using System;
using System.Globalization;

namespace Hunted.Core
{
    /// <summary>
    /// A one-hidden-layer perceptron small enough to run in a mod: no dependencies,
    /// a few hundred multiply-adds per evaluation, plain-text persistence.
    /// Outputs are raw scores; the caller decides what they mean.
    /// </summary>
    public sealed class TinyNet
    {
        public int Inputs { get; }
        public int Hidden { get; }
        public int Outputs { get; }

        private readonly float[] w1; // Inputs x Hidden
        private readonly float[] b1;
        private readonly float[] w2; // Hidden x Outputs
        private readonly float[] b2;
        private readonly float[] hid;

        public TinyNet(int inputs, int hidden, int outputs, int seed)
        {
            if (inputs < 1 || hidden < 1 || outputs < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), "Layer sizes must be positive");
            }
            Inputs = inputs;
            Hidden = hidden;
            Outputs = outputs;
            w1 = new float[inputs * hidden];
            b1 = new float[hidden];
            w2 = new float[hidden * outputs];
            b2 = new float[outputs];
            hid = new float[hidden];
            var rng = new Random(seed);
            float s1 = 1f / (float)Math.Sqrt(inputs);
            float s2 = 1f / (float)Math.Sqrt(hidden);
            for (int i = 0; i < w1.Length; i++)
            {
                w1[i] = (float)(rng.NextDouble() * 2 - 1) * s1;
            }
            for (int i = 0; i < w2.Length; i++)
            {
                w2[i] = (float)(rng.NextDouble() * 2 - 1) * s2;
            }
        }

        /// <summary>Evaluates the network into <paramref name="output"/> (length Outputs) and returns it.</summary>
        public float[] Forward(float[] input, float[] output)
        {
            Check(input, output);
            for (int h = 0; h < Hidden; h++)
            {
                float s = b1[h];
                for (int i = 0; i < Inputs; i++)
                {
                    s += w1[i * Hidden + h] * input[i];
                }
                hid[h] = (float)Math.Tanh(s);
            }
            for (int o = 0; o < Outputs; o++)
            {
                float s = b2[o];
                for (int h = 0; h < Hidden; h++)
                {
                    s += w2[h * Outputs + o] * hid[h];
                }
                output[o] = s;
            }
            return output;
        }

        /// <summary>
        /// One gradient step moving output <paramref name="index"/> toward <paramref name="target"/>
        /// (squared error) for this input. Only that output's weights and the shared hidden layer move.
        /// </summary>
        public void Train(float[] input, int index, float target, float learningRate)
        {
            if (index < 0 || index >= Outputs)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            var output = new float[Outputs];
            Forward(input, output);
            float err = output[index] - target;
            for (int h = 0; h < Hidden; h++)
            {
                float gHid = err * w2[h * Outputs + index] * (1f - hid[h] * hid[h]);
                w2[h * Outputs + index] -= learningRate * err * hid[h];
                b1[h] -= learningRate * gHid;
                for (int i = 0; i < Inputs; i++)
                {
                    w1[i * Hidden + h] -= learningRate * gHid * input[i];
                }
            }
            b2[index] -= learningRate * err;
        }

        /// <summary>Layer sizes then every weight, comma separated, culture invariant. No '&lt;', '&gt;' or '|'.</summary>
        public string Serialize()
        {
            var parts = new string[4 + w1.Length + b1.Length + w2.Length + b2.Length];
            int k = 0;
            parts[k++] = Inputs.ToString(CultureInfo.InvariantCulture);
            parts[k++] = Hidden.ToString(CultureInfo.InvariantCulture);
            parts[k++] = Outputs.ToString(CultureInfo.InvariantCulture);
            parts[k++] = "1"; // format version
            foreach (float[] layer in new[] { w1, b1, w2, b2 })
            {
                for (int i = 0; i < layer.Length; i++)
                {
                    parts[k++] = layer[i].ToString("R", CultureInfo.InvariantCulture);
                }
            }
            return string.Join(",", parts);
        }

        public static bool TryParse(string text, out TinyNet net)
        {
            net = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string[] parts = text.Split(',');
            if (parts.Length < 4
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int inputs)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hidden)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int outputs)
                || parts[3] != "1"
                || inputs < 1 || hidden < 1 || outputs < 1)
            {
                return false;
            }
            var result = new TinyNet(inputs, hidden, outputs, 0);
            int expected = 4 + result.w1.Length + result.b1.Length + result.w2.Length + result.b2.Length;
            if (parts.Length != expected)
            {
                return false;
            }
            int k = 4;
            foreach (float[] layer in new[] { result.w1, result.b1, result.w2, result.b2 })
            {
                for (int i = 0; i < layer.Length; i++)
                {
                    if (!float.TryParse(parts[k++], NumberStyles.Float, CultureInfo.InvariantCulture, out layer[i]) || float.IsNaN(layer[i]) || float.IsInfinity(layer[i]))
                    {
                        return false;
                    }
                }
            }
            net = result;
            return true;
        }

        private void Check(float[] input, float[] output)
        {
            if (input == null || input.Length != Inputs)
            {
                throw new ArgumentException("Expected " + Inputs + " inputs", nameof(input));
            }
            if (output == null || output.Length != Outputs)
            {
                throw new ArgumentException("Expected " + Outputs + " outputs", nameof(output));
            }
        }
    }
}
