using Steamworks;
using UnityEngine;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Game-side stand-in for another player. On the host it sits in DungeonLogic's enemy target
    /// list so AI chases and shoots it. Damage it receives is ignored: each player simulates the
    /// replicated enemy attacks locally and takes damage from what they actually see.
    /// </summary>
    internal sealed class RemotePlayerCharacter : Character
    {
        public CSteamID SteamId;
        public Vector3 NetVelocity;
        public bool Downed;

        public override bool IsAlive => isActiveAndEnabled;
        public override bool IsValidTarget => !Downed;
        public override Vector3 TargetPosition => transform.position + Vector3.up * 1.5f;
        public override Vector3 Velocity => NetVelocity;

        public override bool Hurt(Logic.DamageData damage) => true; // absorb (stops the bullet)
    }
}
