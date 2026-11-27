using System.Collections.Generic;
using Logic.Fights;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Room fights: the host decides when a fight is over (its enemies are the real ones).
    /// Client fights still start locally (doors lock, music) but spawn nothing and only end
    /// when the host says so.
    /// </summary>
    internal static class FightSync
    {
        private static readonly HashSet<int> clearedRooms = new HashSet<int>();
        private static int clearedLevel = -1;

        public static string LastInfo { get; private set; } = "-";

        public static void Reset()
        {
            clearedRooms.Clear();
            clearedLevel = -1;
            LastInfo = "-";
        }

        private static void EnsureLevel(int level)
        {
            if (clearedLevel == level) return;
            clearedLevel = level;
            clearedRooms.Clear();
        }

        // Host
        public static void HostOnFightClear(Fight fight)
        {
            if (!Session.IsHost || fight?.Room?.Room == null) return;
            var w = Session.Begin(MsgType.FightClear);
            w.Write(StageParameters.DungeonLevel);
            w.Write(fight.Room.Room.Id);
            Session.SendBuiltToAll(true);
            LastInfo = $"Room {fight.Room.Room.Id} cleared";
        }

        // Client
        public static void OnHostFightClear(int level, int roomId)
        {
            EnsureLevel(level);
            clearedRooms.Add(roomId);
            LastInfo = $"Host cleared room {roomId}";
            if (level != StageParameters.DungeonLevel) return;

            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (logic != null && logic.IsFighting && logic.CurrentFight?.Room?.Room?.Id == roomId)
            {
                Plugin.Log.LogInfo($"[client] Ending fight in room {roomId} (host cleared it)");
                logic.CurrentFight.EndFight();
            }
            else RoomSync.ClientMarkCleared(roomId);
        }

        /// <summary>Client fight just started: if the host already cleared this room, end it now.</summary>
        public static void ClientOnFightStarted(RoomScript room, Fight fight)
        {
            if (!Session.IsClient || room?.Room == null) return;
            EnsureLevel(StageParameters.DungeonLevel);
            if (clearedRooms.Contains(room.Room.Id))
            {
                Plugin.Log.LogInfo($"[client] Room {room.Room.Id} already cleared by host, ending fight");
                fight.EndFight();
            }
            else LastInfo = $"Fighting in room {room.Room.Id} (waiting for host)";
        }

        /// <summary>Escape hatch from the F8 menu if a client fight gets stuck.</summary>
        public static void ForceEndCurrentFight()
        {
            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (logic != null && logic.IsFighting) logic.CurrentFight.EndFight();
        }
    }
}
