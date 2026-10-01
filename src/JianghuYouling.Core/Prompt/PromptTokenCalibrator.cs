using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// Session-local calibration between the deterministic estimator and each provider
    /// model's reported prompt usage. It never stores credentials or message contents.
    /// </summary>
    public static class PromptTokenCalibrator
    {
        private sealed class State
        {
            public double Factor = 1;
            public int Samples;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, State> States =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        public static void Observe(string identity, int estimatedTokens, int actualTokens)
        {
            if (string.IsNullOrWhiteSpace(identity) || estimatedTokens < 256
                || actualTokens < 256) return;
            double sample = Clamp((double)actualTokens / estimatedTokens, 0.55, 2.5);
            lock (Gate)
            {
                if (!States.TryGetValue(identity, out State state))
                    States[identity] = state = new State();
                state.Factor = state.Samples == 0 ? sample : state.Factor * 0.8 + sample * 0.2;
                state.Samples++;
            }
        }

        public static int CalibratedBudget(string identity, int rawBudget)
        {
            if (rawBudget <= 0 || string.IsNullOrWhiteSpace(identity)) return rawBudget;
            double factor;
            int samples;
            lock (Gate)
            {
                if (!States.TryGetValue(identity, out State state)) return rawBudget;
                factor = state.Factor;
                samples = state.Samples;
            }
            if (samples < 2) return rawBudget;
            // Do not expand context aggressively when an endpoint under-reports usage.
            factor = Clamp(factor, 0.85, 1.75);
            return Math.Max(1024, (int)Math.Floor(rawBudget / factor));
        }

        public static double Factor(string identity)
        {
            lock (Gate)
                return !string.IsNullOrWhiteSpace(identity)
                    && States.TryGetValue(identity, out State state) ? state.Factor : 1;
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;
    }
}
