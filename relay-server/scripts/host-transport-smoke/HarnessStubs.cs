using System;

namespace Overlord
{
    // The smoke compiles the production transport and serializer without loading
    // RimWorld. These are the only two game-owned types those files need.
    public sealed class ViewerSession
    {
        public string preferredWeaponDef;
    }

    public static class LogUtil
    {
        public static void Log(string message) => Console.WriteLine("[host] " + message);
        public static void Warn(string message) => Console.Error.WriteLine("[host] WARN " + message);
        public static void Error(string message) => Console.Error.WriteLine("[host] ERROR " + message);
    }
}
