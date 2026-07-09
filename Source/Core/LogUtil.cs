using Verse;

namespace Overlord
{
    public static class LogUtil
    {
        private const string Tag = "[Overlord]";

        public static void Log(string message)
        {
            Verse.Log.Message($"{Tag} {message}");
        }

        public static void Warn(string message)
        {
            Verse.Log.Warning($"{Tag} {message}");
        }

        public static void Error(string message)
        {
            Verse.Log.Error($"{Tag} {message}");
        }
    }
}
