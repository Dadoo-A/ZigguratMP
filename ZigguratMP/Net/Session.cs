using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using ZigguratMP.Sync;

namespace ZigguratMP.Net
{
    /// <summary>
    /// Owns the Steam lobby and the P2P mesh. Lobby owner = host (authoritative for run seed,
    /// and later for enemies/loot). Every member sends its own player state to every other member.
    /// </summary>
    internal static class Session
    {
        public static bool SteamReady { get; private set; }
        public static bool InLobby => LobbyId.IsValid() && LobbyId != CSteamID.Nil;
        public static CSteamID LobbyId { get; private set; } = CSteamID.Nil;
        public static CSteamID LocalId { get; private set; }
        public static CSteamID HostId => InLobby ? SteamMatchmaking.GetLobbyOwner(LobbyId) : CSteamID.Nil;
        public static bool IsHost => InLobby && HostId == LocalId;
        public static bool IsClient => InLobby && !IsHost;

        public static string Status { get; private set; } = "Waiting for Steam...";
        public static readonly List<CSteamID> Members = new List<CSteamID>();

        private static readonly SteamP2PTransport transport = new SteamP2PTransport();
        private static readonly PacketWriter writer = new PacketWriter();
        private static readonly HashSet<CSteamID> welcomed = new HashSet<CSteamID>();

        private static CallResult<LobbyCreated_t> lobbyCreatedCr;
        private static CallResult<LobbyEnter_t> lobbyEnterCr;
        private static Callback<LobbyEnter_t> lobbyEnterCb;
        private static Callback<GameLobbyJoinRequested_t> joinRequestedCb;
        private static Callback<LobbyChatUpdate_t> chatUpdateCb;

        private static float steamRetryTimer;
        private static float sendTimer;
        private static ulong launchLobbyArg;

        public static void Init()
        {
            transport.OnPacket += HandlePacket;
            transport.AcceptFilter = id => InLobby && Members.Contains(id);
            launchLobbyArg = ParseConnectLobbyArg();
        }

        /// <summary>Steam invite while game was closed: Steam launches us with "+connect_lobby &lt;id&gt;".</summary>
        private static ulong ParseConnectLobbyArg()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                    return id;
            return 0;
        }

        private static bool TryInitSteam()
        {
            try
            {
                LocalId = SteamUser.GetSteamID(); // throws until the game has called SteamAPI.Init()
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            if (!LocalId.IsValid()) return false;

            transport.Init();
            lobbyCreatedCr = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEnterCr = CallResult<LobbyEnter_t>.Create(OnLobbyEnteredResult);
            lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            joinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            chatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);

            SteamReady = true;
            Status = $"Steam ready as {SteamFriends.GetPersonaName()}";
            Plugin.Log.LogInfo(Status);

            if (launchLobbyArg != 0)
                Join(new CSteamID(launchLobbyArg));
            return true;
        }

        public static void Tick(float dt)
        {
            if (!SteamReady)
            {
                steamRetryTimer -= dt;
                if (steamRetryTimer <= 0f)
                {
                    steamRetryTimer = 1f;
                    TryInitSteam();
                }
                return;
            }

            if (!InLobby) return;

            transport.Poll();

            sendTimer -= dt;
            if (sendTimer <= 0f)
            {
                sendTimer = 1f / Mathf.Max(1, Plugin.SendRateHz.Value);
                SendLocalPlayerState();
            }

            RemotePlayers.Tick(dt);
            RunSync.Tick();
            NetEnemies.Tick(dt);
            ShotSync.Tick(dt);
            RoomSync.Tick();
            BossWatch.Tick();
            DownedSync.Tick(dt);
            ObjectiveSync.Tick(dt);
            PortalSync.Tick(dt);
            ReadyRoom.Tick();
            UI.AllyHud.Tick(dt);
        }

        // ---------------- Lobby lifecycle ----------------

