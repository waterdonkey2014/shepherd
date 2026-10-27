# Porting another Unity game with Shepherd

Shepherd grew around one game, so some first-game assumptions are still wired in. This page lists the known ones and the order that
worked for the first port. Fixing a seam so it reads the value from the game's own data is a welcome contribution.

## Known first-game seams
| Where | What is wired in | Better |
|---|---|---|
| `engine/Shepherd.Engine/Icalls/AppIcalls.cs` | `Application.productName` / `companyName` return the first game's | Read them from the game's `globalgamemanagers` / `app.info` |
| `Render/Gfx.cs`, `Render/GlBackend.cs`, `Render/IRenderBackend.cs` | Window title | The product name |
| `Runtime/K8Video.cs`, `Runtime/K6Engine.cs`, `Diag/K7.cs`, `Icalls/VitaPad.cs` | The boot splash clip's name and length rules | A per-game list (or the first video the boot plays) |
| `Runtime/PlayClock.cs` | Starting time settings | Read the game's TimeManager (the engine already parses `globalgamemanagers`) |
| `Physics/PhysCheck*.cs`, `Physics/Composite.cs` | Physics checks run on the first game's scenes and settings | Per-game check lists |
| `engine/Shepherd.Assets/Shepherd.Assets.Cli/Program.cs`, `tools/VitaBudget`, `tools/FmodBake` | Default data paths / `COTL_DATA` | `SHEPHERD_GAME_DATA` everywhere |
| `vita/shepherd/` (TITLE_IDs, app names, splash rows, scripted pad routes in `vita/v6`, `vita/gameaot/pads`) | First-game test routes and ids | Per-game profiles |
| Scripted test routes (`Diag/K6Probe.cs`, `Diag/K7.cs`) | Menu labels of the first game | Routes as data files |

## The order that worked
1. **Mac first.** Get the game to its first scene in the Mac engine (`play.sh`). Work through `runtime/logs/missing-icalls.txt` and
   the engine log; the Mac is minutes per try, the Vita is a person and a memory card per try.
2. **Fidelity before speed.** Compare against the real game; keep a strict-original path for everything you add.
3. **Then the Vita.** Headless boot (logs only) -> GL -> input -> menus -> gameplay. Use vlx before every device run.
4. **Memory.** The Vita has ~200 MiB for everything after the eboot: trim the BCL, AOT what runs, watch fragmentation (the largest free
   block), and measure on the device.
5. **Write down what you learn** -- including what failed -- in the companion plugin's knowledge base.

## What your game will likely need that the first one didn't
- Icalls for engine features the first game never touched (3D physics simulation, terrain, other render pipelines).
- Shader replacements for its own shaders (GLSL ES 1.00 for vitaGL); keep ports of the game's shaders local (Derived).
- Middleware stand-ins (Wwise instead of FMOD, a different input library) -- the FMOD surface is the pattern to follow.
