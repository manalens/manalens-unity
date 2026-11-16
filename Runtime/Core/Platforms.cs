namespace UntitledAnalyticsSdk
{
    public static class Platforms
    {
        /// <summary>
        /// Maps the name of a UnityEngine.RuntimePlatform value
        /// (Application.platform.ToString()) to the collector platform:
        /// windows, macos, linux, android, ios, web or other.
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
                default:
                    // tvOS, visionOS, consoles, UWP (WSAPlayer*) and others.
                    return "other";
            }
        }
    }
}
