using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Overlord
{
    /// <summary>
    /// Preserves the PREVIOUS session's Player.log on every launch.
    ///
    /// WHY THIS EXISTS. RimWorld keeps exactly two logs: on launch it rotates
    /// Player.log -> Player-prev.log and starts a fresh Player.log. So the log of a
    /// crashed session survives exactly ONE restart, and is then destroyed forever.
    ///
    /// That cost a real diagnosis. On 2026-08-04 the game crashed while a caravan set
    /// up camp; by the time it was investigated the streamer had restarted twice, so
    /// both surviving logs were from AFTER the crash. Every other source was checked
    /// and was empty too — no Unity crash dump (that folder had been untouched since
    /// July), no HugsLib archive, and the Twitch Toolkit daily log holds only coin
    /// awards. The crash is still unexplained purely because nothing kept the file.
    ///
    /// Archiving Player-prev.log at startup is the right hook: at that moment it is
    /// the just-ended session (the one that may have crashed) AND it is not held open
    /// by the process, so the copy is safe. Doing it at shutdown would miss the exact
    /// case that matters — a hard crash never reaches shutdown.
    ///
    /// Deliberately best-effort: any failure here is swallowed. A diagnostic aid must
    /// never be able to stop the mod loading.
    /// </summary>
    public static class PlayerLogArchiver
    {
        private const string ArchiveDirName = "OverlordLogArchive";
        private const int MaxArchivedLogs = 30;

        public static void ArchivePreviousSessionLog()
        {
            try
            {
                string logDir = ResolveLogDirectory();
                if (logDir == null)
                    return;

                string previous = Path.Combine(logDir, "Player-prev.log");
                if (!File.Exists(previous))
                    return;

                var info = new FileInfo(previous);
                if (info.Length == 0)
                    return;

                string archiveDir = Path.Combine(logDir, ArchiveDirName);
                Directory.CreateDirectory(archiveDir);

                // Name from the SOURCE file's write time, not from now. Launching twice
                // without an intervening session would otherwise archive the same log
                // under two names; this way the second launch sees the file already
                // exists and skips it.
                string stamp = info.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");
                string target = Path.Combine(archiveDir, $"Player_{stamp}.log");
                if (File.Exists(target))
                    return;

                File.Copy(previous, target);
                LogUtil.Log($"Archived previous session log -> {ArchiveDirName}/Player_{stamp}.log ({info.Length / 1024}KB)");

                Prune(archiveDir);
            }
            catch (Exception ex)
            {
                // Never fatal — this is a diagnostic convenience, not a feature.
                LogUtil.Warn("Could not archive previous session log: " + ex.Message);
            }
        }

        private static void Prune(string archiveDir)
        {
            try
            {
                var files = new DirectoryInfo(archiveDir)
                    .GetFiles("Player_*.log")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                for (int i = MaxArchivedLogs; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// The folder Unity writes Player.log into. Application.persistentDataPath
        /// points at the same LocalLow folder on Windows, so derive it from there
        /// rather than hard-coding a user path.
        /// </summary>
        private static string ResolveLogDirectory()
        {
            var candidates = new List<string>();

            try
            {
                string persistent = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(persistent))
                    candidates.Add(persistent);
            }
            catch { }

            try
            {
                // Fallback for any platform layout where persistentDataPath is not the
                // log folder: LocalLow/<company>/<product>.
                string localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
                candidates.Add(localLow);
            }
            catch { }

            foreach (string dir in candidates)
            {
                try
                {
                    if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Player.log")))
                        return dir;
                }
                catch { }
            }

            return null;
        }
    }
}
