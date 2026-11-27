using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Logic;
using Logic.Campaign;
using Milkstone.Logic.Buffs;
using Milkstone.Utils;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Fairness and balance in co-op:
    ///  - difficulty: more enemies per wave and more champions per extra player (host);
    ///  - economy: optional shared fountains / shrines / shop (host setting, lobby data);
    ///  - omens / run modifiers that change enemies apply for everyone;
    ///  - co-op runs never post to leaderboards.
    /// </summary>
    internal static class BalanceSync
    {
        private const string LobbyKeyEconomy = "zmp_eco";

        public static bool CoopRun { get; private set; }

        /// <summary>Run start postfix (campaign / extra modes), host and client.</summary>
        public static void OnAnyRunStarted() => CoopRun = Session.InLobby && Session.Members.Count > 1;

        // ------------------------------------------------------------------ difficulty (host)

        private static PlayerData scaledData;
        private static BuffFloat threatBuff;
        private static object scaledStage;
        private static BuffFloat championBuff;
        private static float appliedThreat = 1f, appliedChampion = 1f;

        private static void TickDifficulty()
        {
            if (!Session.IsHost || LocalPlayerAccess.Get() == null || StageParameters.IsTutorial) return;
            int extra = Mathf.Clamp(Session.Members.Count, 1, 4) - 1;
            float threat = 1f + extra * Plugin.EnemyCountPerPlayer.Value;
            float champ = 1f + extra * Plugin.ChampionChancePerPlayer.Value;

            PlayerData data = LocalPlayerAccess.Data;
            if (data != null && (data != scaledData || !Mathf.Approximately(threat, appliedThreat)))
            {
                if (scaledData != null && threatBuff != null) scaledData.GetStat(PlayerData.PlayerStats.FightThreatFactor).RemoveBuff(threatBuff);
                threatBuff = new BuffFloat(BuffTypes.Multiplicative, threat);
                data.GetStat(PlayerData.PlayerStats.FightThreatFactor).AddBuff(threatBuff);
                scaledData = data;
                appliedThreat = threat;
            }

            StageData stage = StageScript.StageData;
            if (stage != null && (!ReferenceEquals(stage, scaledStage) || !Mathf.Approximately(champ, appliedChampion)))
            {
                if (scaledStage is StageData old && championBuff != null) old.GetStat(StageData.StageStats.EnemyChampionChance).RemoveBuff(championBuff);
                championBuff = new BuffFloat(BuffTypes.Multiplicative, champ);
                stage.GetStat(StageData.StageStats.EnemyChampionChance).AddBuff(championBuff);
                scaledStage = stage;
                appliedChampion = champ;
            }
        }

        public static string DifficultyInfo
        {
            get
            {
                int extra = Mathf.Clamp(Session.Members.Count, 1, 4) - 1;
                return $"Enemies x{1f + extra * Plugin.EnemyCountPerPlayer.Value:0.##}, champions x{1f + extra * Plugin.ChampionChancePerPlayer.Value:0.##}";
            }
        }

        // ------------------------------------------------------------------ economy

        /// <summary>Host's choice, published in the lobby so every player follows the same rule.</summary>
        public static bool SharedEconomy =>
            Session.InLobby && SteamMatchmaking.GetLobbyData(Session.LobbyId, LobbyKeyEconomy) == "shared";

        private static bool publishedEconomy;
        private static bool lastPublished;

        private static void TickPublishEconomy()
        {
            if (!Session.IsHost) { publishedEconomy = false; return; }
            bool want = Plugin.SharedEconomy.Value;
            if (publishedEconomy && want == lastPublished) return;
            SteamMatchmaking.SetLobbyData(Session.LobbyId, LobbyKeyEconomy, want ? "shared" : "own");
            publishedEconomy = true;
            lastPublished = want;
        }

        private static bool applyingRemote;
        public const byte EcoFountain = 0, EcoShrine = 1, EcoShop = 2;

        /// <summary>Interact postfix of fountains / shrines / shop items (local player).</summary>
        public static void OnLocalEconomyUse(byte kind, Component c, bool success)
        {
            if (!success || applyingRemote || c == null || !SharedEconomy) return;
            BinaryWriter w = Session.Begin(MsgType.EconomyUsed);
            w.Write(StageParameters.DungeonLevel);
            w.Write(kind);
            Vector3 p = c.transform.position;
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            Session.SendBuiltToAll(true);
        }

        public static void OnEconomyUsed(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            byte kind = r.ReadByte();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (level != StageParameters.DungeonLevel || !SharedEconomy || LocalPlayerAccess.Get() == null) return;
            applyingRemote = true;
            try
            {
                switch (kind)
                {
                    case EcoFountain:
                        Component f = Nearest("FountainScript", pos);
                        if (f != null)
                        {
                            Traverse.Create(f).Field("opened").SetValue(true);
                            Traverse.Create(f).Method("DisableItems").GetValue();
                        }
                        break;
                    case EcoShrine:
                        ShrineScript s = Nearest("ShrineScript", pos) as ShrineScript;
                        if (s != null) Traverse.Create(s).Field("opened").SetValue(true);
                        break;
                    case EcoShop:
                        ShopScript shop = Nearest("ShopScript", pos) as ShopScript;
                        if (shop != null)
                        {
                            Traverse t = Traverse.Create(shop);
                            t.Field("available").SetValue(false);
                            if (shop.BeforeBuying != null) shop.BeforeBuying.SetActive(false);
                            if (shop.AfterBuying != null) shop.AfterBuying.SetActive(true);
                            GameObject preview = t.Field("PreviewSlot").GetValue<GameObject>();
                            if (preview != null) { Object.Destroy(preview); t.Field("PreviewSlot").SetValue(null); }
                        }
                        break;
                }
                Plugin.Log.LogInfo($"[economy] {SteamFriends.GetFriendPersonaName(from)} used a shared {(kind == EcoFountain ? "fountain" : kind == EcoShrine ? "shrine" : "shop item")}");
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"economy: {e.Message}"); }
            finally { applyingRemote = false; }
        }

        private static Component Nearest(string typeName, Vector3 pos)
        {
            System.Type t = AccessTools.TypeByName(typeName);
            if (t == null) return null;
            Component best = null;
            float bestD = 1.5f * 1.5f;
            foreach (Object o in Object.FindObjectsOfType(t))
            {
                var c = o as Component;
                if (c == null) continue;
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // ------------------------------------------------------------------ enemy run modifiers

        private static readonly HashSet<RunModifierTags> EnemyTags = new HashSet<RunModifierTags>
        {
            RunModifierTags.EnemyHealth, RunModifierTags.EnemySpeed, RunModifierTags.EnemyDamage, RunModifierTags.EnemyStatusEffects,
        };

        /// <summary>PlayerData.ApplyRunModifier postfix (local choice at an omen, or run start).</summary>
        public static void OnLocalRunModifier(RunModifierPair pair)
        {
            if (pair == null || !Session.InLobby || Session.Members.Count <= 1) return;
            foreach (RunModifierDefinition def in new[] { pair.Positive, pair.Negative })
            {
                if (def == null || !EnemyTags.Contains(def.Tag)) continue;
                Session.Begin(MsgType.EnemyModifier).Write(def.FullId);
                Session.SendBuiltToAll(true);
                Plugin.Log.LogInfo($"[omen] Shared enemy modifier {def.FullId}");
            }
        }

        public static void OnEnemyModifier(CSteamID from, BinaryReader r)
        {
            string id = r.ReadString();
            PlayerData data = LocalPlayerAccess.Data;
            if (data == null) return;
            RunModifierDefinition def = ItemManager.GetItem<RunModifierDefinition>(id);
            if (def == null) return;
            data.ApplyModifiers(new RunModifierPair(def, null), isRandom: true);
            Plugin.Log.LogInfo($"[omen] {SteamFriends.GetFriendPersonaName(from)}'s omen affects enemies: {id}");
        }

        public static void Tick()
        {
            if (!Session.InLobby) return;
            TickDifficulty();
            TickPublishEconomy();
        }

        public static void Reset()
        {
            publishedEconomy = false;
            scaledData = null; threatBuff = null; scaledStage = null; championBuff = null;
            appliedThreat = appliedChampion = 1f;
        }
    }
}
