# Co-op roadmap

Ziggurat 2 is a single-player, first-person roguelite. Players clear procedurally generated floors (combat rooms locked until cleared, plus key, treasure, shop, shrine, fountain, forge, omen and challenge rooms) and kill the floor boss to open the portal. Along the way they level up and pick perks, find weapons and amulets, and manage mana pools. Runs come from a campaign (story and random missions, plus a meta skill tree) or from extra modes (Classic, Endless, Daily, Gauntlet, Linear).

Many systems assume **one player who can stop time**. This roadmap lists what has to change for 2-4 players, ordered by how much it breaks a co-op run today.

Legend: ✅ done · 🔶 implemented or partial, needs testing · ⬜ to do

## Already in place (0.7.x)

| System | Status |
|---|---|
| Lobby, invites, shared character select, synchronized run start | ✅ |
| Same mission, floors and layout (host generator inputs) | ✅ |
| Enemies host-authoritative, puppets on clients, replayed attacks | ✅ |
| Remote players: 3D body, animations, held weapon, shot replay | ✅ |
| Room fights started and cleared by any player | ✅ |
| Shared map exploration, ally markers | ✅ |
| Shared XP and keys; loot, chests, shops per player | ✅ |
| Downed / revive / team wipe | ✅ |
| Boss stages and enemy status effects mirrored | ✅ |
| Breakable props, item sharing (G), ally HUD, difficulty scaling (HP) | ✅ |

## P0 — breaks or desyncs a run

### 1. Time never stops in co-op 🔶 *(0.8.0, needs testing)*
The game sets `Time.timeScale = 0` for the pause menu, the **level-up perk screen**, the map, the **omen choice** and the character sheet (`StageScript.Pause(PauseReasons)`). In co-op:
- **Host pausing** freezes every enemy for everyone.
- **Client pausing** freezes only their own simulation, so their view drifts.

Plan:
- Keep time running while any menu is open.
- Make a player in a menu harder to hit (damage reduction), and show "in menu" on their name tag.
- Optional: a host-only "pause all" that freezes everyone together.

### 2. Fight start and allies left outside 🔶 *(0.8.0, needs testing)*
Starting a fight locks the room doors, so an ally still in the corridor is shut out (and on the host, `DungeonLogic` assumes the fight is in the host's current room).

Plan:
- When a fight starts, teleport allies who are outside into the room, near its entrance.
- Or keep doors passable for allies; behaviour configurable.

### 3. Boss room 🔶 *(0.8.0, needs testing)*
Only the host can trigger the boss cutscene today.

Plan:
- Any player entering the boss room starts it for everyone.
- Everyone is teleported into the arena and sees the same cutscene.
- The boss fight ends at the same time for all players.

### 4. Special fight objectives (host-authoritative) 🔶 *(0.8.0, needs testing)*
Some fights have objectives that live in each player's copy of the world, so they currently desync:
- **Obelisk:** break the obelisks (`Obelisk` is a damageable Character).
- **Overload:** items to activate.
- **Scavenge:** items to collect.
- **Sequence:** objects to interact with in order.
- **Combat challenge:** a chest reward.

Plan:
- Objectives belong to the host.
- Client hits and interactions are forwarded to the host, and their state is mirrored to everyone.

### 5. Floor exit / portal 🔶 *(0.8.0, needs testing)*
Today the host's portal pulls everyone.

Plan:
- Any player can take the portal.
- The floor ends when **all standing players** are in the portal, or after a countdown started by the first one, with a "waiting for X" display.
- The level-complete screen is synced.

### 6. End of run and campaign progress 🔶 *(0.8.0, needs testing)*
Each player saves their own campaign; a client runs the host's mission inside their own save.

Plan:
- Win and defeat are synced (the host's `CompleteMission` or game over triggers the same result for everyone).
- Each player gets their own mission rewards in their own save.
- Missions the client doesn't own (story missions) must not break or skip the client's own campaign.

### 7. Client combat effects applied on the host 🔶 *(0.8.0, needs testing)*
Client damage reaches the host; still to check or forward:
- elemental procs (burn, freeze…) caused by client weapons;
- knockback and pushes (Apprentice 2 skill), slows;
- enemy debuffs from skills (Vampire, Warlock…);
- crits and kill credit, so on-kill perks work for the client.

## P1 — fairness and balance

### 8. Player-team entities ⬜
These exist only on the owner's side today:
- **Necromancer** minions (spawned directly, outside `SpawnEnemy`);
- turrets (`TurretAmulet`, projectile turrets) and other summons.

Plan: replicate them as visual puppets, and make the host's enemies target and fight them.

### 9. Skills and amulets visuals ⬜
Remote players' active skills, amulet activations and their area effects should be visible: replay the visual effect, but apply its game effect only on the authority side (as for shots).

### 10. Run modifiers and omens ⬜
Run modifiers chosen at omen shrines, and campaign run modifiers, can change **enemies**. Those must come from the host for everyone.

Player-side modifiers stay personal. Decide per modifier tag (`RunModifierTags`).

### 11. Difficulty scaling 🔶
Enemy and boss health scale with player count. Still to do:
- optional extra enemies per wave;
- champion chance;
- per-mode presets;
- check that shared XP doesn't speed up leveling too much.

### 12. Economy decisions ⬜
Shops, shrines, fountains and the forge are per player (each player has their own copy of every room). Make this an explicit host setting:
- **per player:** current behaviour, generous;
- **shared:** using a fountain or shrine consumes it for everyone; shop stock is shared.

### 13. Leaderboards and Daily ⬜
Disable leaderboard submission for co-op runs (Daily, Endless, Classic), or tag them as co-op, so solo leaderboards stay fair.

## P2 — game modes

| Mode | What to adapt |
|---|---|
| Campaign (story / random) | end-of-run rewards (item 6), story missions the client doesn't own |
| Campaign Linear | entry door locking (`LockEntryDoor`) for all players |
| Gauntlet (campaign / classic) | arena teleports (`GauntletTeleportScript`) and wave rounds for everyone |
| Endless | floor-after-floor sync, scaling over time |
| Daily | same seed for all; leaderboard off (item 13) |
| Challenge rooms | platform challenge stays per player; combat challenge via item 4 |

## P3 — quality of life

- **Downed spectator:** a downed player can watch an ally (camera follow).
- **Ping marker:** a key to mark a spot or an enemy for the others.
- **Join in progress:** spawn next to the host instead of skipping floors one by one.
- **Host leaving:** clean end of session for clients (return to menu, keep progress); later, host migration.
- **Held weapon:** wrist and gauntlet weapons placed on the forearm instead of in the hand.
- **Installer / updater.**

## Technical notes

- **Host authority:** anything that changes enemies, objectives or shared state goes through the host. Visuals are replayed everywhere without game effect (see `ShotSync`, `ReplayDamageSource`).
- **Protocol version:** every wire change bumps `Protocol.Version`; mismatched lobbies are refused.
- **Hook points:**
  - pause: `StageScript.Pause(PauseReasons)`;
  - fight start: `RoomScript.StartFight`, `DungeonLogic.StartFight`;
  - boss: `StageScript.StartBossCutscene`;
  - portal and level end: `StageScript.LevelCleared`, `CompleteMission`;
  - summons: `Necromancer.Instance.OnSummonSpawned`, `TurretAmulet`;
  - special fights: `Logic.Fights.*`.
