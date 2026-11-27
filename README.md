# Ziggurat MP — 2-4 player co-op mod for Ziggurat 2

Unofficial online co-op for **Ziggurat 2** (Milkstone Studios), built as a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin with Harmony patches. Players connect through **Steam lobbies and Steam P2P**: no server, no port forwarding.

> Fan-made and unaffiliated with Milkstone Studios. You need your own copy of the game. This repository contains only original mod code; no game code or assets.

## Features

- **Lobby:** host from the F8 menu, invite through Steam, or join a friend's lobby from F8.
- **Character select together:** when the host opens a mission briefing, the same screen opens for every client. Everyone picks character and weapons there, each pick is shown to all players, and the run starts once everyone has picked.
- **Same world:** identical run and floors (mission seed plus per-floor generator inputs from the host), shared map exploration, and floor transitions follow the host. Late joiners catch up floor by floor.
- **Host-authoritative enemies:** the host runs the AI. Clients see synced puppets: position, health, status effects (burn, freeze…), boss phases and shields, and replayed attacks.
- **Combat for everyone:** enemies target every player. Client damage is sent to the host. Any player entering a combat room starts the fight.
- **Remote players:** real 3D character models with walk/run animation, the equipped weapon in hand, aim, recoil, dash and jump poses. Shots, beams, muzzle effects and sounds are replayed visually, with no double damage.
- **Downed and revive:** at 0 HP you go down instead of dying while an ally still stands. An ally who stays next to you for 3 seconds revives you; if everyone goes down, it's game over.
- **Sharing:** XP and quest items (boss keys, secret keys) are shared, while chests and loot stay per player. Press **G** while looking at a weapon or amulet on the ground to give it to the others.
- **Co-op rules for a single-player game:** menus (level-up perks, map, omens, pause) no longer stop time in co-op; you take half damage while in one, and others see [MENU] above you. Optionally (F8 > Settings, off by default), players outside a room are teleported in when its fight or the boss starts. The exit portal waits for everyone (or a countdown). Special fight objectives (obelisks, sequences, scavenge, overload) are shared.
- **Difficulty scaling:** each extra player adds enemy and boss health, bigger waves and more champions (all configurable by the host).
- **Allies' summons and skills:** Necromancer minions and other player-team summons are visible to everyone and enemies can target them. Amulet turrets and skill explosions are shown too, and omens that change enemies apply for everyone.
- **Economy:** per player by default; the host can make fountains, shrines and the shop shared. Co-op runs are never sent to leaderboards.
- **HUD:** ally names with health bars, off-screen markers, allies on the minimap, and ping. Breakable props are synced.
- **Quality of life:**
  - **Ping marker:** middle click marks the spot or enemy you look at.
  - **Downed spectator camera:** follow an ally while you wait for a revive.
  - **Late joiners** start next to the host on the current floor.
  - **Host leaving:** if the host leaves mid-run, the others keep playing solo.

## Install

Every player in the group must do this, with the **same mod version**; the lobby refuses mismatched versions.

### Prerequisites

- **Ziggurat 2** on Steam (Windows). Co-op uses Steam lobbies and Steam P2P, so all players need Steam running.
- **[BepInEx 5](https://github.com/BepInEx/BepInEx/releases), 64-bit Windows build**: the file named `BepInEx_win_x64_5.4.x.zip` (5.4.23 or newer).
  - Do **not** use BepInEx 6 or the x86 build: the game is 64-bit Unity Mono, and the mod is built for BepInEx 5.

### 1. Install BepInEx (once)

1. Find the game folder: in Steam, right-click **Ziggurat 2**, then *Manage*, then *Browse local files*. It's usually `C:\Program Files (x86)\Steam\steamapps\common\Ziggurat 2`.
2. Extract the contents of `BepInEx_win_x64_5.4.x.zip` **directly into that folder**, so that `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder sit next to `Ziggurat2.exe`.
3. Start the game once, go to the main menu, then quit. BepInEx creates its folders (`BepInEx/plugins`, `BepInEx/config`, `BepInEx/LogOutput.log`).
   - If `BepInEx/LogOutput.log` doesn't exist afterwards, BepInEx isn't loading. Check that `winhttp.dll` is next to `Ziggurat2.exe`.

### 2. Install the mod

1. Download `ZigguratMP_vX.Y.Z.zip` from the [Releases](../../releases) page.
2. Extract it into the same game folder. It only contains `BepInEx/plugins/ZigguratMP/ZigguratMP.dll`.
3. Start the game. `BepInEx/LogOutput.log` should contain `Ziggurat MP X.Y.Z loaded`, and **F8** opens the multiplayer menu.

### Update / uninstall

- **Update:** extract the new release over the old one (it replaces `ZigguratMP.dll`).
- **Remove the mod:** delete `BepInEx/plugins/ZigguratMP`.
- **Remove all mods:** delete `winhttp.dll` (or the whole `BepInEx` folder, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version`).

## How to play

1. Host: press **F8**, then *Host co-op lobby*, then *Invite friends*. Friends accept the Steam invite, or use *Join <friend>* in their own F8 menu.
2. The host opens a mission briefing; the same briefing opens automatically for the clients. Everyone picks a character and weapons and presses **Start**. The run launches when all players are ready; the host can force it from F8.
3. Useful F8 options:
   - **Settings:** teleport into allies' fights, portal wait time, shared economy, difficulty scaling, network rate, smoothing.
   - **Unstick:** emergency buttons if a fight or boss phase hangs.

Config file: `BepInEx/config/com.zigguratmp.coop.cfg` (menu / share / ping / spectate keys, network, difficulty, fight teleport, portal countdown, forearm weapons).

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

Working today: lobby and shared character select; same run, floors and map; host-authoritative enemies; 3D allies with animations and replicated shots; downed/revive; boss stages and status effects mirrored; shared XP and keys; ally HUD.

The detailed co-op roadmap is in **[ROADMAP.md](ROADMAP.md)**: what in Ziggurat 2's single-player design has to change for co-op (pauses that stop time, locked fight rooms, boss trigger, special fight objectives, portal, campaign rewards, summons, game modes).

Bug reports are welcome: please attach `BepInEx/LogOutput.log` from **every** player.

## License

The mod's source code is released under the [MIT License](LICENSE).
[BepInEx](https://github.com/BepInEx/BepInEx) is a separate prerequisite (not bundled) under its own license.
Ziggurat 2 and all of its code and assets belong to Milkstone Studios and are not part of this project.
