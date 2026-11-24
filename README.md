# Last Guardian Standing

Four guardians, four goals, one survivor. The name nods to the Four Symbols, the guardian
beasts of the cardinal directions.

Four-way paddle ball, made with Godot 4.6 (.NET / C#). Square arena, one goal per side (N/E/S/W),
quarter-circle posts in the corners that can't be scored on. Balls drop in from the corners.

Status: local play (one keyboard + bots) and **online multiplayer** (host / join, up to 4 players).

## Run it

1. Open `Godot_v4.6.3-stable_mono_win64.exe`, choose **Import**, pick this folder's `project.godot`.
2. Press **F5** (or the ▶ button top-right).

Bot-only demo straight from a terminal:

```powershell
& "C:\Users\crist\Desktop\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe" --path . -- --demo
```

## Playing online

1. **Host:** type your name, click **Host online game** (port 7777 by default). Windows may ask to allow
   the game through the firewall the first time; allow it.
2. **Friends:** type their name and the host's address, click **Join game**.
3. In the lobby the host picks the settings and who defends which side (players, bots or empty),
   then clicks **Start match**. Players who disconnect are replaced by a bot.

Online, everyone sees **their own goal at the bottom** and plays with **← → or A / D**.
Host: `Esc` or `Space` (after a match) returns everyone to the lobby, `R` is a rematch.
Clients: `Esc` leaves.

Testing on one PC: run two copies, host in one, join `127.0.0.1` in the other.
Over the internet the host must be reachable: forward the port on the router, or use a
virtual LAN such as Tailscale and join with the host's Tailscale IP.

## Controls (local match)

| Side  | Keys            |
|-------|-----------------|
| North | W A S D         |
| East  | Numpad 8 4 5 6  |
| South | Arrow keys      |
| West  | I J K L         |

Keys are screen directions: North/South paddles use left/right, East/West use up/down.
Paddles slide along their goal line only. `Esc` returns to the menu.

## Command-line options (for testing)

Pass them after `--`, e.g. `Godot.exe --path . -- --host --name Cris --autostart 3`.

| Option | Effect |
|---|---|
| `--demo` | All-bot local chaos match |
| `--name N`, `--port P` | Pre-fill name / port |
| `--host` / `--join ADDRESS` | Host or join straight away |
| `--autostart S` | Host: start the match after S seconds |
| `--screenshot PATH`, `--screenshot-at S` | Save a screenshot after S seconds, then quit |

## Rules / modes

- **Teams**: free-for-all, 2v2, 1v1v2, 1v3. Team layout: side by side, facing, or random.
- **Lives**: personal (each player has their own) or shared per team. At zero, that goal becomes a wall.
  Empty slots are walls from the start.
- **Rounds**: last team standing takes the round; first to N rounds wins the match.
- **Balls**: single, or chaos (starts with 3, adds one every 3 s, up to the max, never more than 10).
- Where you hit the paddle picks the bounce angle; each hit speeds the ball up a little.

## Code layout

- `src/Sim/` — the whole game in plain C#, no Godot types. Deterministic given a seed.
  - `Simulation.cs` — rounds, spawning, physics, scoring
  - `Arena.cs` — geometry and tuning constants (sizes, speeds)
  - `MatchConfig.cs` — modes and team assignment
  - `BotBrain.cs` — bot AI
- `src/Net/` — online protocol, plain C#.
  - `GameState.cs` — one drawable moment of a match; binary snapshot format; `Protocol.Version`
  - `LobbyState.cs` — players, seats and settings, owned by the host
  - `SnapshotBuffer.cs` — client-side smoothing (draws ~67 ms behind the host)
- `src/Game/` — Godot front-end.
  - `Main.cs` — glue: local / host / client modes, input
  - `NetSession.cs` — ENet connection and RPCs
  - `ArenaView.cs` — draws a `GameState` (rotatable), HUD and effects
  - `MenuUI.cs` — main menu, local setup, online lobby
- `scenes/Main.tscn` — root scene (camera + glow environment).

### How online play works

The host runs the only real simulation. Each client sends where its own paddle is (60×/s);
the host's copy of that paddle chases it at a capped speed. The host sends snapshots of the whole
match (60×/s, under 1 KB each); clients draw them slightly in the past, blending between snapshots,
while their own paddle is drawn exactly where they steer it.