        public static void Host()
        {
            if (!SteamReady || InLobby) return;
            Status = "Creating lobby...";
            lobbyCreatedCr.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 4));
        }

        public static void Join(CSteamID lobby)
        {
            if (!SteamReady) return;
            if (InLobby) Leave();
            Status = $"Joining lobby {lobby}...";
            lobbyEnterCr.Set(SteamMatchmaking.JoinLobby(lobby));
        }

        public static void InviteFriends()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public static void Leave()
        {
            if (!InLobby) return;
            foreach (CSteamID m in Members)
                if (m != LocalId) transport.Close(m);
            SteamMatchmaking.LeaveLobby(LobbyId);
            LobbyId = CSteamID.Nil;
            Members.Clear();
            welcomed.Clear();
            RunSync.Reset();
            FightSync.Reset();
            NetEnemies.Reset();
            ShotSync.Reset();
            RoomSync.Reset();
            DownedSync.Reset();
            PortalSync.Reset();
            GatherSync.Reset();
            ReadyRoom.Reset();
            UI.AllyHud.Reset();
            RemotePlayers.Clear();
            Status = "Left lobby";
        }

        public static void Shutdown()
        {
            if (SteamReady) Leave();
        }

        private static void OnLobbyCreated(LobbyCreated_t res, bool ioFailure)
        {
            if (ioFailure || res.m_eResult != EResult.k_EResultOK)
            {
                Status = $"Lobby creation failed: {res.m_eResult}";
                return;
            }
            var lobby = new CSteamID(res.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, Protocol.LobbyKeyMod, Plugin.Guid);
            SteamMatchmaking.SetLobbyData(lobby, Protocol.LobbyKeyVersion, Protocol.Version.ToString());
            // LobbyEnter_t callback follows and sets LobbyId.
        }

        private static void OnLobbyEnteredResult(LobbyEnter_t res, bool ioFailure)
        {
            if (ioFailure) Status = "Join failed (IO failure)";
            // Success path handled by the global LobbyEnter_t callback below.
        }

        private static void OnLobbyEntered(LobbyEnter_t res)
        {
            if (res.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Status = $"Join refused: {(EChatRoomEnterResponse)res.m_EChatRoomEnterResponse}";
                return;
            }

            var lobby = new CSteamID(res.m_ulSteamIDLobby);
            string ver = SteamMatchmaking.GetLobbyData(lobby, Protocol.LobbyKeyVersion);
            if (ver != Protocol.Version.ToString())
            {
                Status = $"Protocol mismatch (lobby {ver}, mod {Protocol.Version}). Update the mod.";
                SteamMatchmaking.LeaveLobby(lobby);
                return;
            }

            LobbyId = lobby;
            RefreshMembers();
            Status = IsHost ? "Hosting lobby" : $"Joined {SteamFriends.GetFriendPersonaName(HostId)}'s lobby";
            Plugin.Log.LogInfo(Status);

            if (IsClient)
            {
                BinaryWriter w = writer.Begin(MsgType.Hello);
                w.Write(Protocol.Version);
                SendTo(HostId, true);
            }
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t req)
        {
            Plugin.Log.LogInfo($"Steam invite accepted from {SteamFriends.GetFriendPersonaName(req.m_steamIDFriend)}");
            Join(req.m_steamIDLobby);
        }

        private static void OnLobbyChatUpdate(LobbyChatUpdate_t upd)
        {
            if (upd.m_ulSteamIDLobby != LobbyId.m_SteamID) return;
            var who = new CSteamID(upd.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)upd.m_rgfChatMemberStateChange;
            RefreshMembers();

            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                Plugin.Log.LogInfo($"{SteamFriends.GetFriendPersonaName(who)} joined");
            }
            else
            {
                Plugin.Log.LogInfo($"{SteamFriends.GetFriendPersonaName(who)} left ({change})");
                transport.Close(who);
                welcomed.Remove(who);
                RemotePlayers.Remove(who);
            }
        }

        private static void RefreshMembers()
        {
            Members.Clear();
            int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < n; i++)
                Members.Add(SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i));
        }

        // ---------------- Sending ----------------

        /// <summary>Start building a packet; follow with <see cref="SendBuiltToAll"/> / <see cref="SendBuiltToHost"/>.</summary>
        public static BinaryWriter Begin(MsgType type) => writer.Begin(type);
        public static void SendBuiltToAll(bool reliable) => Broadcast(reliable);
        public static void SendBuiltToHost(bool reliable) { if (IsClient) SendTo(HostId, reliable); }
        public static int BuiltLength => writer.Length;
        public static void SendBuiltTo(CSteamID to, bool reliable) => SendTo(to, reliable);

        private static void SendTo(CSteamID to, bool reliable)
        {
            transport.Send(to, writer.Buffer, writer.Length, reliable);
        }

        private static void Broadcast(bool reliable)
        {
            foreach (CSteamID m in Members)
                if (m != LocalId) SendTo(m, reliable);
        }

        private static float infoTimer;
        private static string lastInfoCharacter, lastInfoWeapon;

        private static void SendLocalPlayerState()
        {
            if (!LocalPlayerAccess.TryGetState(out PlayerStateMsg state)) return;
            BinaryWriter w = writer.Begin(MsgType.PlayerState);
            state.Write(w);
            Broadcast(false);

            // Character + weapon ids for remote 3D bodies: sent on change, and every few seconds
            // (covers late joiners).
            infoTimer -= 1f / Mathf.Max(1, Plugin.SendRateHz.Value);
            string character = LocalPlayerAccess.Data?.Definition?.FullId;
            string weapon = null;
            try { weapon = LocalPlayerAccess.Get()?.CurrentWeapon?.Definition?.FullId; } catch { }
            if (!string.IsNullOrEmpty(character) && (infoTimer <= 0f || character != lastInfoCharacter || weapon != lastInfoWeapon))
            {
                infoTimer = 3f;
                lastInfoCharacter = character;
                lastInfoWeapon = weapon;
                BinaryWriter iw = writer.Begin(MsgType.PlayerInfo);
                iw.Write(character);
                iw.Write(weapon ?? "");
                Broadcast(true);
            }
        }

        public static void BroadcastStartRun(string json)
        {
            if (!IsHost) return;
            writer.Begin(MsgType.StartRun).Write(json);
            Broadcast(true);
        }

        public static void BroadcastFloorInfo(FloorInfoMsg info)
        {
            if (!IsHost) return;
            info.Write(writer.Begin(MsgType.FloorInfo));
            Broadcast(true);
        }

        /// <summary>Host: bring a late joiner up to date with the run in progress.</summary>
        private static void SendRunStateTo(CSteamID to)
        {
            if (RunSync.HostRunJson == null) return;
            writer.Begin(MsgType.StartRun).Write(RunSync.HostRunJson);
            SendTo(to, true);
            foreach (FloorInfoMsg f in RunSync.Floors)
            {
                f.Write(writer.Begin(MsgType.FloorInfo));
                SendTo(to, true);
            }
        }

        // ---------------- Receiving ----------------

        private static void HandlePacket(CSteamID from, byte[] data, int length)
        {
            if (length < 1 || !Members.Contains(from)) return;

            using (var ms = new MemoryStream(data, 1, length - 1, false))
            using (var r = new BinaryReader(ms))
            {
                switch ((MsgType)data[0])
                {
                    case MsgType.Hello when IsHost:
                    {
                        ushort ver = r.ReadUInt16();
                        if (ver != Protocol.Version) { Plugin.Log.LogWarning($"{from} has protocol {ver}"); return; }
                        welcomed.Add(from);
                        writer.Begin(MsgType.Welcome);
                        SendTo(from, true);
                        SendRunStateTo(from);
                        break;
                    }
                    case MsgType.Welcome when from == HostId:
                    {
                        Status = "Connected to host";
                        break;
                    }
                    case MsgType.StartRun when from == HostId:
                    {
                        RunSync.OnStartRunReceived(r.ReadString());
                        break;
                    }
                    case MsgType.FloorInfo when from == HostId:
                    {
                        RunSync.OnFloorInfoReceived(FloorInfoMsg.Read(r));
                        break;
                    }
                    case MsgType.PlayerState:
                    {
                        RemotePlayers.ApplyState(from, PlayerStateMsg.Read(r));
                        break;
                    }
                    case MsgType.PlayerInfo:
                        RemotePlayers.ApplyInfo(from, r.ReadString(), r.ReadString());
                        break;
                    case MsgType.PlayerDowned:
                        DownedSync.OnDowned(from, r);
                        break;
                    case MsgType.Revive:
                        DownedSync.OnRevive(from, r);
                        break;
                    case MsgType.Wipe:
                        DownedSync.OnWipe(from);
                        break;
                    case MsgType.CharacterPick:
                        ReadyRoom.OnPick(from, r);
                        break;
                    case MsgType.ItemShare:
                        ItemShare.OnItemShare(from, r);
                        break;
                    case MsgType.BreakableBroken:
                        WorldSync.OnRemoteBreak(from, r);
                        break;
                    case MsgType.Ping:
                        UI.AllyHud.OnPing(from, r);
                        break;
                    case MsgType.Pong:
                        UI.AllyHud.OnPong(from, r);
                        break;
                    case MsgType.RoomEnter:
                        RoomSync.OnRemoteRoomEnter(from, r);
                        break;
                    case MsgType.ShotFired:
                        ShotSync.OnShot(from, r);
                        break;
                    case MsgType.WeaponFx:
                        ShotSync.OnWeaponFx(from, r);
                        break;
                    case MsgType.BeamStart:
                        ShotSync.OnBeamStart(from, r);
                        break;
                    case MsgType.BeamUpdate:
                        ShotSync.OnBeamUpdate(from, r);
                        break;
                    case MsgType.BeamEnd:
                        ShotSync.OnBeamEnd(from, r);
                        break;
                    case MsgType.XpShare:
                        SharedProgress.OnXpShare(r);
                        break;
                    case MsgType.KeyShare:
                        SharedProgress.OnKeyShare(r);
                        break;
                    case MsgType.LevelCleared when from == HostId:
                        RunSync.OnHostLevelCleared(r.ReadInt32());
                        break;
                    case MsgType.FightClear when from == HostId:
                        FightSync.OnHostFightClear(r.ReadInt32(), r.ReadInt32());
                        break;
                    case MsgType.EnemySpawn when from == HostId:
                        NetEnemies.OnSpawn(r);
                        break;
                    case MsgType.EnemySnapshots when from == HostId:
                        NetEnemies.OnSnapshots(r);
                        break;
                    case MsgType.EnemyDie when from == HostId:
                        NetEnemies.OnDie(r);
                        break;
                    case MsgType.EnemyAttack when from == HostId:
                        NetEnemies.OnAttack(r);
                        break;
                    case MsgType.EnemyImpulse when IsHost:
                        NetEnemies.OnImpulseRequest(r);
                        break;
                    case MsgType.EnemyStatus when IsHost:
                        NetEnemies.OnStatusRequest(from, r);
                        break;
                    case MsgType.Gather when from == HostId:
                        GatherSync.OnGather(r);
                        break;
                    case MsgType.ObeliskHit when IsHost:
                        ObjectiveSync.OnObeliskHit(r);
                        break;
                    case MsgType.ObeliskDead when from == HostId:
                        ObjectiveSync.OnObeliskDead(r);
                        break;
                    case MsgType.SequenceActivate when IsHost:
                        ObjectiveSync.OnSequenceActivate(r);
                        break;
                    case MsgType.SequenceStates when from == HostId:
                        ObjectiveSync.OnSequenceStates(r);
                        break;
                    case MsgType.ScavengePicked when IsHost:
                        ObjectiveSync.OnScavengePicked(r);
                        break;
                    case MsgType.PortalReady:
                        PortalSync.OnPortalReady(from, r);
                        break;
                    case MsgType.EnemyHurt when IsHost:
                        NetEnemies.OnHurtRequest(from, r);
                        break;
                }
            }
        }
    }
}
