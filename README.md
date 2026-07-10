# Voxel Miner (Vulkan)

A 3D voxel mining game in C# on Vulkan (Silk.NET). Biome-driven procedural
terrain (oceans, rivers, plains, forests, deserts, ridged mountains) with
caves, depth-based ores, trees, vegetation, and villages; Minecraft-style
lighting with torches; flowing/connected water, swimming, and craftable
boats; survival and creative modes (health, fall damage, drowning, death /
instant breaking, infinite blocks, flight); mining and building with
Minecraft-style item drops you walk over to collect; tools with durability;
crafting; an inventory; and wandering animals with A* pathfinding.

## Run

```
dotnet run
```

`dotnet run -- --map` opens the standalone world-map explorer instead of the
game: a scrollable, zoomable 2D view of the whole generated world (drag to
pan, scroll or +/- to zoom between 1-8 blocks per pixel, Esc to quit). It
renders straight from the terrain generator — no chunks are generated — and
fills in progressively, with markers for villages and world spawn.

`dotnet run -- --autotest` runs a headed smoke test: it exercises crafting,
world edits, lighting, water flow/connectivity, and pathfinding, saves
screenshots (surface, torch-lit room, ocean, flowing water, a village, a
biome vista), and exits. Combine with `--map` to smoke-test the map explorer.

## Controls

WASD move, mouse look, Space jump/swim up, Shift sprint, scroll/1-9 hotbar,
LMB mine/punch, RMB place, E inventory + crafting, M world map, G switch
survival/creative, Esc pause.

## Game modes

Survival (default): 10 hearts, fall damage past 3 blocks, a 15-second air
supply with drowning damage, slow regeneration, and a death screen that
respawns you at spawn (inventory kept). Creative (press G): invulnerable,
instant block breaking (even bedrock) with no drops, placing consumes
nothing, the inventory panel offers an infinite palette of every block, and
double-tapping Space toggles flight (Space up, Shift down).

Boats (crafted from 5 planks): RMB on water with the boat selected to place,
RMB the boat to get in, W/S to row, A/D to steer, Space to hop out, punch
the boat to pick it back up.

## Structure

```
Program.cs               Entry point: launches the game or the --map explorer
MapViewer.cs             Standalone scrollable/zoomable world-map window
assets/ (build output)   Block atlas + inventory icon PNGs (torch/water tiles
                         and the torch icon are generated at load, TextureGen)
Core/
  Constants.cs           World dimensions, sea level, Block/Item ids
  Noise.cs               Deterministic hash + value-noise functions
World/
  BlockRegistry.cs       Data-driven block defs (solidity, opacity, emission)
  ItemRegistry.cs        Tool/item definitions and crafting recipes
  Biome.cs               Ocean/River/Beach/Plains/Forest/Desert/Mountains enum
  TerrainGenerator.cs    Height/biome model, caves, ores, trees, vegetation, villages
  GameWorld.cs           Chunk data store + edits; emits chunk-changed events
  LightEngine.cs         Sky + block light: BFS flood fill, incremental updates
  Fluids.cs              Water flow: instant falls, paced sideways spread
Engine/
  VulkanContext.cs       Instance/device/swapchain/frame loop
  GpuResources.cs        Buffers, meshes, textures
  Shaders.cs             GLSL sources compiled to SPIR-V at startup
  Pipelines.cs           World / line / HUD pipelines
  Renderer.cs            Frame API: meshes with push constants, HUD batches
  Assets.cs              PNG loading, icon/font atlases, procedural tiles
  ChunkMesher.cs         Chunk geometry: culling, AO, smooth light, water/torch/cross
  WorldRenderer.cs       Chunk mesh streaming + light-dirty remeshing
  ParticleSystem.cs      Block-break debris
Gameplay/
  Player.cs              First-person physics (AABB collision, swimming)
  Inventory.cs           Slot/stack model with cursor drag state + durability
  BlockInteraction.cs    Voxel raycast, mining with tools, placing
  Boats.cs               Rideable boats: buoyancy, steering, place/mount/pick up
  Drops.cs               Item drop entities: pop out, fall, magnetize, collect
Entities/                Animals: behaviour, spawning, A* pathfinding
UI/                      HUD (hotbar, stats, toasts, inventory panel),
                         animal and boat renderers, the 2D world map (MapView)
Audio/                   Tiny NAudio effect synthesis
```

