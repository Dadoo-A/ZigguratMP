using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ZigguratMP.Net;
using ZigguratMP.UI;

namespace ZigguratMP
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Ziggurat2.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.zigguratmp.coop";
        public const string Name = "Ziggurat MP";
        public const string Version = "0.9.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyCode> MenuKey;
        internal static ConfigEntry<int> SendRateHz;
        internal static ConfigEntry<KeyCode> ShareKey;
        internal static ConfigEntry<bool> TeleportToFights;
        internal static ConfigEntry<float> PortalCountdown;
        internal static ConfigEntry<float> EnemyCountPerPlayer;
        internal static ConfigEntry<float> ChampionChancePerPlayer;
        internal static ConfigEntry<bool> SharedEconomy;
        internal static ConfigEntry<float> InterpDelay;
        internal static ConfigEntry<float> EnemyHealthPerPlayer;
        internal static ConfigEntry<float> BossHealthPerPlayer;

        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F8, "Toggle the multiplayer menu.");
            SendRateHz = Config.Bind("Network", "SendRateHz", 20, "Player state snapshots sent per second.");
            ShareKey = Config.Bind("General", "ShareKey", KeyCode.G, "Look at a weapon/amulet on the ground and press this to give it to the other players.");
            TeleportToFights = Config.Bind("Coop", "GatherInFights", false, "When a fight or the boss starts, teleport you next to the player inside the room if you are outside. Off by default (also in F8 > Settings).");
            PortalCountdown = Config.Bind("Coop", "PortalCountdown", 30f, "Seconds the exit portal waits for the other players once someone is in it.");
            EnemyCountPerPlayer = Config.Bind("Difficulty", "EnemyCountPerPlayer", 0.35f, "Host: extra fight threat (more / tougher waves) per additional player (0.35 = +35%).");
            ChampionChancePerPlayer = Config.Bind("Difficulty", "ChampionChancePerPlayer", 0.25f, "Host: extra champion chance per additional player.");
            SharedEconomy = Config.Bind("Coop", "SharedEconomy", false, "Host: fountains, shrines and shop items are used up for everyone when one player uses them (off = each player has their own).");
            InterpDelay = Config.Bind("Network", "InterpDelay", 0.15f, "Seconds remote players are shown behind real time (higher = smoother, more delay).");
            EnemyHealthPerPlayer = Config.Bind("Difficulty", "EnemyHealthPerPlayer", 0.6f, "Host: extra enemy health per additional player (0.6 = +60%).");
            BossHealthPerPlayer = Config.Bind("Difficulty", "BossHealthPerPlayer", 0.75f, "Host: extra boss health per additional player.");

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            Session.Init();
            Log.LogInfo($"{Name} {Version} loaded. Press {MenuKey.Value} for the multiplayer menu.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(MenuKey.Value))
                MpMenu.Visible = !MpMenu.Visible;
            if (Input.GetKeyDown(ShareKey.Value))
                Sync.ItemShare.TryShare();

            Session.Tick(Time.unscaledDeltaTime);
        }

        private void OnGUI()
        {
            MpMenu.Draw();
            Sync.RemotePlayers.DrawLabels();
            Sync.DownedSync.DrawHud();
            Sync.ItemShare.DrawHud();
            Sync.PortalSync.DrawHud();
            Sync.ReadyRoom.DrawOverlay();
        }

        private void OnDestroy()
        {
            Session.Shutdown();
            harmony?.UnpatchSelf();
        }
    }
}
