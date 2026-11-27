using System.Collections.Generic;
using System.IO;
using System.Linq;
using Logic;
using Logic.Fights;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Quality of life:
    ///  - spectator camera on an ally while downed;
    ///  - ping markers;
    ///  - late join: start on the host's floor, next to the host, with the host's map/cleared rooms;
    ///  - host leaving mid-run: clients keep playing solo (puppets get their AI back).
    /// </summary>
    internal static class ComfortSync
    {
        // ------------------------------------------------------------------ spectator

        private static bool spectating;
        private static Transform camT;
        private static Vector3 camLocalPos;
        private static Quaternion camLocalRot;
        private static Camera handsCam;
        private static bool handsWasEnabled;
        private static int spectateIndex;
        private static string spectatedName;

        public static void LateTick()
        {
            Player p = LocalPlayerAccess.Get();
            List<RemotePlayers.GhostView> allies = RemotePlayers.VisibleGhosts().Where(g => !g.Downed).ToList();
            bool want = DownedSync.LocalDowned && p != null && allies.Count > 0;

            if (want && !spectating) StartSpectate(p);
            if (!want) { StopSpectate(); return; }

            if (Input.GetKeyDown(Plugin.SpectateNextKey.Value)) spectateIndex++;
            RemotePlayers.GhostView target = allies[((spectateIndex % allies.Count) + allies.Count) % allies.Count];
            spectatedName = target.Name;

            // Orbit around the ally; the mouse still turns the (downed) player, which drives the orbit.
            Vector3 pivot = target.Position + Vector3.up * 1.6f;
            Vector3 desired = pivot + Quaternion.Euler(18f, p.transform.eulerAngles.y, 0f) * new Vector3(0f, 0f, -4.2f);
            Vector3 dir = desired - pivot;
            if (Physics.Raycast(pivot, dir.normalized, out RaycastHit hit, dir.magnitude, 1 << GameGlobals.LayerLevel, QueryTriggerInteraction.Ignore))
                desired = hit.point - dir.normalized * 0.3f;
            camT.position = desired;
            camT.rotation = Quaternion.LookRotation(pivot - desired, Vector3.up);
        }

        private static void StartSpectate(Player p)
        {
            Camera cam = p.Camera != null ? p.Camera.Camera : null;
            if (cam == null) return;
            camT = cam.transform;
            camLocalPos = camT.localPosition;
            camLocalRot = camT.localRotation;
            handsCam = p.HandsCamera;
            handsWasEnabled = handsCam != null && handsCam.enabled;
            if (handsCam != null) handsCam.enabled = false; // hide the first-person weapon overlay
            spectating = true;
        }

        private static void StopSpectate()
        {
            if (!spectating) return;
            spectating = false;
            if (camT != null) { camT.localPosition = camLocalPos; camT.localRotation = camLocalRot; }
            if (handsCam != null) handsCam.enabled = handsWasEnabled;
            camT = null;
            handsCam = null;
        }

        // ------------------------------------------------------------------ pings

        private struct Marker { public string Name; public Vector3 Pos; public bool Enemy; public float Until; }
        private static readonly List<Marker> markers = new List<Marker>();
        private static float nextPing;

        public static void TryPing()
        {
            Player p = LocalPlayerAccess.Get();
            if (p == null || !Session.InLobby || Time.unscaledTime < nextPing) return;
            Camera cam = spectating ? null : p.Camera?.Camera;
            if (cam == null) return;
            // First hit that isn't the local player itself (the camera sits inside its colliders).
            RaycastHit hit = default;
            bool found = false;
            foreach (RaycastHit h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 250f, ~0, QueryTriggerInteraction.Collide).OrderBy(x => x.distance))
            {
                if (h.collider == null || h.collider.GetComponentInParent<Player>() == p) continue;
                bool enemyHit = h.collider.GetComponentInParent<Enemy>() != null;
                if (h.collider.isTrigger && !enemyHit) continue; // room triggers, pickups...
                hit = h; found = true; break;
            }
            if (!found) return;
            nextPing = Time.unscaledTime + 0.4f;
            bool enemy = hit.collider.GetComponentInParent<Enemy>() is Enemy e && e.Team == Team.Enemy;
            Vector3 pos = enemy ? hit.collider.bounds.center : hit.point;
            AddMarker(SteamFriends.GetPersonaName(), pos, enemy);
            BinaryWriter w = Session.Begin(MsgType.Ping3D);
            w.Write(StageParameters.DungeonLevel);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write(enemy);
            Session.SendBuiltToAll(true);
        }

        public static void OnPing(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool enemy = r.ReadBoolean();
            if (level != StageParameters.DungeonLevel) return;
            AddMarker(SteamFriends.GetFriendPersonaName(from), pos, enemy);
        }

        private static void AddMarker(string name, Vector3 pos, bool enemy)
        {
            markers.RemoveAll(m => m.Name == name); // one marker per player
            markers.Add(new Marker { Name = name, Pos = pos, Enemy = enemy, Until = Time.unscaledTime + 8f });
        }

        // ------------------------------------------------------------------ late join

        public static bool JoinTeleportPending;
        private static int requestedLevel = -1;

        /// <summary>Client: once per floor, ask the host which rooms are visited / cleared; after a late join, teleport next to the host.</summary>
        private static void TickFloorSetup()
        {
            if (!Session.IsClient) { requestedLevel = -1; return; }
            StageScript stage = StageScript.Instance;
            if (stage == null || LocalPlayerAccess.Get() == null || stage.StageState != StageScript.StageStates.Playing) return;

            if (requestedLevel != StageParameters.DungeonLevel)
            {
                requestedLevel = StageParameters.DungeonLevel;
                Session.Begin(MsgType.RoomStateRequest).Write(StageParameters.DungeonLevel);
                Session.SendBuiltToHost(true);
            }

            if (!JoinTeleportPending) return;
            Vector3? host = RemotePlayers.GetPosition(Session.HostId);
            if (!host.HasValue) return;
            JoinTeleportPending = false;
            Vector2 off = Random.insideUnitCircle.normalized * 1.5f;
            LocalPlayerAccess.Get().Controller.Teleport(host.Value + new Vector3(off.x, 0.2f, off.y));
            Plugin.Log.LogInfo("[join] Teleported next to the host");
        }

        /// <summary>Host: tell a (late) client which rooms of this floor are visited and cleared.</summary>
        public static void OnRoomStateRequest(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (!Session.IsHost || logic?.Rooms == null || level != StageParameters.DungeonLevel) return;
            int visited = 0, cleared = 0;
            foreach (RoomScript room in logic.Rooms)
            {
                if (room?.Room == null) continue;
                if (room.IsVisited)
                {
                    BinaryWriter w = Session.Begin(MsgType.RoomEnter);
                    w.Write(level); w.Write(room.Room.Id);
                    Session.SendBuiltTo(from, true);
                    visited++;
                }
                if (room.RoomState == RoomScript.RoomStates.Clear)
                {
                    BinaryWriter w = Session.Begin(MsgType.FightClear);
                    w.Write(level); w.Write(room.Room.Id);
                    Session.SendBuiltTo(from, true);
                    cleared++;
                }
            }
            Plugin.Log.LogInfo($"[host] Sent floor {level} state to {SteamFriends.GetFriendPersonaName(from)}: {visited} visited, {cleared} cleared rooms");
        }

        // ------------------------------------------------------------------ host leaving

        private static string notice;
        private static float noticeUntil;
        private static float soloFightWatch = -1f;

        /// <summary>Client: the host left. Leave the lobby (so nobody inherits a half-synced run) and play on solo.</summary>
        public static void OnHostLeft(string hostName)
        {
            bool inRun = LocalPlayerAccess.Get() != null;
            int released = 0;
            if (inRun)
            {
                foreach (Enemy e in Object.FindObjectsOfType<Enemy>())
                {
                    NetEnemy ne = e != null ? e.GetComponent<NetEnemy>() : null;
                    if (ne == null || !ne.IsPuppet) continue;
                    if (ne.IsSummonReplica) { Object.Destroy(e.gameObject); continue; }
                    ne.IsPuppet = false; // own AI again
                    if (e.State == Enemy.EnemyState.Spawning) e.EndSpawn();
                    released++;
                }
                soloFightWatch = Time.time;
            }
            notice = inRun ? $"{hostName} left: the run continues solo" : $"{hostName} left the lobby";
            noticeUntil = Time.unscaledTime + 8f;
            Plugin.Log.LogInfo($"[session] Host left ({hostName}); {released} enemies switched to local AI");
        }

        /// <summary>After the host left: end a local fight once its room has no living enemy left.</summary>
        private static void TickSoloFight()
        {
            if (soloFightWatch < 0f || Session.InLobby) return;
            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (logic == null) { soloFightWatch = -1f; return; }
            if (!logic.IsFighting) return;
            bool anyAlive = Object.FindObjectsOfType<Enemy>().Any(e => e != null && e.Team == Team.Enemy && e.IsAlive);
            if (anyAlive) { soloFightWatch = Time.time; return; }
            if (Time.time - soloFightWatch < 3f) return;
            Plugin.Log.LogInfo("[session] Solo: no enemy left, ending the current fight");
            logic.CurrentFight.EndFight();
            soloFightWatch = Time.time;
        }

        // ------------------------------------------------------------------ tick / HUD

        public static void Tick()
        {
            TickFloorSetup();
            TickSoloFight();
            markers.RemoveAll(m => Time.unscaledTime > m.Until);
        }

        private static GUIStyle label, big;

        public static void DrawHud()
        {
            if (label == null)
            {
                label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15 };
                big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 20, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };
            }

            if (notice != null && Time.unscaledTime < noticeUntil)
                GUI.Label(new Rect(0, Screen.height * 0.25f, Screen.width, 30), notice, big);

            if (spectating && spectatedName != null)
                GUI.Label(new Rect(0, Screen.height * 0.42f, Screen.width, 30), $"Spectating {spectatedName}  —  [{Plugin.SpectateNextKey.Value}] next ally", big);

            Camera cam = Camera.main;
            if (cam == null || markers.Count == 0) return;
            foreach (Marker m in markers)
            {
                Color c = m.Enemy ? new Color(1f, 0.35f, 0.3f) : new Color(0.4f, 1f, 0.6f);
                Vector3 sp = cam.WorldToScreenPoint(m.Pos);
                float dist = Vector3.Distance(cam.transform.position, m.Pos);
                if (sp.z > 0f && sp.x > 0 && sp.x < Screen.width && sp.y > 0 && sp.y < Screen.height)
                {
                    label.normal.textColor = c;
                    GUI.Label(new Rect(sp.x - 90f, Screen.height - sp.y - 34f, 180f, 22f), $"{(m.Enemy ? "⚔" : "▼")} {m.Name}", label);
                    GUI.Label(new Rect(sp.x - 90f, Screen.height - sp.y - 16f, 180f, 22f), $"{dist:0} m", label);
                }
                else UI.AllyHud.DrawOffscreen(cam, m.Pos, $"{(m.Enemy ? "⚔" : "▼")} {m.Name}", m.Enemy);
            }
        }

        public static void Reset()
        {
            StopSpectate();
            markers.Clear();
            JoinTeleportPending = false;
            requestedLevel = -1;
        }
    }
}
