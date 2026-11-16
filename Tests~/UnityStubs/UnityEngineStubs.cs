// Compile-only stand-ins for the UnityEngine API that Runtime/Unity uses, with
// the same names and signatures as Unity 2021.3. They let `dotnet build` catch
// C# errors in the Unity layer on a machine without the Unity Editor. They do
// nothing at run time and prove nothing about Unity behavior.
using System;
using System.Collections;

namespace UnityEngine
{
    public enum RuntimePlatform { OSXEditor = 0, OSXPlayer = 1, WindowsPlayer = 2, WindowsEditor = 7, IPhonePlayer = 8, Android = 11, LinuxPlayer = 13, LinuxEditor = 16, WebGLPlayer = 17 }
    public enum SystemLanguage { Arabic, Chinese, ChineseSimplified, ChineseTraditional, Czech, Danish, Dutch, English, Finnish, French, German, Greek, Hebrew, Hungarian, Indonesian, Italian, Japanese, Korean, Norwegian, Polish, Portuguese, Romanian, Russian, Spanish, Swedish, Thai, Turkish, Ukrainian, Vietnamese, Unknown }
    public enum LogType { Error = 0, Assert = 1, Warning = 2, Log = 3, Exception = 4 }
    public enum RuntimeInitializeLoadType { AfterSceneLoad = 0, BeforeSceneLoad = 1, AfterAssembliesLoaded = 2, BeforeSplashScreen = 3, SubsystemRegistration = 4 }
    [Flags] public enum HideFlags { None = 0, HideInHierarchy = 1, HideAndDontSave = 61 }

    public static class Application
    {
        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static event LogCallback logMessageReceivedThreaded;
        public static event Func<bool> wantsToQuit;
        public static event Action quitting;
        public static RuntimePlatform platform => RuntimePlatform.WindowsPlayer;
        public static string version => "1.0";
        public static string persistentDataPath => "";
        public static bool isEditor => false;
        public static SystemLanguage systemLanguage => SystemLanguage.English;
        public static void Quit() { }
        public static void OpenURL(string url) { }
        internal static void Touch() { logMessageReceivedThreaded?.Invoke("", "", LogType.Log); wantsToQuit?.Invoke(); quitting?.Invoke(); }
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class PlayerPrefs
    {
        public static bool HasKey(string key) => false;
        public static string GetString(string key) => "";
        public static void SetString(string key, string value) { }
        public static void DeleteKey(string key) { }
        public static void Save() { }
    }

    public static class Time
    {
        public static float realtimeSinceStartup => 0f;
    }

    public class Object
    {
        public HideFlags hideFlags { get; set; }
        public static void DontDestroyOnLoad(Object target) { }
    }

    public class Component : Object { }
    public class Behaviour : Component { }
    public class Coroutine { }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null;
    }

    public sealed class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : Component, new() => new T();
    }

    public class AsyncOperation
    {
        public event Action<AsyncOperation> completed;
        internal void Touch() => completed?.Invoke(this);
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenu : Attribute
    {
        public AddComponentMenu(string menuName) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }
}

namespace UnityEngine.Scripting
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class PreserveAttribute : Attribute { }
}

namespace UnityEngine.Networking
{
    public class UploadHandler : IDisposable { public void Dispose() { } }
    public sealed class UploadHandlerRaw : UploadHandler { public UploadHandlerRaw(byte[] data) { } }
    public class DownloadHandler : IDisposable { public void Dispose() { } }
    public sealed class DownloadHandlerBuffer : DownloadHandler { }
    public sealed class UnityWebRequestAsyncOperation : AsyncOperation { }

    public class UnityWebRequest : IDisposable
    {
        public const string kHttpVerbPOST = "POST";
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }
        public UnityWebRequest(string url, string method) { }
        public UploadHandler uploadHandler { get; set; }
        public DownloadHandler downloadHandler { get; set; }
        public int timeout { get; set; }
        public Result result => Result.Success;
        public long responseCode => 0;
        public void SetRequestHeader(string name, string value) { }
        public UnityWebRequestAsyncOperation SendWebRequest() => new UnityWebRequestAsyncOperation();
        public void Dispose() { }
    }
}
