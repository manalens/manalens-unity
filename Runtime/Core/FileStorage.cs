using System;
using System.IO;

namespace ManalensSdk
{
    /// <summary>
    /// Stores each blob as a file in one folder (Unity: Application.persistentDataPath).
    /// A write goes to a temporary file first and then replaces the old file
    /// with a rename, so a crash during a write keeps the old content.
    /// </summary>
    public sealed class FileStorage : IStorage
    {
        readonly string _folder;
        readonly ILog _log;

        public FileStorage(string folder, ILog log = null)
        {
            _folder = folder;
            _log = log ?? new NullLog();
        }

        string PathOf(string name) => Path.Combine(_folder, name + ".txt");

        public ReadStatus Read(string name, out string content)
        {
            var status = ReadFile(PathOf(name), out content);
            // Only a temporary file: a write stopped before its first rename.
            // Its content may be partial; the SDK checks it (StateFile.TryParse).
            return status == ReadStatus.Missing ? ReadFile(PathOf(name) + ".tmp", out content) : status;
        }

        /// <summary>
        /// Opens the file directly. Only "not found" means Missing: File.Exists
        /// also returns false on a permission error, which must count as Failed.
        /// </summary>
        ReadStatus ReadFile(string path, out string content)
        {
            content = null;
            try
            {
                content = File.ReadAllText(path);
                return ReadStatus.Ok;
            }
            catch (FileNotFoundException)
            {
                return ReadStatus.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return ReadStatus.Missing;
            }
            catch (Exception e)
            {
                _log.Warning("could not read " + path + " (" + e.Message + ")");
                content = null;
                return ReadStatus.Failed;
            }
        }

        public bool Write(string name, string content)
        {
            var path = PathOf(name);
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllText(temporary, content);
                if (!File.Exists(path))
                {
                    File.Move(temporary, path);
                    return true;
                }
                try
                {
                    // rename(2) on Unix, ReplaceFile on Windows: atomic.
                    File.Replace(temporary, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(temporary, path);
                }
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("could not write " + path + " (" + e.Message + ")");
                return false;
            }
        }

        public bool Delete(string name)
        {
            var path = PathOf(name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("could not delete " + path + " (" + e.Message + ")");
                return false;
            }
        }
    }
}
