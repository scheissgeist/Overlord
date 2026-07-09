using System;
using System.Collections.Generic;
using Verse;
using RimWorld;

namespace Overlord
{
    public enum ActionLogKind
    {
        Command,
        CommandFailed,
        Claim,
        Assignment,
        Unassignment,
        Death,
        Moderation,
        System
    }

    public class ActionLogEntry
    {
        public DateTime when;
        public ActionLogKind kind;
        public string username;
        public string action;
        public string message;
        public int? pawnId;
    }

    /// <summary>
    /// Bounded in-memory history of viewer actions. Renders to the in-game Action Log window.
    /// Not save-persisted: history is meaningful for the live session only.
    /// </summary>
    public static class ActionLog
    {
        public const int Capacity = 500;
        private static readonly LinkedList<ActionLogEntry> entries = new LinkedList<ActionLogEntry>();
        private static readonly object syncLock = new object();

        public static int Count
        {
            get { lock (syncLock) return entries.Count; }
        }

        public static void Append(ActionLogKind kind, string username, string action, string message, int? pawnId = null)
        {
            var entry = new ActionLogEntry
            {
                when = DateTime.Now,
                kind = kind,
                username = username,
                action = action,
                message = message,
                pawnId = pawnId
            };
            lock (syncLock)
            {
                entries.AddLast(entry);
                while (entries.Count > Capacity)
                    entries.RemoveFirst();
            }
        }

        public static List<ActionLogEntry> Snapshot()
        {
            lock (syncLock)
                return new List<ActionLogEntry>(entries);
        }

        public static void Clear()
        {
            lock (syncLock)
                entries.Clear();
        }
    }
}
