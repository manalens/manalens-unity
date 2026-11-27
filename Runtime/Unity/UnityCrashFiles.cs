using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// The files where a Unity desktop player keeps the crash data of the last
    /// run (sdks/SPEC.md "Crash stacks"). Windows: the newest crash folder in
    /// %TEMP%\&lt;company&gt;\&lt;product&gt;\Crashes (error.log), then the
    /// previous Player log. macOS and Linux: Player-prev.log next to
    /// Application.consoleLogPath (Unity keeps the log of the last run there).
    /// The paths are read on the main thread when the SDK starts.
    /// </summary>
    sealed class UnityCrashFiles : ICrashFiles
    {
        readonly string _crashFolder;
        readonly string _previousLog;

        UnityCrashFiles(string crashFolder, string previousLog)
        {
            _crashFolder = crashFolder;
            _previousLog = previousLog;
        }

        /// <summary>Null in the editor and off the desktop.</summary>
        public static UnityCrashFiles ForThisPlayer()
        {
#if UNITY_EDITOR
            return null;
#else
            var platform = Application.platform;
            if (platform != RuntimePlatform.WindowsPlayer && platform != RuntimePlatform.OSXPlayer && platform != RuntimePlatform.LinuxPlayer) return null;
            string previousLog = null, crashFolder = null;
            try
            {
                var log = Application.consoleLogPath;
                if (!string.IsNullOrEmpty(log)) previousLog = Path.Combine(Path.GetDirectoryName(log) ?? "", "Player-prev.log");
                if (platform == RuntimePlatform.WindowsPlayer)
                    crashFolder = Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes");
            }
            catch (Exception)
            {
                return null;
            }
            return new UnityCrashFiles(crashFolder, previousLog);
#endif
        }

        public IEnumerable<CrashFile> Candidates()
        {
            var found = new List<CrashFile>();
            if (_crashFolder != null && Directory.Exists(_crashFolder))
            {
                var folders = new List<DirectoryInfo>(new DirectoryInfo(_crashFolder).GetDirectories());
                folders.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var folder in folders)
                {
                    var error = Path.Combine(folder.FullName, "error.log");
                    if (File.Exists(error)) { found.Add(Make(error, "unity_crash_folder")); break; }
                }
            }
            if (_previousLog != null && File.Exists(_previousLog)) found.Add(Make(_previousLog, "unity_player_log"));
            return found;
        }

        static CrashFile Make(string path, string source) => new CrashFile
        {
            Path = path,
            ModifiedUnixSeconds = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() / 1000.0,
            Source = source,
            ReadTail = max => ReadTail(path, max),
        };

        /// <summary>The last <c>max</c> bytes of the file as UTF-8; never the whole of a large log.</summary>
        static string ReadTail(string path, int max)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = stream.Length;
                var start = Math.Max(0, length - max);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[length - start];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
    }
}
