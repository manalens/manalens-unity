using UnityEngine;
#if MANALENS_STEAMWORKS_NET
using Steamworks;
#endif

namespace ManalensSdk
{
    /// <summary>
    /// The Steamworks.NET helper of Manalens.LinkSteam() (P-2815). It compiles
    /// only with the scripting define MANALENS_STEAMWORKS_NET: the Manalens
    /// assembly sets it when the Steamworks.NET package
    /// (com.rlabrecque.steamworks.net) is installed; add it in Player Settings
    /// for a Steamworks.NET copy in Assets. Without it, the SDK has no Steam
    /// dependency and Manalens.LinkSteam() with no ticket does nothing. The
    /// game must start Steam (SteamAPI.Init) itself.
    /// </summary>
    internal static class SteamLink
    {
        /// <summary>Tries for the SteamLink option: about one minute at one try per 10 seconds.</summary>
        const int AutoTries = 6;
        const float AutoIntervalSeconds = 10f;
        static int _triesLeft = -1;
        static float _nextTryAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _triesLeft = -1;
            _nextTryAt = 0;
#if MANALENS_STEAMWORKS_NET
            _callback = null;
            _client = null;
            _ticket = HAuthTicket.Invalid;
#endif
        }

        /// <summary>true when this build has the Steamworks.NET helper.</summary>
        internal static bool Available
        {
            get
            {
#if MANALENS_STEAMWORKS_NET
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The SteamLink option: called every frame by the driver; asks for one ticket per launch, after consent.</summary>
        internal static void Tick(AnalyticsClient client)
        {
            if (!Available || client == null || !client.Options.SteamLink || !client.IsActive) return;
            if (_triesLeft < 0) _triesLeft = AutoTries;
            if (_triesLeft == 0 || Time.realtimeSinceStartup < _nextTryAt) return;
            _triesLeft--;
            _nextTryAt = Time.realtimeSinceStartup + AutoIntervalSeconds;
            if (Request(client)) _triesLeft = 0;
        }

        /// <summary>Stops the SteamLink option for this launch (the game called LinkSteam itself).</summary>
        internal static void StopAuto() => _triesLeft = 0;

#if MANALENS_STEAMWORKS_NET
        static Callback<GetTicketForWebApiResponse_t> _callback;
        static AnalyticsClient _client;
        static HAuthTicket _ticket = HAuthTicket.Invalid;

        /// <summary>Asks Steam for a Web API ticket with the identity "manalens". false when Steam is not running.</summary>
        internal static bool Request(AnalyticsClient client)
        {
            if (_ticket != HAuthTicket.Invalid) return true; // A ticket is on its way.
            try
            {
                if (!SteamAPI.IsSteamRunning()) return false;
                _client = client;
                if (_callback == null) _callback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
                _ticket = SteamUser.GetAuthTicketForWebApi(AnalyticsClient.SteamTicketIdentity);
                return _ticket != HAuthTicket.Invalid;
            }
            catch (System.Exception)
            {
                // Steam is not initialized (SteamAPI.Init not called yet): try again later.
                _ticket = HAuthTicket.Invalid;
                return false;
            }
        }

        static void OnTicket(GetTicketForWebApiResponse_t response)
        {
            if (response.m_hAuthTicket != _ticket) return; // A ticket that the game asked for itself.
            _ticket = HAuthTicket.Invalid;
            if (response.m_eResult != EResult.k_EResultOK || response.m_cubTicket <= 0 || response.m_rgubTicket == null)
            {
                Debug.LogWarning("Manalens: Steam gave no Web API ticket (" + response.m_eResult + ")");
                return;
            }
            var bytes = new byte[System.Math.Min(response.m_cubTicket, response.m_rgubTicket.Length)];
            System.Array.Copy(response.m_rgubTicket, bytes, bytes.Length);
            _client?.LinkSteam(bytes);
        }
#else
        internal static bool Request(AnalyticsClient client) => false;
#endif
    }
}
