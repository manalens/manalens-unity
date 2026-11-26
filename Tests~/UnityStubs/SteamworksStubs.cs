// Steamworks.NET stand-ins for the compile check of Runtime/Unity/SteamLink.cs
// with MANALENS_STEAMWORKS_NET. The names and types follow Steamworks.NET
// (com.rlabrecque.steamworks.net): SteamUser.GetAuthTicketForWebApi,
// Callback<GetTicketForWebApiResponse_t> and HAuthTicket.
namespace Steamworks
{
    public enum EResult { k_EResultOK = 1, k_EResultFail = 2 }

    public struct HAuthTicket : System.IEquatable<HAuthTicket>
    {
        public static readonly HAuthTicket Invalid = new HAuthTicket(0);
        public uint m_HAuthTicket;
        public HAuthTicket(uint value) { m_HAuthTicket = value; }
        public bool Equals(HAuthTicket other) => m_HAuthTicket == other.m_HAuthTicket;
        public override bool Equals(object other) => other is HAuthTicket ticket && Equals(ticket);
        public override int GetHashCode() => m_HAuthTicket.GetHashCode();
        public static bool operator ==(HAuthTicket x, HAuthTicket y) => x.m_HAuthTicket == y.m_HAuthTicket;
        public static bool operator !=(HAuthTicket x, HAuthTicket y) => !(x == y);
    }

    public struct GetTicketForWebApiResponse_t
    {
        public HAuthTicket m_hAuthTicket;
        public EResult m_eResult;
        public int m_cubTicket;
        public byte[] m_rgubTicket;
    }

    public static class SteamAPI
    {
        public static bool IsSteamRunning() => false;
    }

    public static class SteamUser
    {
        public static HAuthTicket GetAuthTicketForWebApi(string pchIdentity) => HAuthTicket.Invalid;
    }

    public sealed class Callback<T>
    {
        public delegate void DispatchDelegate(T param);
        public static Callback<T> Create(DispatchDelegate func) => new Callback<T>();
    }
}
