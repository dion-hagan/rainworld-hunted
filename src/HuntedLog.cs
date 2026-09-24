using System;
using BepInEx.Logging;

namespace Hunted
{
    internal static class HuntedLog
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("Hunted");

        public static void Info(string message) => Log.LogInfo(message);
        public static void Warn(string message) => Log.LogWarning(message);
        public static void Error(string message) => Log.LogError(message);
        public static void Error(string message, Exception e) => Log.LogError(message + Environment.NewLine + e);
    }
}
