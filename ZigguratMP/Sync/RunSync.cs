using System;
using System.Collections.Generic;
using Logic;
using Logic.Campaign;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Run start + floor determinism.
    /// Host: captures StageParameters (whole MissionData incl. RandSeed) when a run starts and the
    /// builder inputs of every floor it generates. Client: starts the same mission automatically
    /// and forces the host's builder inputs on each floor.
    /// </summary>
    internal static class RunSync
    {
        /// <summary>Host: JSON of the run in progress (re-sent to late joiners). Client: run to start.</summary>
        public static string HostRunJson { get; private set; }
        private static string pendingStartJson;
        private static float pendingSince;
        private static int pendingStartLevel = 1;
        private const float FloorInfoTimeout = 30f;

        private static readonly Dictionary<int, FloorInfoMsg> floors = new Dictionary<int, FloorInfoMsg>();
        public static IEnumerable<FloorInfoMsg> Floors => floors.Values;

        public static string LastInfo { get; private set; } = "-";

        public static void Reset()
        {
            HostRunJson = null;
            pendingStartJson = null;
            chosenLoadout = null;
            clearedLevel = -1;
            deferredNextLevel = null;
            floors.Clear();
            LastInfo = "-";
        }

        // ---------------- Host side ----------------

        public static void OnHostRunStarted()
        {
            if (!Session.IsHost || StageParameters.IsTutorial) return;
            floors.Clear();
            HostRunJson = StageParameters.ToJson().ToString(Formatting.None);
            ReadyRoom.OnRunStarted();
            LastInfo = $"Run started ({StageParameters.DungeonMode}), seed {StageParameters.Mission.RandSeed}";
            Plugin.Log.LogInfo($"[host] {LastInfo}");
            Session.BroadcastStartRun(HostRunJson);
        }

        public static void OnHostFloorGenerated(DungeonBuilderParameters p, int finalSeed)
        {
            var info = new FloorInfoMsg
            {
                Level = StageParameters.DungeonLevel,
                Seed = finalSeed,
                BossRoomType = (int)p.BossRoomType,
                ForceShop = p.ForceShop,
                ForceShrine = p.ForceShrine,
                ForceFountain = p.ForceFountain,
                ExtraSpecialRooms = p.ExtraSpecialRooms,
            };
            floors[info.Level] = info;
            LastInfo = $"Floor {info.Level} seed {info.Seed}";
            Plugin.Log.LogInfo($"[host] {LastInfo}");
            Session.BroadcastFloorInfo(info);
        }

        // ---------------- Client side ----------------

        public static void OnStartRunReceived(string json)
        {
            pendingStartJson = json;
            ReadyRoom.OnRunStarted(); // keep chosenLoadout if we already picked in the ready room
            pendingSince = UnityEngine.Time.unscaledTime;
            try { pendingStartLevel = (int?)JObject.Parse(json)["level"] ?? 1; }
            catch (JsonException) { pendingStartLevel = 1; }
            floors.Clear();
            LastInfo = "Host started a run";
            Plugin.Log.LogInfo("[client] Host started a run, will follow as soon as possible");
        }

        public static void OnFloorInfoReceived(FloorInfoMsg info)
        {
            floors[info.Level] = info;
            Plugin.Log.LogInfo($"[client] Floor {info.Level} info received, seed {info.Seed}");
        }

        // ---------------- Floor transitions ----------------

        private static int clearedLevel = -1;
        private static StageScript deferredNextLevel;
        private static float deferredSince;
        private static bool bypassNextLevelDefer;
        private const float NextFloorTimeout = 90f;

        /// <summary>Prefix of StageScript.LevelCleared. Prevents a double clear on the same floor.</summary>
        public static bool BeforeLevelCleared()
        {
            if (!Session.InLobby || StageParameters.IsTutorial) return true;
            if (clearedLevel == StageParameters.DungeonLevel) return false;
            clearedLevel = StageParameters.DungeonLevel;
            return true;
        }

        public static void AfterLevelCleared()
        {
            if (!Session.IsHost || StageParameters.IsTutorial) return;
            Session.Begin(MsgType.LevelCleared).Write(StageParameters.DungeonLevel);
            Session.SendBuiltToAll(true);
            Plugin.Log.LogInfo($"[host] Floor {StageParameters.DungeonLevel} cleared, pulling clients along");
        }

        /// <summary>Client: host took the portal → we leave the floor too.</summary>
        public static void OnHostLevelCleared(int level)
        {
            StageScript stage = StageScript.Instance;
            if (stage == null || level != StageParameters.DungeonLevel || clearedLevel == level) return;
            if (stage.StageState != StageScript.StageStates.Playing) return;
            Plugin.Log.LogInfo($"[client] Host cleared floor {level}, following");
            LastInfo = $"Host cleared floor {level}";
            stage.LevelCleared();
        }

        /// <summary>Prefix of StageScript.LoadNextLevel. Client waits until the host has generated it.</summary>
        public static bool BeforeLoadNextLevel(StageScript stage)
        {
            if (!Session.IsClient || bypassNextLevelDefer) return true;
            int next = StageParameters.DungeonLevel + 1;
            if (floors.ContainsKey(next)) return true;
            deferredNextLevel = stage;
            deferredSince = UnityEngine.Time.unscaledTime;
            LastInfo = $"Waiting for host to enter floor {next}...";
            Plugin.Log.LogInfo($"[client] {LastInfo}");
            return false;
        }

        private static void TickDeferredNextLevel()
        {
            if (deferredNextLevel == null) return;
            int next = StageParameters.DungeonLevel + 1;
            bool timedOut = UnityEngine.Time.unscaledTime - deferredSince > NextFloorTimeout;
            if (!floors.ContainsKey(next) && !timedOut && Session.IsClient) return;

            if (timedOut) Plugin.Log.LogWarning($"[client] Host never entered floor {next}, loading alone");
            StageScript stage = deferredNextLevel;
            deferredNextLevel = null;
            bypassNextLevelDefer = true;
            try { stage.LoadNextLevel(); }
            finally { bypassNextLevelDefer = false; }
        }

        // ---------------- Client: joining the host's run ----------------
        // The client opens any mission in the game's own UI (briefing screen: character + weapons)
        // and presses Start. We intercept that start and launch the host's mission instead,
        // keeping the character and starting gear picked there.

        private sealed class Loadout
        {
            public CharacterDefinition Character;
            public List<EquipmentDefinition> Equipment;
        }

        private static Loadout chosenLoadout;
        private static bool startingHostRun;

        public static bool HasPendingRun => pendingStartJson != null;

        /// <summary>Prefix of CampaignData.LoadMission / ExtraModesData.LoadGame. True = intercepted.</summary>
        public static bool TryJoinWithLoadoutOf(MissionData mission)
        {
            if (!Session.IsClient || startingHostRun || mission == null) return false;
            if (mission.Type == MissionTypes.Tutorial) return false;
            chosenLoadout = new Loadout
            {
                Character = mission.Character,
                Equipment = mission.StartingEquipment != null ? new List<EquipmentDefinition>(mission.StartingEquipment) : null,
            };
            if (pendingStartJson == null)
            {
                // Host hasn't launched yet: this is our pick; we join automatically once it starts.
                ReadyRoom.ClientPicked(mission.Character?.FullId);
                LastInfo = $"Ready as {mission.Character?.FullId}, waiting for the host";
                return true;
            }
            LastInfo = $"Joining host run as {mission.Character?.FullId}...";
            Plugin.Log.LogInfo($"[client] Mission start intercepted: joining host run with {mission.Character?.FullId}");
            return true;
        }

        /// <summary>F8 "Join now": skip the briefing screen.</summary>
        public static void JoinNow(bool ownProposedLoadout)
        {
            if (pendingStartJson == null) return;
            MissionLoadout l = ownProposedLoadout ? SaveManager.Instance?.CurrentCampaign?.NextMissionLoadout : null;
            chosenLoadout = new Loadout { Character = l?.DefaultCharacter, Equipment = l?.DefaultEquipment };
        }

        /// <summary>Called every frame on the client; starts the host run once a loadout is chosen.</summary>
        private static float catchUpSince = -1f;

        /// <summary>
        /// Client joined late (or fell behind): host is on a deeper floor. Clear floors one by one
        /// (same path as following the host through the portal) until we are on the host's floor.
        /// </summary>
        private static void TickCatchUp()
        {
            StageScript stage = StageScript.Instance;
            if (!Session.IsClient || stage == null || floors.Count == 0 || StageParameters.IsTutorial) { catchUpSince = -1f; return; }
            int hostLevel = int.MinValue;
            foreach (int l in floors.Keys) if (l > hostLevel) hostLevel = l;
            int level = StageParameters.DungeonLevel;
            if (hostLevel <= level || stage.StageState != StageScript.StageStates.Playing || clearedLevel == level || deferredNextLevel != null)
            {
                catchUpSince = -1f;
                return;
            }
            if (catchUpSince < 0f) catchUpSince = UnityEngine.Time.unscaledTime;
            if (UnityEngine.Time.unscaledTime - catchUpSince < 4f) return; // let the floor settle first
            catchUpSince = -1f;
            Plugin.Log.LogInfo($"[client] Host is on floor {hostLevel}, skipping floor {level} to catch up");
            LastInfo = $"Catching up with host (floor {hostLevel})";
            stage.LevelCleared();
        }

        public static void Tick()
        {
            TickDeferredNextLevel();
            TickCatchUp();
            if (pendingStartJson == null || !Session.IsClient) return;

            if (StageScript.Instance != null)
            {
                LastInfo = "Host started a run: go back to the hub to follow";
                return;
            }
            SaveManager save = SaveManager.Instance;
            if (save == null || save.CurrentSlot == null || PanelManager.Instance == null)
            {
                LastInfo = "Host started a run: load your save to follow";
                return;
            }
            if (chosenLoadout == null)
            {
                LastInfo = "Host started a run: open a mission, pick character + weapons, press Start";
                return;
            }

            // Host sends FloorInfo while it generates the floor, a few seconds after StartRun.
            // Wait for it so our first floor is built from the host's inputs (timeout = fallback).
            if (!floors.ContainsKey(pendingStartLevel) && UnityEngine.Time.unscaledTime - pendingSince < FloorInfoTimeout)
            {
                LastInfo = $"Waiting for host floor {pendingStartLevel}...";
                return;
            }

            string json = pendingStartJson;
            Loadout loadout = chosenLoadout;
            pendingStartJson = null;
            chosenLoadout = null;
            try
            {
                StartFromJson(save, json, loadout);
            }
            catch (Exception e)
            {
                LastInfo = "Failed to start host run (see log)";
                Plugin.Log.LogError($"[client] Could not start host run: {e}");
            }
        }

        private static void StartFromJson(SaveManager save, string json, Loadout loadout)
        {
            JObject j = JObject.Parse(json);
            var mode = (DungeonModes)(int)j["mode"];
            var missionJson = (JObject)j["mission"];
            bool campaignMode = mode == DungeonModes.Campaign || mode == DungeonModes.CampaignLinear || mode == DungeonModes.CampaignGauntlet;

            if (loadout?.Character != null)
            {
                missionJson["character"] = loadout.Character.FullId;
                if (loadout.Equipment != null && loadout.Equipment.Count > 0)
                {
                    var gear = new JArray();
                    foreach (EquipmentDefinition e in loadout.Equipment)
                        if (e != null) gear.Add(e.FullId);
                    missionJson["startingEquipment"] = gear;
                }
                Plugin.Log.LogInfo($"[client] Using own loadout: {loadout.Character.FullId} + {loadout.Equipment?.Count ?? 0} items");
            }

            MissionData mission = MissionData.FromJson(missionJson);
            clearedLevel = -1;
            Plugin.Log.LogInfo($"[client] Starting host run: mode {mode}, seed {mission.RandSeed}, character {mission.Character?.FullId}");
            LastInfo = $"Following host run, seed {mission.RandSeed}";

            startingHostRun = true;
            try
            {
                if (campaignMode)
                {
                    if (save.CurrentCampaign == null) throw new InvalidOperationException("No campaign in current save");
                    save.CurrentCampaign.LoadMission(mission);
                }
                else
                {
                    if (save.CurrentExtraModes == null) throw new InvalidOperationException("No extra modes data in current save");
                    save.CurrentExtraModes.LoadGame(mission);
                }
            }
            finally { startingHostRun = false; }
        }

        /// <summary>Client: overwrite builder inputs with the host's for this floor.</summary>
        public static void ApplyFloorOverride(DungeonBuilderParameters p)
        {
            int level = StageParameters.DungeonLevel;
            if (!floors.TryGetValue(level, out FloorInfoMsg f))
            {
                Plugin.Log.LogWarning($"[client] No host info for floor {level}: layout may differ. Let the host enter the floor first.");
                LastInfo = $"Floor {level}: no host info (layout may differ)";
                return;
            }
            p.Seed = f.Seed;
            p.BossRoomType = (BossRoomTypes)f.BossRoomType;
            p.ForceShop = f.ForceShop;
            p.ForceShrine = f.ForceShrine;
            p.ForceFountain = f.ForceFountain;
            p.ExtraSpecialRooms = f.ExtraSpecialRooms;
            LastInfo = $"Floor {level} synced, seed {f.Seed}";
            Plugin.Log.LogInfo($"[client] {LastInfo}");
        }
    }
}
