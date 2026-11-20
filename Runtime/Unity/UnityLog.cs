using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// SDK messages in the Unity console. The "Manalens:" prefix also
    /// tells the error capture to skip them (no loop).
    /// </summary>
    internal sealed class UnityLog : ILog
    {
        const string Prefix = "Manalens: ";

        public void Info(string message) => Debug.Log(Prefix + message);
        public void Warning(string message) => Debug.LogWarning(Prefix + message);
        public void Error(string message) => Debug.LogError(Prefix + message);
    }
}
