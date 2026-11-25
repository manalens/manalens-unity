#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// WebGL storage: the browser's localStorage, written at once (WebGL/ManalensStorage.jslib).
    /// PlayerPrefs reach IndexedDB only a moment after Save(), so a reload in that moment
    /// lost the state and the next load made a new anonymous ID (P-2791).
    /// Keys start with the game's persistentDataPath, because games on one origin (for
    /// example html-classic.itch.zone) share one localStorage. A value that SDK 0.1.0
    /// kept in PlayerPrefs moves here on its first read. Without localStorage (some
    /// private modes and sandboxed iframes), the SDK uses PlayerPrefs.
    /// </summary>
    internal sealed class BrowserStorage : IStorage
    {
        [DllImport("__Internal")] static extern int ManalensStorageAvailable();
        [DllImport("__Internal")] static extern string ManalensStorageGet(string key);
        [DllImport("__Internal")] static extern int ManalensStorageSet(string key, string value);
        [DllImport("__Internal")] static extern int ManalensStorageRemove(string key);

        readonly string _prefix;
        readonly PlayerPrefsStorage _legacy = new PlayerPrefsStorage();

        BrowserStorage(string prefix)
        {
            _prefix = prefix;
        }

        public static IStorage Create()
        {
            return ManalensStorageAvailable() == 1
                ? new BrowserStorage("manalens:" + Application.persistentDataPath + ":")
                : (IStorage)new PlayerPrefsStorage();
        }

        public ReadStatus Read(string name, out string content)
        {
            content = null;
            var result = ManalensStorageGet(_prefix + name);
            if (string.IsNullOrEmpty(result) || result[0] == '2') return ReadStatus.Failed;
            if (result[0] == '1')
            {
                content = result.Substring(1);
                return ReadStatus.Ok;
            }
            // Missing here: move a value from PlayerPrefs (SDK 0.1.0) once.
            var legacy = _legacy.Read(name, out var old);
            if (legacy != ReadStatus.Ok) return legacy;
            if (ManalensStorageSet(_prefix + name, old) == 1) _legacy.Delete(name);
            content = old;
            return ReadStatus.Ok;
        }

        public bool Write(string name, string content)
        {
            return ManalensStorageSet(_prefix + name, content) == 1;
        }

        public bool Delete(string name)
        {
            // Also the PlayerPrefs copy, or a later read would bring the old value back.
            var removed = ManalensStorageRemove(_prefix + name) == 1;
            return _legacy.Delete(name) && removed;
        }
    }
}
#endif
