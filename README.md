# Ziggurat MP — 2-4 player co-op mod for Ziggurat 2

Unofficial online co-op for **Ziggurat 2** (Milkstone Studios), built as a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin with Harmony patches. Players connect through **Steam lobbies and Steam P2P**: no server, no port forwarding.

> Fan-made and unaffiliated with Milkstone Studios. You need your own copy of the game. This repository contains only original mod code; no game code or assets.

## Features

- **Lobby:** host from the F8 menu, invite through Steam, or join a friend's lobby from F8.
- **Character select together:** everyone picks character and weapons on the game's own briefing screen. Each pick is shown to all players, and the run starts once everyone has picked.
- **Same world:** identical run and floors (mission seed plus per-floor generator inputs from the host), shared map exploration, and floor transitions follow the host. Late joiners catch up floor by floor.
- **Host-authoritative enemies:** the host runs the AI. Clients see synced puppets: position, health, status effects (burn, freeze…), boss phases and shields, and replayed attacks.
- **Combat for everyone:** enemies target every player. Client damage is sent to the host. Any player entering a combat room starts the fight.
- **Remote players:** real 3D character models with walk/run animation, the equipped weapon in hand, aim, recoil, dash and jump poses. Shots, beams, muzzle effects and sounds are replayed visually, with no double damage.
- **Downed and revive:** at 0 HP you go down instead of dying while an ally still stands. An ally who stays next to you for 3 seconds revives you; if everyone goes down, it's game over.
- **Sharing:** XP and quest items (boss keys, secret keys) are shared, while chests and loot stay per player. Press **G** while looking at a weapon or amulet on the ground to give it to the others.
- **Difficulty scaling:** enemy and boss health grows with each extra player (configurable).
- **HUD:** ally names with health bars, off-screen markers, allies on the minimap, and ping. Breakable props are synced.

## Install

1. Install **BepInEx 5.4.x (x64)** into the game folder (`steamapps/common/Ziggurat 2`) and run the game once.
2. Copy `ZigguratMP.dll` to `BepInEx/plugins/ZigguratMP/`.
3. Everyone in the group needs the **same mod version**; the lobby refuses mismatched versions.

## How to play

1. Host: press **F8**, then *Host co-op lobby*, then *Invite friends*. Friends accept the Steam invite, or use *Join <friend>* in their own F8 menu.
2. Everyone opens a mission briefing, picks a character and weapons, and presses **Start**. The run launches when all players are ready; the host can force it from F8.
3. Useful F8 options:
   - **Settings:** network rate, smoothing, health scaling.
   - **Unstick:** emergency buttons if a fight or boss phase hangs.

Config file: `BepInEx/config/com.zigguratmp.coop.cfg` (menu key, share key, network, difficulty).

## Building from source

Requirements: .NET SDK (any recent version) and the game installed. The project targets `net472` and references the game's own assemblies.

```
cd ZigguratMP
dotnet build -c Release
```

The build copies the DLL into `<GameDir>/BepInEx/plugins/ZigguratMP/`. `GameDir` defaults to the standard Steam path. Override it with `-p:GameDir="X:\...\Ziggurat 2"`, or with a git-ignored `ZigguratMP/GameDir.props.user`:

```xml
<Project><PropertyGroup><GameDir>X:\SteamLibrary\steamapps\common\Ziggurat 2</GameDir></PropertyGroup></Project>
```

Logs are in `BepInEx/LogOutput.log`; all mod lines are prefixed with `Ziggurat MP`.

## Architecture

```
ZigguratMP/
  Plugin.cs                    BepInEx entry point, config, Harmony, Update/OnGUI
  Net/Protocol.cs              message ids + binary serialization (bump Version on any change)
  Net/SteamP2PTransport.cs     SendP2PPacket / ReadP2PPacket, lobby members only
  Net/Session.cs               Steam lobby (owner = host), P2P mesh, dispatch
  Sync/ReadyRoom.cs            shared character select, synchronized run start
  Sync/RunSync.cs              run start, floors, late-join catch-up
  Sync/FightSync.cs            room fights end when the host says so
  Sync/RoomSync.cs             shared map, fights started by any player, boss safety net
  Sync/NetEnemies.cs           host enemies -> client puppets (pos, HP, status, boss stages)
  Sync/RemotePlayers.cs        other players: interpolation, 3D body, held weapon
  Sync/RemoteBodyAnimator.cs   locomotion (PlayableGraph), aim, recoil, dash, downed pose
  Sync/ShotSync.cs             visual replay of shots / beams (no damage)
  Sync/DownedSync.cs           downed / revive / team wipe
  Sync/SharedProgress.cs       shared XP and keys
  Sync/ItemShare.cs            give a ground item to the other players
  Sync/WorldSync.cs            breakable props
  UI/MpMenu.cs                 F8 menu
  UI/AllyHud.cs                ally health bars, off-screen markers, ping, friend lobbies
  UI/MinimapAllies.cs          allies on the minimap
  Patches/*                    Harmony hooks into the game
```

### Authority model

| What | Authority | Others |
|---|---|---|
| Map, mission | host | rebuilt from the host's generator inputs |
| Enemies (spawn, AI, HP, death) | host | puppets |
| Enemy attack choice | host | replayed locally |
| Damage taken by a player | each player | what you see is what hits you |
| Damage dealt to enemies | shooter | sent to the host |
| Room fight end, boss stages | host | mirrored |
| Loot | each player | local drops |
| XP, keys | shared | applied for everyone |
| Player shots | shooter | replayed visually, 0 damage |
| Player death | each player | downed while an ally stands |

## Status and roadmap

- [x] Lobby, same run and floors, host enemies, cross-player combat
- [x] Shared character select, shared XP and keys, 3D bodies and animations, shot replication
- [x] Downed/revive, boss phases and status effects on clients, difficulty scaling, ally HUD
- [ ] Traps sync, challenge and gauntlet modes validated in multiplayer
- [ ] Installer / auto-update

Bug reports are welcome: please attach `BepInEx/LogOutput.log` from **every** player.

## License

The mod's source code is released under the [MIT License](LICENSE).
The install zips on the Releases page also bundle [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1), which keeps its own license.
Ziggurat 2 and all of its code and assets belong to Milkstone Studios and are not part of this project.
