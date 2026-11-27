using System;
using Steamworks;

namespace ZigguratMP.Net
{
    /// <summary>
    /// Thin wrapper over the legacy ISteamNetworking P2P API (the only one present in the
    /// game's Steamworks.NET v11). Steam handles NAT traversal and relay.
    /// The game itself calls SteamAPI.RunCallbacks(), so callbacks fire without our help.
    /// </summary>
    internal sealed class SteamP2PTransport
    {
        public delegate void PacketHandler(CSteamID from, byte[] data, int length);

        public event PacketHandler OnPacket;

        /// <summary>Decides whether an incoming session request is accepted (lobby members only).</summary>
        public Func<CSteamID, bool> AcceptFilter;

        private Callback<P2PSessionRequest_t> sessionRequestCb;
        private Callback<P2PSessionConnectFail_t> connectFailCb;
        private byte[] recvBuffer = new byte[4096];

        public void Init()
        {
            sessionRequestCb = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            connectFailCb = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
        }

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            CSteamID remote = req.m_steamIDRemote;
            if (AcceptFilter != null && AcceptFilter(remote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(remote);
                Plugin.Log.LogInfo($"P2P session accepted: {remote}");
            }
            else
            {
                Plugin.Log.LogWarning($"P2P session refused (not in lobby): {remote}");
            }
        }

        private void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            Plugin.Log.LogWarning($"P2P connect fail with {fail.m_steamIDRemote}: error {fail.m_eP2PSessionError}");
        }

        public bool Send(CSteamID to, byte[] data, int length, bool reliable)
        {
            EP2PSend mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            return SteamNetworking.SendP2PPacket(to, data, (uint)length, mode, Protocol.Channel);
        }

        public void Poll()
        {
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, Protocol.Channel))
            {
                if (recvBuffer.Length < size)
                    recvBuffer = new byte[size];

                if (!SteamNetworking.ReadP2PPacket(recvBuffer, size, out uint read, out CSteamID from, Protocol.Channel))
                    break;

                try
                {
                    OnPacket?.Invoke(from, recvBuffer, (int)read);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Error handling packet from {from}: {e}");
                }
            }
        }

        public void Close(CSteamID remote) => SteamNetworking.CloseP2PSessionWithUser(remote);
    }
}
