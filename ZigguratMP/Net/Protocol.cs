using System.IO;
using UnityEngine;

namespace ZigguratMP.Net
{
    /// <summary>Wire message ids. Bump <see cref="Protocol.Version"/> on any breaking change.</summary>
    internal enum MsgType : byte
    {
        Hello = 1,        // client -> host: protocol version
        Welcome = 2,      // host -> client: accepted
        StartRun = 3,     // host -> all: StageParameters JSON (mission, mode, difficulty, level)
        FloorInfo = 4,
        CharacterPick = 5, // any -> all: I picked character X in the briefing (ready)    // host -> all: dungeon builder inputs for one floor
        PlayerState = 10, // any -> all: transform snapshot (unreliable)
        PlayerInfo = 11,  // any -> all: character FullId (for the 3D body)
        PlayerDowned = 12, // any -> all: I'm down / back up
        Revive = 13,       // any -> all: I revived player X (target applies it)
        Wipe = 14,         // any -> all: last player died, everyone dies for real
        BreakableBroken = 18, // any -> all: I broke the prop at P
        ItemShare = 19,    // any -> all: pickup JSON given to the others
        Ping = 50,         // any -> all
        Pong = 51,         // reply to Ping

        XpShare = 30,     // any -> all: I earned N XP, you get it too
        KeyShare = 31,    // any -> all: I picked up a key at P

        LevelCleared = 15,   // host -> all: host left the floor through the portal
        FightClear = 16,     // host -> all: room fight finished (unlock doors)

        EnemySpawn = 20,     // host -> all
        EnemySnapshots = 21, // host -> all (unreliable, batched)
        EnemyDie = 22,       // host -> all
        EnemyAttack = 23,    // host -> all: replay attack locally (bullets hit the local player)
        EnemyHurt = 24,      // client -> host: I hit enemy X for N damage

        RoomEnter = 17,      // any -> all: I entered room X (map reveal; host starts the fight)

        ShotFired = 40,      // any -> all: one of my bullets (visual replay, no damage)
        WeaponFx = 41,       // any -> all: I fired attack N (muzzle fx, sound, recoil)
        BeamStart = 42,      // any -> all: beam id + definition
        BeamUpdate = 43,     // any -> all (unreliable): beam transform
        BeamEnd = 44,        // any -> all
    }

    internal static class Protocol
    {
        public const ushort Version = 8;
        public const int Channel = 0;
        public const string LobbyKeyMod = "zmp_mod";
        public const string LobbyKeyVersion = "zmp_ver";
    }

    /// <summary>
    /// Builder inputs that depend on per-save/per-player state (unlock tier, perks) and would
    /// otherwise make the client's layout diverge even with the same mission seed.
    /// </summary>
    internal struct FloorInfoMsg
    {
        public int Level;
        public int Seed;
        public int BossRoomType;
        public bool ForceShop;
        public bool ForceShrine;
        public bool ForceFountain;
        public int ExtraSpecialRooms;

        public void Write(BinaryWriter w)
        {
            w.Write(Level); w.Write(Seed); w.Write(BossRoomType);
            w.Write(ForceShop); w.Write(ForceShrine); w.Write(ForceFountain);
            w.Write(ExtraSpecialRooms);
        }

        public static FloorInfoMsg Read(BinaryReader r) => new FloorInfoMsg
        {
            Level = r.ReadInt32(), Seed = r.ReadInt32(), BossRoomType = r.ReadInt32(),
            ForceShop = r.ReadBoolean(), ForceShrine = r.ReadBoolean(), ForceFountain = r.ReadBoolean(),
            ExtraSpecialRooms = r.ReadInt32(),
        };
    }

    internal struct PlayerStateMsg
    {
        public float Time;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public Vector3 Velocity;
        public byte Flags;
        public byte Health; // 0..255 = health progress

        public const byte FlagGrounded = 1, FlagDashing = 2, FlagJumping = 4, FlagShooting = 8;

        public void Write(BinaryWriter w)
        {
            w.Write(Time);
            w.Write(Position.x); w.Write(Position.y); w.Write(Position.z);
            w.Write(Yaw);
            w.Write(Pitch);
            w.Write(Velocity.x); w.Write(Velocity.y); w.Write(Velocity.z);
            w.Write(Flags);
            w.Write(Health);
        }

        public static PlayerStateMsg Read(BinaryReader r) => new PlayerStateMsg
        {
            Time = r.ReadSingle(),
            Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Yaw = r.ReadSingle(),
            Pitch = r.ReadSingle(),
            Velocity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Flags = r.ReadByte(),
            Health = r.ReadByte(),
        };
    }

    /// <summary>Reusable packet builder to avoid per-send allocations of streams.</summary>
    internal sealed class PacketWriter
    {
        private readonly MemoryStream stream = new MemoryStream(256);
        public readonly BinaryWriter W;

        public PacketWriter() { W = new BinaryWriter(stream); }

        public BinaryWriter Begin(MsgType type)
        {
            stream.SetLength(0);
            W.Write((byte)type);
            return W;
        }

        public byte[] Buffer => stream.GetBuffer();
        public int Length => (int)stream.Length;
    }
}