## Lighting

Each chunk stores two 0-15 light channels per cell (`LightEngine`): sky light
(15 under open sky, free downward travel, -1 per sideways step) and block
light (torches emit 14, -1 per step; water absorbs extra). Edits update
incrementally with the classic two-queue BFS add/remove. The mesher bakes
smooth per-vertex light — averaging the four cells around each vertex — with
ambient occlusion, a directional face shade, and a warm tint for torch light.

## Water

Terrain below sea level (y=28) floods at generation. Water renders as a
separate translucent pass; each cell's height falls off with distance from
its source (0 = full, up to 7 = a thin shallow edge), except a cell with
water — or any solid block — directly above it, which is forced to full
height so columns and capped water stay visually connected. Surface heights
are averaged over the (up to 4) cells sharing a grid vertex, using absolute
world coordinates, so neighbouring quads always agree on that vertex's height
— no seams. The camera gets underwater fog, and the player swims (hold Space
to rise). Mining next to water wakes `Fluids`: a cell falls straight down
through open air immediately (gravity isn't paced), then spreads sideways
across a floor one BFS layer per tick — bounded to 7 steps, resetting on
every fall — so mining into the ocean floods nearby caves gradually instead
of all at once.

## Terrain and biomes

`TerrainGenerator` layers three noise bands into a height: a very
low-frequency "continental" value picks broad ocean/plains/mountain bands, a
ridge noise adds sharp peaks only where that band says "mountain", and small
high-frequency noise adds rolling detail everywhere. Rivers are the 0.5
iso-line of one more low-frequency noise band: within a narrow threshold of
that contour the terrain is carved below sea level (soft-blended banks, full
depth at the center) so the channel floods at generation, wanders naturally,
and drains into whatever ocean the contour reaches. A separate
temperature/humidity pair (independent of height) picks Desert, Forest, or
Plains once a column is confirmed dry land; steep columns (a big height
jump to a neighbour) expose Stone instead of Grass/Dirt, and Mountains are
rocky throughout. Trees are denser in Forest, sparse in Plains, and absent
elsewhere; tall grass and flowers scatter on flat Plains/Forest grass tiles
as billboarded cross-quads (alpha-cutout, lit like any other block).

Villages sit on a coarse world-space grid (mirroring how trees already
straddle chunk borders deterministically): a cell qualifies if it rolls
below a threshold, lands on flat Plains, and isn't steep, then a well plus
four houses are stamped around that anchor. `TerrainGenerator.FindNearestVillage`
locates one without generating chunks, for tooling/tests.

## World map

M opens a top-down 2D map (384x384 blocks, north up), separate from the 3D
scene. It's rebuilt into a texture each time it opens: columns in loaded
chunks show their actual top block — player builds, trees, village houses —
while everything ungenerated falls back to the terrain generator's biome
colors, drawn dimmer as "unexplored". Terrain is shaded by height and water
by depth; markers show the player (red, with a heading dot) and any village
anchors in range.

### Design notes

- **Registries are data.** New blocks, tools, and recipes are added by
  editing `BlockRegistry.cs` / `ItemRegistry.cs` — no logic changes needed.
- **The world knows nothing about rendering.** `GameWorld` stores voxels and
  emits chunk-changed events; `WorldRenderer` subscribes and rebuilds meshes;
  `LightEngine` likewise reports light-stale chunks.
- **Dependencies are injected.** Subsystems receive collaborators through
  constructors; `Program.cs` is the only place that knows the whole graph.
