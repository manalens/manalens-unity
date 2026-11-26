using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// The settings of Project Settings > Manalens, saved in
    /// Assets/Resources/ManalensSettings.asset so that a player build has them.
    /// With a publishable key and AutoInitialize on, the SDK starts by itself
    /// after the first scene loads (after Awake), unless your code called
    /// Manalens.Init() before: your options then win.
    /// </summary>
    public sealed class ManalensSettings : ScriptableObject
    {
        /// <summary>The name under Resources/.</summary>
        public const string ResourceName = "ManalensSettings";

        [Tooltip("The project publishable key (mana_pk_...). It is safe in a game build. Never put a secret key or an API key here.")]
        public string PublishableKey = "";

        [Tooltip("The Manalens ingest URL, without /v0/events.")]
        public string Endpoint = "https://events.manalens.dev";

        [Tooltip("Start the SDK after the first scene loads. Turn it off when your code calls Manalens.Init() later, for example after a consent screen.")]
        public bool AutoInitialize = true;

        [Tooltip("No events and no stored ID until Manalens.SetConsent(true).")]
        public bool ConsentRequired;

        [Tooltip("Send from Play mode in the editor. Set Build (for example dev) to tell test runs apart.")]
        public bool EnabledInEditor = true;

        [Tooltip("Empty: Application.version.")]
        public string AppVersion = "";

        [Tooltip("Your build number or label, for example 571 or dev.")]
        public string Build = "";

        [Tooltip("steam, itch, epic, gog, app_store, play, psn, xbox, nintendo or other. Empty: not sent.")]
        public string Storefront = "";

        [Tooltip("A dedicated server build (UNITY_SERVER, or a player in batch mode) is not a player. Off: it sends no game_open, sessions or unclean exits. Track() still works.")]
        public bool TrackServerBuilds;

        [Tooltip("Log each queued event and each send in the Console.")]
        public bool Debug;

        /// <summary>The options of these settings.</summary>
        public ManalensOptions ToOptions()
        {
            return new ManalensOptions
            {
                PublishableKey = (PublishableKey ?? "").Trim(),
                Endpoint = (Endpoint ?? "").Trim(),
                ConsentRequired = ConsentRequired,
                Enabled = EnabledInEditor || !Application.isEditor,
                AppVersion = string.IsNullOrWhiteSpace(AppVersion) ? null : AppVersion.Trim(),
                Build = (Build ?? "").Trim(),
                Storefront = (Storefront ?? "").Trim(),
                TrackServerBuilds = TrackServerBuilds,
                Debug = Debug,
            };
        }

        /// <summary>The settings asset of the build, or null when the project has none.</summary>
        public static ManalensSettings Load() => Resources.Load<ManalensSettings>(ResourceName);
    }
}
