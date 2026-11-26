namespace ManalensSdk
{
    public static class Platforms
    {
        /// <summary>
        /// Maps the name of a UnityEngine.RuntimePlatform value
        /// (Application.platform.ToString()) to the collector platform:
        /// windows, macos, linux, android, ios, web, ps5, xbox_series,
        /// xbox_one, switch, switch2 or other.
        /// </summary>
        public static string FromRuntimePlatform(string runtimePlatform)
        {
            switch (runtimePlatform)
            {
                case "WindowsPlayer":
                case "WindowsEditor":
                case "WindowsServer":
                    return "windows";
                case "OSXPlayer":
                case "OSXEditor":
                case "OSXServer":
                    return "macos";
                case "LinuxPlayer":
                case "LinuxEditor":
                case "LinuxServer":
                    return "linux";
                case "Android":
                    return "android";
                case "IPhonePlayer":
                    return "ios";
                case "WebGLPlayer":
                    return "web";
                case "PS5":
                    return "ps5";
                case "GameCoreXboxSeries":
                    return "xbox_series";
                case "GameCoreXboxOne":
                case "XboxOne": // The old XDK player.
                    return "xbox_one";
                case "Switch":
                    return "switch";
                // Nintendo Switch 2: the public RuntimePlatform enum has no
                // value for it that we could check. A Switch 2 build sets
                // ManalensOptions.Platform = "switch2" itself. The name below
                // is matched in case the console SDK adds it; it costs nothing
                // when it does not exist.
                case "Switch2":
                    return "switch2";
                default:
                    // tvOS, visionOS, PS4, UWP (WSAPlayer*) and others.
                    return "other";
            }
        }
    }
}
