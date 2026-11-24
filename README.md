# Last Guardian Standing

Four guardians, four goals, one survivor. The name nods to the Four Symbols, the guardian
beasts of the cardinal directions.

Four-way paddle ball, made with Godot 4.6 (.NET / C#). Square arena, one goal per side (N/E/S/W),
quarter-circle posts in the corners that can't be scored on. Balls drop in from the corners.

Status: **local prototype** (one keyboard + bots). Online multiplayer is next.

## Run it

1. Open `Godot_v4.6.3-stable_mono_win64.exe`, choose **Import**, pick this folder's `project.godot`.
2. Press **F5** (or the ▶ button top-right).

Bot-only demo straight from a terminal:

```powershell
& "C:\Users\crist\Desktop\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe" --path . -- --demo
```

## Controls

| Side  | Keys            | Notes                                   |
|-------|-----------------|-----------------------------------------|
| North | W A S D         |                                         |
| East  | Numpad 8 4 5 6  |                                         |
| South | Arrow keys      |                                         |
| West  | I J K L         |                                         |

Keys are screen directions: North/South paddles use left/right, East/West use up/down.
Paddles slide along their goal line only. `Esc` returns to the menu.

## Rules / modes

- **Teams**: free-for-all, 2v2, 1v1v2, 1v3. Team layout: side by side, facing, or random.
- **Lives**: personal (each player has their own) or shared per team. At zero, that goal becomes a wall.
  Empty slots are walls from the start.
- **Rounds**: last team standing takes the round; first to N rounds wins the match.
- **Balls**: single, or chaos (starts with 3, adds one every 3 s, up to the max, never more than 10).
- Where you hit the paddle picks the bounce angle; each hit speeds the ball up a little.

## Code layout

- `src/Sim/` — the whole game in plain C#, no Godot types. Deterministic given a seed.
  Online, the host will run this and clients will just send intents / draw snapshots.
  - `Simulation.cs` — rounds, spawning, physics, scoring
  - `Arena.cs` — geometry and tuning constants (sizes, speeds)
  - `MatchConfig.cs` — modes and team assignment
  - `BotBrain.cs` — bot AI
- `src/Game/Main.cs` — Godot front-end: menu, keyboard input, drawing, effects.
- `scenes/Main.tscn` — root scene (camera + glow environment).
