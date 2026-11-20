using System;
using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// WebGL storage: PlayerPrefs, which Unity keeps in the browser's IndexedDB.
    /// Files in Application.persistentDataPath need a manual sync on WebGL.
    /// A PlayerPrefs value is replaced as a whole, so a write is atomic.
    /// </summary>
    internal sealed class PlayerPrefsStorage : IStorage
    {
        public ReadStatus Read(string name, out string content)
        {
            content = null;
            try
            {
                if (!PlayerPrefs.HasKey(name)) return ReadStatus.Missing;
                content = PlayerPrefs.GetString(name);
                return ReadStatus.Ok;
            }
            catch (Exception)
            {
                return ReadStatus.Failed;
            }
        }

        public bool Write(string name, string content)
        {
            try
            {
                PlayerPrefs.SetString(name, content);
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception)
            {
                // PlayerPrefsException: the WebGL quota (1 MB) is full.
                return false;
            }
        }

        public bool Delete(string name)
        {
            try
            {
                PlayerPrefs.DeleteKey(name);
                PlayerPrefs.Save();
                return !PlayerPrefs.HasKey(name);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
