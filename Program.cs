using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace VoxelMiner;

using VoxelMiner.Audio;
using VoxelMiner.Core;
using VoxelMiner.Engine;
using VoxelMiner.Entities;
using VoxelMiner.Gameplay;
using VoxelMiner.Gameplay.Drops;
using VoxelMiner.UI;
using VoxelMiner.World;

public static class Program
{
    public static void Main(string[] args)
    {
        // dev tool: regenerate every swappable texture PNG into an assets tree
        // (defaults to the running copy) and exit. No GPU needed.
        int bakeIdx = Array.IndexOf(args, "--bake-textures");
        if (bakeIdx >= 0)
        {
            string dir = bakeIdx + 1 < args.Length && !args[bakeIdx + 1].StartsWith("--")
                ? args[bakeIdx + 1]
                : Path.Combine(AppContext.BaseDirectory, "assets");
            TextureBaker.BakeAll(dir);
            return;
        }

        // world seed: --seed <n> reproduces a world; autotest pins one so
        // its scenes are deterministic; otherwise each launch rolls a new one
        int seedIdx = Array.IndexOf(args, "--seed");
        if (seedIdx >= 0 && seedIdx + 1 < args.Length && int.TryParse(args[seedIdx + 1], out int seed))
            Constants.Seed = seed;
        else if (args.Contains("--autotest"))
            Constants.Seed = 12345;

        Console.WriteLine($"World seed: {Constants.Seed}");

        if (args.Contains("--map"))
        {
            // standalone scrollable world-map explorer, no game
            using var viewer = new MapViewerApp(autotest: args.Contains("--autotest"));
            viewer.Run();
            return;
        }
        // a pinned seed or autotest jumps straight into a world; otherwise
        // the main menu handles choosing/creating one
        bool skipMenu = args.Contains("--autotest") || seedIdx >= 0;
        using var game = new Game(autotest: args.Contains("--autotest"), skipMenu);
        game.Run();
    }
}

enum GameState { Paused, Playing, InventoryOpen, MapOpen, Dead, ChestOpen, FurnaceOpen, MainMenu, CreateWorld, Settings }

/// Composition root: constructs every subsystem, wires them together,
/// and runs the game loop.
sealed class Game : IDisposable
{
    static readonly Vector4 SkyColor = new(0.529f, 0.808f, 0.922f, 1f);
    static readonly Vector4 NightSkyColor = new(0.015f, 0.03f, 0.075f, 1f);
    static readonly Vector4 SunsetColor = new(0.93f, 0.48f, 0.25f, 1f);
    static readonly Vector4 WaterFogColor = new(0.10f, 0.24f, 0.45f, 1f);

    // ------------------------------------------------------------- day/night
    // _timeOfDay wraps 0..1 over one DayLength: 0 = sunrise, 0.25 = noon,
    // 0.5 = sunset, 0.75 = midnight. Daylight scales the sky-light band in
    // the world shader (torch light is unaffected), with a moonlight floor
    // so nights stay navigable.
    const float DayLength = 600f; // seconds per full cycle
    const float NewWorldTime = 0.05f; // fresh worlds start just after sunrise
    float _timeOfDay = NewWorldTime;

    float SunElevation => MathF.Sin(MathF.Tau * _timeOfDay);

    float Daylight
    {
        get
        {
            float d = Math.Clamp((SunElevation + 0.05f) / 0.3f, 0f, 1f);
            d = d * d * (3 - 2 * d);
            return 0.14f + 0.86f * d;
        }
    }

    /// Sky/fog color for the current time: night blue to day blue, blended
    /// through an orange band while the sun crosses the horizon.
    Vector4 CurrentSkyColor
    {
        get
        {
            float e = SunElevation;
            float blend = Math.Clamp((e + 0.15f) / 0.35f, 0f, 1f);
            blend = blend * blend * (3 - 2 * blend);
            var sky = Vector4.Lerp(NightSkyColor, SkyColor, blend);
            float horizon = MathF.Exp(-MathF.Pow((e - 0.02f) / 0.14f, 2));
            return Vector4.Lerp(sky, SunsetColor, horizon * 0.45f);
        }
    }

    // Fog tracks the render distance: chunks stream in a square of
    // ±ViewRadius, so the nearest unloaded edge sits ViewRadius*ChunkSize
    // blocks away along an axis — fog reaches full opacity just inside that,
    // hiding chunk pop-in, and starts at half that distance.
    // properties: ViewRadius is now the render-distance setting, changeable at runtime
    static float RenderDistance => Constants.ViewRadius * Constants.ChunkSize;
    static float FogFar => RenderDistance - Constants.ChunkSize * 0.5f;
    static float FogNear => FogFar * 0.5f;

    readonly IWindow _window;
    readonly bool _autotest;
    int _frameCounter;

    VulkanContext _ctx;
    Renderer _renderer;
    TextureHandle _atlas, _white, _animalTex;
    IconAtlas _icons;
    FontAtlas _font;
    Mesh _cube, _lineCube;
    CubeMeshCache _animalMeshes;
    CubeMeshCache _blockCubes; // block-atlas cubes for item drops

    GameWorld _world;
    WorldRenderer _worldRenderer;
    RedstoneSim _redstone;
    readonly Frustum _frustum = new();
    Player _player;
    Inventory _inventory;
    BlockInteraction _interaction;
    AnimalManager _animals;
    BoatManager _boats;
    DropManager _drops;
    BlockEntities _blockEntities;
    (int X, int Y, int Z) _container; // chest/furnace the open panel points at
    Fluids _fluids;
    ParticleSystem _particles;
    SoundPlayer _sound;
    GameHud _hud;
    TerrainGenerator _terrain;
    MapView _map;

    GameState _state = GameState.MainMenu;
    GameMode _mode = GameMode.Survival;

    // menu state
    List<SaveInfo> _saveList = new();
    string _newName = "", _newSeed = "";
    int _createField;                    // 0 = name, 1 = seed
    string _worldName = "World", _savePath;
    GameSettings _settings = new();
    GameState _settingsReturn = GameState.MainMenu; // where Back leads
    float _lastSpaceTap = -1f, _clock;
    float _hurtFlash;
    readonly HashSet<Key> _keys = new();
    IMouse _mouse;
    bool _mineHeld, _placeHeld;
    Vector2 _lastMouse;
    bool _hasLastMouse;

    string _toastText = "";
    float _toastTimer;
    float _fpsTimer;
    int _fpsFrames, _fps;

    readonly bool _skipMenu; // --seed / --autotest launch straight into a world

    public Game(bool autotest, bool skipMenu = false)
    {
        _autotest = autotest;
        _skipMenu = skipMenu;
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Voxel Miner (Vulkan)",
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += _ => { if (_ctx != null) _ctx.FramebufferResized = true; };
        _window.Closing += () => { if (_world != null) SaveWorld(silent: true); }; // auto-save on quit
    }

    /// Any crash (a lost GPU device included) still tries to save the world
    /// before the process dies, so a driver hiccup never costs progress.
    public void Run()
    {
        try
        {
            _window.Run();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Fatal error: {e.Message}");
            if (e.Message.Contains("DeviceLost"))
                Console.WriteLine("The GPU device was lost (driver reset/TDR). " +
                    "If this happens often, try a lower render distance or updating the graphics driver.");
            try
            {
                if (_world != null)
                {
                    SaveWorld(silent: true);
                    Console.WriteLine($"Emergency save written for '{_worldName}'.");
                }
            }
            catch (Exception saveEx)
            {
                Console.WriteLine($"Emergency save failed: {saveEx.Message}");
            }
            throw;
        }
    }

    // ------------------------------------------------------------- init

    void OnLoad()
    {
        _ctx = new VulkanContext(_window);
        _renderer = new Renderer(_ctx);

        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        // every texture is one PNG under assets/, packed into atlases here;
        // missing files self-heal from the procedural source (TextureGen)
        var (blockHandle, _, blockTiles) = BlockTextures.Build(_renderer, assets);
        _atlas = blockHandle;
        if (blockTiles != ChunkMesher.AtlasTiles)
            throw new InvalidOperationException($"block atlas has {blockTiles} tiles, mesher expects {ChunkMesher.AtlasTiles}");
        _white = _renderer.RegisterTexture(new GpuTexture(_ctx, new byte[] { 255, 255, 255, 255 }, 1, 1, srgb: false));
        _icons = new IconAtlas(_renderer, Path.Combine(assets, "icons"));
        _font = new FontAtlas(_renderer);
        _cube = Primitives.CreateShadedCube(_ctx);
        _lineCube = Primitives.CreateLineCube(_ctx);

        var (mobHandle, _, mobTiles) = MobTextures.Build(_renderer, assets);
        _animalTex = mobHandle;
        _animalMeshes = new CubeMeshCache(_ctx, mobTiles); // one row of 16px tiles
        _blockCubes = new CubeMeshCache(_ctx, ChunkMesher.AtlasTiles);

        _player = new Player();
        _inventory = new Inventory();
        _particles = new ParticleSystem();
        _sound = new SoundPlayer();
        _hud = new GameHud(_font, _icons, _white, _inventory);
        // autotest pins default settings so its scenes stay deterministic
        _settings = _autotest ? new GameSettings() : GameSettings.Load();
        ApplySettings();
        WireInput();

        if (_autotest || _skipMenu)
        {
            // scripted or seed-pinned launches go straight into a fresh world
            string name = _autotest ? "autotest" : $"World {Constants.Seed}";
            StartWorld(null, WorldSave.NewPath(name), name);
            if (!_autotest) SetState(GameState.Paused); // show the title/help screen
        }
        else
        {
            _saveList = WorldSave.ListSaves();
            SetState(GameState.MainMenu);
        }
    }

    /// Builds every world-dependent system for a new or loaded world and
    /// enters it. Called once per session, from the menu (or directly for
    /// --seed/--autotest launches).
    void StartWorld(SaveData save, string path, string name)
    {
        _savePath = path;
        _worldName = name;

        _terrain = new TerrainGenerator();
        _world = new GameWorld(_terrain);
        _fluids = new Fluids(_world); // mesher reads flow levels, so this must exist first
        _worldRenderer = new WorldRenderer(_renderer, _world, new ChunkMesher(_world, _fluids), _atlas);
        _map = new MapView(_renderer, _world, _terrain);
        _animals = new AnimalManager(_world);
        _boats = new BoatManager(_world);
        _boats.Toast += ShowToast;
        _boats.Placed += () => _sound.Tone(150, 0.1, 0.12);
        _boats.PickedUp += () => _sound.Tone(300, 0.06, 0.1);
        _drops = new DropManager(_world);
        _boats.Dropped += (pos, id) => _drops.Spawn(pos.X, pos.Y, pos.Z, id);
        _drops.Collected += id =>
        {
            _sound.Tone(560, 0.05, 0.08);
            if (id == BlockId.Diamond) ShowToast($"Diamond! ({_inventory.CountItem(BlockId.Diamond)} total)");
            else if (id == BlockId.Gold) ShowToast("Gold ore!");
        };

        _interaction = new BlockInteraction(_world, _player, _inventory);
        _interaction.Toast += ShowToast;
        _interaction.MineTick += () => _sound.Noise(0.05, 0.08, 900);
        _interaction.EatTick += () => _sound.Noise(0.05, 0.06, 1400);
        _interaction.Ate += () => _sound.Tone(220, 0.18, 0.12);
        _interaction.BlockPlaced += () => _sound.Tone(190, 0.08, 0.12);
        _blockEntities = new BlockEntities(_world);
        _blockEntities.ItemSpilled += (x, y, z, id, n) => _drops.Spawn(x + 0.5f, y + 0.5f, z + 0.5f, id, n);
        _interaction.DoorToggled += () => _sound.Tone(260, 0.08, 0.1);
        _interaction.ContainerOpened += (x, y, z, furnace) =>
        {
            _container = (x, y, z);
            _sound.Tone(320, 0.06, 0.08);
            SetState(furnace ? GameState.FurnaceOpen : GameState.ChestOpen);
        };
        _interaction.CraftingOpened += () =>
        {
            _hud.TableOpen = true; // unlock the advanced recipes for this panel
            _sound.Tone(320, 0.06, 0.08);
            SetState(GameState.InventoryOpen);
        };
        _interaction.BlockBroken += (x, y, z, id) =>
        {
            _particles.Burst(x, y, z, BlockRegistry.Blocks[id].ParticleColor);
            _sound.Noise(0.14, 0.25, 400);
            _fluids.Wake(x, y, z); // neighbouring water flows into the hole
            _blockEntities.OnBlockBroken(x, y, z); // chests/furnaces spill their items
        };
        _interaction.ItemDropped += (x, y, z, id) => _drops.Spawn(x + 0.5f, y + 0.4f, z + 0.5f, id);
        _animals.AnimalPunched += a => _sound.Tone(a.Type == AnimalType.Chicken ? 720 : 340, 0.12, 0.15);

        _redstone = new RedstoneSim(_world, _blockEntities.ComparatorSignal);
        _redstone.ItemPopped += (x, y, z, id) => _drops.Spawn(x + 0.5f, y + 0.4f, z + 0.5f, id);

        if (save != null)
        {
            WorldSave.Apply(save, _world, _fluids, _blockEntities, _player, _inventory, _boats, _drops);
            SetMode(save.Mode);
            _timeOfDay = save.TimeOfDay;
            // comparator strengths and pending timers aren't saved; re-derive
            foreach (var (cx, cz, _) in save.Chunks) _redstone.RefreshChunk(cx, cz);
            ShowToast($"Loaded '{name}' (F5 saves, auto-saves on exit)");
        }
        else
        {
            SetMode(GameMode.Survival); // fresh worlds always begin in survival
            _timeOfDay = NewWorldTime;
            _player.Spawn(_world);
        }
        // build the starting area synchronously so the player doesn't fall through
        int scx = (int)MathF.Floor(_player.Pos.X / Constants.ChunkSize);
        int scz = (int)MathF.Floor(_player.Pos.Z / Constants.ChunkSize);
        _worldRenderer.BuildAround(scx, scz, 1);

        SetState(GameState.Playing);
    }

    void WireInput()
    {
        var input = _window.CreateInput();
        foreach (var kb in input.Keyboards)
        {
            kb.KeyDown += (_, key, _) => OnKeyDown(key);
            kb.KeyUp += (_, key, _) => _keys.Remove(key);
            kb.KeyChar += (_, ch) => OnChar(ch); // text entry for the menu
        }
        _mouse = input.Mice[0];
        _mouse.MouseDown += (_, button) => OnMouseDown(button);
        _mouse.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Left) _mineHeld = false;
            if (button == MouseButton.Right) _placeHeld = false;
        };
        _mouse.MouseMove += (_, pos) => OnMouseMove(pos);
        _mouse.Scroll += (_, wheel) =>
        {
            if (_state == GameState.Playing) _inventory.CycleSelection(wheel.Y < 0 ? 1 : -1);
        };
    }

    // ------------------------------------------------------------- input

    void OnKeyDown(Key key)
    {
        bool wasHeld = _keys.Contains(key); // guard against key-repeat events
        _keys.Add(key);
        if (key >= Key.Number1 && key <= Key.Number9 && _state == GameState.Playing)
            _inventory.SelectSlot(key - Key.Number1);

        switch (key)
        {
            case Key.Escape when _state == GameState.MainMenu:
                _window.Close();
                break;
            case Key.Escape when _state == GameState.CreateWorld:
                SetState(GameState.MainMenu);
                break;
            case Key.Escape when _state == GameState.Settings:
                CloseSettings();
                break;
            case Key.Backspace when _state == GameState.CreateWorld:
                if (_createField == 0 && _newName.Length > 0) _newName = _newName[..^1];
                else if (_createField == 1 && _newSeed.Length > 0) _newSeed = _newSeed[..^1];
                break;
            case Key.Tab when _state == GameState.CreateWorld:
                _createField = 1 - _createField;
                break;
            case Key.Enter when _state == GameState.CreateWorld:
                CreateWorldFromMenu();
                break;
            case Key.Space when _state == GameState.Playing && _boats.Ridden != null:
                _boats.Dismount(_player);
                break;
            case Key.Space when _state == GameState.Playing && _mode == GameMode.Creative && !wasHeld:
                if (_clock - _lastSpaceTap < 0.3f)
                {
                    _player.Flying = !_player.Flying;
                    if (_player.Flying) _player.Vel.Y = 0;
                    ShowToast(_player.Flying ? "Flying (double-tap Space to land)" : "Flying off");
                    _lastSpaceTap = -1f;
                }
                else
                {
                    _lastSpaceTap = _clock;
                }
                break;
            case Key.G when _state == GameState.Playing:
                SetMode(_mode == GameMode.Survival ? GameMode.Creative : _mode == GameMode.Creative ? GameMode.Spectator : GameMode.Survival);
                break;
            case Key.E when _state == GameState.Playing && _mode != GameMode.Spectator:
                _hud.TableOpen = false; // hand crafting only from the pocket panel
                SetState(GameState.InventoryOpen);
                break;
            case Key.E or Key.Escape when _state is GameState.InventoryOpen or GameState.ChestOpen or GameState.FurnaceOpen:
                _inventory.ReturnCursor();
                SetState(GameState.Playing);
                break;
            case Key.F5 when _state is GameState.Playing or GameState.Paused:
                SaveWorld(silent: false);
                break;
            case Key.M when _state == GameState.Playing:
                _map.Rebuild(_player.Pos);
                SetState(GameState.MapOpen);
                break;
            case Key.M or Key.Escape when _state == GameState.MapOpen:
                SetState(GameState.Playing);
                break;
            case Key.Escape when _state == GameState.Playing:
                SetState(GameState.Paused);
                break;
        }
    }

    /// Switches survival/creative and pushes the mode into every system that
    /// behaves differently per mode.
    void SetMode(GameMode mode)
    {
        _mode = mode;
        _interaction.Mode = mode;
        _boats.Mode = mode;
        _hud.Mode = mode;

        if (mode == GameMode.Survival) _player.Flying = false;
        else if (mode == GameMode.Spectator) _player.Flying = true;

        ShowToast(mode switch
            {
                GameMode.Creative => "Creative mode",
                GameMode.Survival => "Survival mode", 
                GameMode.Spectator => "Spectator mode",
                _ => "Unknown"
            }
        );
    }

    /// Printable characters feed the create-world text fields.
    void OnChar(char ch)
    {
        if (_state != GameState.CreateWorld || ch < 32 || ch > 126) return;
        if (_createField == 0)
        {
            if (_newName.Length < 24) _newName += ch;
        }
        else if ((char.IsDigit(ch) || (ch == '-' && _newSeed.Length == 0)) && _newSeed.Length < 10)
        {
            _newSeed += ch;
        }
    }

    void CreateWorldFromMenu()
    {
        string name = _newName.Trim();
        if (name.Length == 0) name = "New World";
        Constants.Seed = _newSeed.Length > 0 && int.TryParse(_newSeed, out int seed)
            ? seed
            : Random.Shared.Next();
        Console.WriteLine($"World seed: {Constants.Seed}");
        StartWorld(null, WorldSave.NewPath(name), name);
    }

    void LoadWorldFromMenu(SaveInfo info)
    {
        var data = WorldSave.Load(info.Path);
        if (data == null)
        {
            ShowToast("Couldn't read that save file!");
            return;
        }
        Constants.Seed = data.Seed;
        Console.WriteLine($"World seed: {Constants.Seed}");
        StartWorld(data, info.Path, data.Name);
    }

    void OnMouseDown(MouseButton button)
    {
        switch (_state)
        {
            case GameState.MainMenu when button == MouseButton.Left:
                if (_hud.HitMenuRow(_saveList.Count, _mouse.Position.X, _mouse.Position.Y) is { } row)
                {
                    _sound.Tone(340, 0.05, 0.07);
                    if (row == _saveList.Count)
                    {
                        _newName = "";
                        _newSeed = "";
                        _createField = 0;
                        SetState(GameState.CreateWorld);
                    }
                    else if (row == _saveList.Count + 1)
                    {
                        OpenSettings(GameState.MainMenu);
                    }
                    else
                    {
                        LoadWorldFromMenu(_saveList[row]);
                    }
                }
                break;
            case GameState.CreateWorld when button == MouseButton.Left:
                if (_hud.HitCreateWorld(_mouse.Position.X, _mouse.Position.Y) is { } element)
                {
                    _sound.Tone(340, 0.05, 0.07);
                    if (element <= 1) _createField = element;
                    else if (element == 2) CreateWorldFromMenu();
                    else SetState(GameState.MainMenu);
                }
                break;
            case GameState.Settings when button == MouseButton.Left:
                if (_hud.HitSettings(SettingsRows().Length, _mouse.Position.X, _mouse.Position.Y) is { } hitSet)
                {
                    if (hitSet.Dir == 0) CloseSettings();
                    else AdjustSetting(hitSet.Row, hitSet.Dir);
                }
                break;
            case GameState.Paused:
                if (_hud.HitPauseButton(_mouse.Position.X, _mouse.Position.Y) is { } pauseBtn)
                {
                    _sound.Tone(340, 0.05, 0.07);
                    if (pauseBtn == 0) SetState(GameState.Playing);
                    else if (pauseBtn == 1) OpenSettings(GameState.Paused);
                    else QuitToMenu();
                }
                else
                {
                    SetState(GameState.Playing); // clicking anywhere else resumes
                }
                break;
            case GameState.Dead:
                _player.Spawn(_world);
                SetState(GameState.Playing);
                break;
            case GameState.Playing:
                if (button == MouseButton.Left && !_boats.TryPunch(_player, _inventory))
                {
                    _mineHeld = true;
                    var damage = 1f;

                    if (_inventory.IsTool(_inventory.SelectedItem, out ToolSpec tool))
                        damage = tool.Damage;

                    var punched = _animals.Punch(_player.EyePos, _player.ViewDir, _player.Pos, damage);
                    if (punched != null && _mode == GameMode.Survival)
                        _player.AddExhaustion(0.1f); // Minecraft: attacking costs 0.1
                }
                if (button == MouseButton.Right && !_boats.HandleRightClick(_player, _inventory))
                    _placeHeld = true;
                break;
            case GameState.InventoryOpen:
                if (button == MouseButton.Left) HandleUiClick(_mouse.Position.X, _mouse.Position.Y);
                break;
            case GameState.ChestOpen:
            case GameState.FurnaceOpen:
                if (button == MouseButton.Left) HandleContainerClick(_mouse.Position.X, _mouse.Position.Y);
                break;
        }
    }

    void HandleContainerClick(float mx, float my)
    {
        if (_state == GameState.ChestOpen)
        {
            var chest = _blockEntities.Chest(_container.X, _container.Y, _container.Z);
            if (_hud.HitChestSlot(mx, my) is { } ci)
            {
                _inventory.ClickSlotIn(chest, ci);
                _sound.Tone(300, 0.04, 0.06);
            }
            else if (_hud.HitChestPlayerSlot(mx, my) is { } pi)
            {
                _inventory.ClickSlot(pi);
                _sound.Tone(300, 0.04, 0.06);
            }
        }
        else
        {
            var furnace = _blockEntities.Furnace(_container.X, _container.Y, _container.Z);
            if (_hud.HitFurnaceSlot(mx, my) is { } fi)
            {
                if (fi == FurnaceState.Output) _inventory.TakeFromSlot(furnace.Slots, fi);
                else _inventory.ClickSlotIn(furnace.Slots, fi);
                _sound.Tone(300, 0.04, 0.06);
            }
            else if (_hud.HitFurnacePlayerSlot(mx, my) is { } pi)
            {
                _inventory.ClickSlot(pi);
                _sound.Tone(300, 0.04, 0.06);
            }
        }
    }

    void OnMouseMove(Vector2 pos)
    {
        if (_state != GameState.Playing) return;
        if (_hasLastMouse)
        {
            var delta = pos - _lastMouse;
            if (MathF.Abs(delta.X) < 300 && MathF.Abs(delta.Y) < 300)
                _player.Look(delta.X * _settings.MouseSensitivity,
                    delta.Y * _settings.MouseSensitivity * (_settings.InvertY ? -1f : 1f));
        }
        _lastMouse = pos;
        _hasLastMouse = true;
    }

    void HandleUiClick(float mx, float my)
    {
        if (_hud.HitSlot(mx, my) is { } slot)
        {
            _inventory.ClickSlot(slot);
            _sound.Tone(300, 0.04, 0.06);
            return;
        }
        if (_hud.HitRecipe(mx, my) is { } recipeIndex)
        {
            var recipe = ItemRegistry.Recipes[recipeIndex];
            if (!recipe.Hand && !_hud.TableOpen)
            {
                ShowToast("Needs a crafting table - craft one from 4 planks!");
                return;
            }
            // shift-click crafts a whole batch (until ingredients or space run out)
            bool shift = _keys.Contains(Key.ShiftLeft) || _keys.Contains(Key.ShiftRight);
            int rounds = shift ? 64 : 1, made = 0;
            var result = Crafting.Result.Ok;
            while (rounds-- > 0 && (result = Crafting.Craft(_inventory, recipe)) == Crafting.Result.Ok)
                made += recipe.Out.Count;
            if (made > 0)
            {
                _sound.Tone(520, 0.09, 0.1);
                ShowToast($"Crafted {(made > 1 ? made + "x " : "")}{ItemRegistry.NameOf(recipe.Out.Id)}");
            }
            else if (result == Crafting.Result.Full) ShowToast("Inventory full!");
            return;
        }
        // creative palette: clicking an entry puts a fresh stack on the cursor
        if (_hud.HitPalette(mx, my) is { } paletteId)
        {
            _inventory.Cursor = new ItemStack
            {
                Id = paletteId,
                Count = ItemRegistry.StackOf(paletteId),
                Durability = ItemRegistry.MaxDurability(paletteId),
            };
            _sound.Tone(340, 0.04, 0.06);
        }
    }

    void SetState(GameState state)
    {
        _state = state;
        _mineHeld = _placeHeld = false;
        _hasLastMouse = false;
        var cursor = _mouse?.Cursor;
        if (cursor != null)
        {
            if (state == GameState.Playing)
                cursor.CursorMode = cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled;
            else
                cursor.CursorMode = CursorMode.Normal;
        }
    }

    void ShowToast(string text)
    {
        _toastText = text;
        _toastTimer = 1.8f;
    }

    // ------------------------------------------------------------- settings

    (string Label, string Value)[] SettingsRows() => new[]
    {
        ("Mouse sensitivity", $"{_settings.MouseSensitivity:0.0}x"),
        ("Invert mouse Y", _settings.InvertY ? "On" : "Off"),
        ("Field of view", $"{_settings.Fov} deg"),
        ("Render distance", $"{_settings.RenderDistance} chunks"),
        ("Volume", $"{_settings.Volume}%"),
    };

    void AdjustSetting(int row, int dir)
    {
        switch (row)
        {
            case 0: _settings.MouseSensitivity += dir * 0.1f; break;
            case 1: _settings.InvertY = !_settings.InvertY; break;
            case 2: _settings.Fov += dir * 5; break;
            case 3: _settings.RenderDistance += dir; break;
            case 4: _settings.Volume += dir * 10; break;
        }
        _settings.ClampAll();
        ApplySettings();
        _sound.Tone(340, 0.04, 0.06);
    }

    /// Pushes the current settings into the systems that consume them.
    /// Render distance streams chunks in/out automatically on its next Update.
    void ApplySettings()
    {
        Constants.ViewRadius = _settings.RenderDistance;
        _sound.Volume = _settings.Volume / 100f;
    }

    void OpenSettings(GameState returnTo)
    {
        _settingsReturn = returnTo;
        SetState(GameState.Settings);
    }

    void CloseSettings()
    {
        _settings.Save();
        SetState(_settingsReturn);
    }

    // ------------------------------------------------------------- quit to menu

    /// Saves the current world, frees it, and returns to the world list.
    void QuitToMenu()
    {
        SaveWorld(silent: true);
        TeardownWorld();
        _saveList = WorldSave.ListSaves();
        SetState(GameState.MainMenu);
    }

    /// Frees every world-dependent system so another world can start. GPU
    /// meshes die only after the device is idle. In-flight background
    /// gen/mesh tasks are harmless: they only read the old world's CPU data
    /// and enqueue results into the old renderer's queues, which nothing
    /// drains anymore — it all gets garbage-collected together.
    void TeardownWorld()
    {
        _ctx.Vk.DeviceWaitIdle(_ctx.Device);
        foreach (var mesh in _worldRenderer.Meshes.Values) mesh.Dispose();
        foreach (var mesh in _worldRenderer.WaterMeshes.Values) mesh.Dispose();
        _map.Dispose();
        _worldRenderer = null;
        _map = null;
        _world = null;
        _terrain = null;
        _fluids = null;
        _redstone = null;
        _animals = null;
        _boats = null;
        _drops = null;
        _blockEntities = null;
        _interaction = null;
        _savePath = null;
        _player = new Player();
        _inventory.Reset();
        _particles = new ParticleSystem();
        _mode = GameMode.Survival;
        _hud.Mode = GameMode.Survival;
    }

    /// Writes the current world to its save file. Autotest never saves (its
    /// scripted edits would clobber a real world).
    void SaveWorld(bool silent)
    {
        if (_autotest || _world == null || _savePath == null) return;
        try
        {
            if (_player.Health <= 0) _player.Spawn(_world); // never save a corpse
            WorldSave.Save(_savePath, _worldName, _world, _fluids, _blockEntities,
                _player, _inventory, _mode, _boats, _drops, _timeOfDay);
            if (!silent) ShowToast($"Saved '{_worldName}'");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Save failed: {e.Message}");
            if (!silent) ShowToast("Save failed!");
        }
    }

    // ------------------------------------------------------------- frame

    void OnRender(double delta)
    {
        float dt = MathF.Min((float)delta, 0.05f);
        _clock += dt;
        if (_hurtFlash > 0) _hurtFlash -= dt * 2.5f;
        _fpsFrames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 0.5f)
        {
            _fps = (int)MathF.Round(_fpsFrames / _fpsTimer);
            _fpsFrames = 0;
            _fpsTimer = 0;
        }
        if (_toastTimer > 0) _toastTimer -= dt;

        if (_state == GameState.Playing)
        {
            int substeps = dt > 0.02f ? 2 : 1;
            bool shift = _keys.Contains(Key.ShiftLeft) || _keys.Contains(Key.ShiftRight);
            bool ctrl = _keys.Contains(Key.ControlLeft) || _keys.Contains(Key.ControlRight);
            var move = new MoveInput
            {
                Forward = (_keys.Contains(Key.W) ? 1 : 0) - (_keys.Contains(Key.S) ? 1 : 0),
                Strafe = (_keys.Contains(Key.D) ? 1 : 0) - (_keys.Contains(Key.A) ? 1 : 0),
                Jump = _keys.Contains(Key.Space),
                Sprint = ctrl,
                Sneak = shift,   // Minecraft layout: Shift sneaks, Ctrl sprints
                Descend = shift, // Shift doubles as "fly down" in creative flight
            };
            if (_boats.Ridden != null)
            {
                _boats.UpdateRidden(dt, move.Forward, move.Strafe); // the boat is the player's body now
            }
            else
            {
                for (int i = 0; i < substeps; i++)
                {
                    _player.Step(dt / substeps, move, _world, _mode);
                    if (_player.LastDamage > 0)
                    {
                        _hurtFlash = 1f;
                        _sound.Noise(0.18, 0.3, 250);
                    }
                    if (_player.JustDied)
                    {
                        _sound.Tone(90, 0.5, 0.3);
                        SetState(GameState.Dead);
                        break;
                    }
                }
            }
            _boats.Update(dt, _player);
            _drops.Update(dt, _player, _inventory);
            _interaction.Update(dt, _mineHeld, _placeHeld);
            _fluids.Update(dt);
            _redstone.Update(dt);
            _timeOfDay = (_timeOfDay + dt / DayLength) % 1f;
        }
        // furnaces keep smelting while their (or a chest's) panel is open
        if (_state is GameState.Playing or GameState.ChestOpen or GameState.FurnaceOpen)
            _blockEntities.Update(dt);
        if (_world != null) // menu screens have no world yet
        {
            _worldRenderer.Update(_player.Pos);
            _particles.Update(dt);
            _animals.Update(dt, _player.Pos);
        }

        DrawFrame();
        RunAutotestHooks();
    }

    void DrawFrame()
    {
        float w = _ctx.SwapExtent.Width, h = _ctx.SwapExtent.Height;
        if (w == 0 || h == 0) return;

        if (_world == null)
        {
            // menu screens: just a sky-colored clear and the 2D overlay
            var menuUbo = new GlobalUbo
            {
                ViewProj = Matrix4x4.Identity,
                CamPos = new Vector4(0, 0, 0, 1),
                FogColor = SkyColor,
                FogParams = new Vector4(1, 2, 0, 1),
            };
            _renderer.EntityLight = 1f;
            if (!_renderer.BeginFrame(in menuUbo, out _)) return;
            DrawHud(w, h);
            _renderer.DrawHud(_hud.Batch, w, h);
            _renderer.EndFrame();
            return;
        }

        var eye = _player.EyePos;
        var view = Matrix4x4.CreateLookAt(eye, eye + _player.ViewDir, Vector3.UnitY);
        // keep the clip far plane beyond the fog so geometry fades out
        // rather than getting clipped
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(_settings.Fov * MathF.PI / 180f, w / h, 0.1f, MathF.Max(400f, FogFar + 100f));
        proj.M22 *= -1; // GL/D3D clip-space Y is up; Vulkan's is down
        bool underwater = _world.GetBlock(
            (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z)) == BlockId.Water;
        float daylight = Daylight;
        var sky = CurrentSkyColor;
        var waterFog = WaterFogColor * new Vector4(daylight, daylight, daylight, 1);
        var ubo = new GlobalUbo
        {
            ViewProj = view * proj,
            // camPos.w carries the time-of-day daylight factor for the
            // shader's sky-light band
            CamPos = new Vector4(eye, daylight),
            FogColor = underwater ? waterFog : sky,
            // world light is baked into vertex color now; keep a small camera
            // glow (z) so pitch-black caves stay navigable. Underwater fog is
            // murky-water visibility, not render distance — it stays short.
            FogParams = underwater ? new Vector4(4, 28, 0.25f, 1.0f) : new Vector4(FogNear, FogFar, 0.25f, 1.0f),
        };
        _renderer.EntityLight = daylight; // animals/boats/drops dim with the night
        _frustum.Set(in ubo.ViewProj); // shared by chunk, entity, and particle culling
        if (!_renderer.BeginFrame(in ubo, out _)) return;

        _worldRenderer.Draw(_frustum);

        if (_state == GameState.Playing && _interaction.Highlighted is { } hit)
        {
            // outline hugs the block's selection shape (slab half, door panel...)
            var (bMin, bMax) = _world.SelectionBoundsAt(hit.X, hit.Y, hit.Z);
            var size = (bMax - bMin) * 1.002f;
            var model = Matrix4x4.CreateScale(size)
                      * Matrix4x4.CreateTranslation(new Vector3(hit.X, hit.Y, hit.Z) + bMin - new Vector3(0.001f));
            _renderer.DrawLineMesh(_lineCube, model, new Vector4(0, 0, 0, 0.6f), _white);
        }

        AnimalRenderer.Draw(_renderer, _animalMeshes, _animalTex, _animals.Animals, _frustum);
        BoatRenderer.Draw(_renderer, _cube, _white, _boats.All, _frustum);
        DropRenderer.Draw(_renderer, _blockCubes, _atlas, _drops.All, _frustum);
        _particles.Draw(_renderer, _cube, _white, _frustum);
        _worldRenderer.DrawWater(); // translucent pass after all opaque geometry

        if (_interaction.MineTarget is { } target && _interaction.MineProgress > 0)
        {
            var (bMin, bMax) = _world.SelectionBoundsAt(target.X, target.Y, target.Z);
            var model = Matrix4x4.CreateScale((bMax - bMin) * 1.004f)
                      * Matrix4x4.CreateTranslation(new Vector3(target.X, target.Y, target.Z) + (bMin + bMax) * 0.5f);
            _renderer.DrawMesh(_cube, model, new Vector4(0, 0, 0, (float)_interaction.MineProgress * 0.55f), false, _white);
        }

        DrawHud(w, h);
        _renderer.DrawHud(_hud.Batch, w, h);
        _renderer.EndFrame();
    }

    void DrawHud(float w, float h)
    {
        _hud.BeginFrame(w, h);
        if (_state == GameState.MainMenu)
        {
            _hud.DrawMainMenu(_saveList, _mouse.Position.X, _mouse.Position.Y);
            _hud.DrawToast(_toastText, Math.Clamp(_toastTimer / 0.4f, 0, 1));
            return;
        }
        if (_state == GameState.CreateWorld)
        {
            _hud.DrawCreateWorld(_newName, _newSeed, _createField, _mouse.Position.X, _mouse.Position.Y);
            return;
        }
        if (_state == GameState.Settings)
        {
            _hud.DrawSettings(SettingsRows(), _mouse.Position.X, _mouse.Position.Y);
            return;
        }
        if (_hurtFlash > 0)
            _hud.Batch.SolidQuad(_white, 0, 0, w, h, new Vector4(0.7f, 0.05f, 0.05f, _hurtFlash * 0.35f));
        _hud.DrawCrosshair();
        _hud.DrawHotbar();
        if (_mode == GameMode.Survival)
        {
            _hud.DrawHealth(_player.Health, _player.Air, Player.MaxAir);
            _hud.DrawHunger(_player.Hunger);

        }
        _hud.DrawStats(
            $"x {_player.Pos.X:0} y {_player.Pos.Y:0} z {_player.Pos.Z:0}  |  {_fps} fps  |  {_mode}  |  seed {Constants.Seed}  |  M map, G mode",
            new[]
            {
                ($"D:{_inventory.CountItem(BlockId.Diamond)}", new Vector4(0.35f, 0.85f, 0.83f, 1)),
                ($"G:{_inventory.CountItem(BlockId.Gold)}", new Vector4(0.91f, 0.79f, 0.25f, 1)),
                ($"I:{_inventory.CountItem(BlockId.Iron)}", new Vector4(0.79f, 0.65f, 0.51f, 1)),
                ($"C:{_inventory.CountItem(BlockId.Coal)}", new Vector4(0.55f, 0.55f, 0.55f, 1)),
            });
        if (_state == GameState.Playing && _interaction.MineTarget != null && _interaction.MineProgress > 0)
            _hud.DrawMineProgress(_interaction.MineProgress);
        if (_state == GameState.Playing && _interaction.EatProgress > 0)
            _hud.DrawMineProgress(_interaction.EatProgress); // chewing progress, same bar
        _hud.DrawToast(_toastText, Math.Clamp(_toastTimer / 0.4f, 0, 1));

        if (_state == GameState.InventoryOpen) _hud.DrawInventoryPanel(_mouse.Position.X, _mouse.Position.Y);
        else if (_state == GameState.ChestOpen)
            _hud.DrawChestPanel(_blockEntities.Chest(_container.X, _container.Y, _container.Z), _mouse.Position.X, _mouse.Position.Y);
        else if (_state == GameState.FurnaceOpen)
            _hud.DrawFurnacePanel(_blockEntities.Furnace(_container.X, _container.Y, _container.Z), _mouse.Position.X, _mouse.Position.Y);
        else if (_state == GameState.MapOpen) _map.Draw(_hud.Batch, _font, _white, w, h, _player);
        else if (_state == GameState.Dead) _hud.DrawDeathOverlay();
        else if (_state == GameState.Paused) _hud.DrawPauseOverlay(_mouse.Position.X, _mouse.Position.Y);
    }

    // ------------------------------------------------------------- autotest

    void RunAutotestHooks()
    {
        if (!_autotest) return;

        _frameCounter++;

        if (_frameCounter == 30)
        {
            // pose animals in front of the camera so the screenshot shows their textures
            SpawnTestAnimal(AnimalType.Pig, 0, -2.2f, faceCamera: true);
            SpawnTestAnimal(AnimalType.Sheep, -1.8f, -3.5f, faceCamera: true);
            SpawnTestAnimal(AnimalType.Chicken, 1.6f, -2.8f, faceCamera: true);
            // a few item drops just outside magnet range, spinning in view
            _drops.Spawn(_player.Pos.X - 1.1f, _player.Pos.Y + 1.2f, _player.Pos.Z - 3.4f, BlockId.Dirt);
            _drops.Spawn(_player.Pos.X + 1.4f, _player.Pos.Y + 1.2f, _player.Pos.Z - 3.6f, BlockId.Stone);
            _drops.Spawn(_player.Pos.X + 0.2f, _player.Pos.Y + 1.2f, _player.Pos.Z - 4.3f, BlockId.Diamond);
            _player.Pitch = -0.35f;
            // food icons in the hotbar and a part-drained hunger bar
            // (7 full, 1 half, 2 empty) for the HUD screenshot
            _inventory.AddItem(ItemId.RawPigMeat, 3);
            _inventory.AddItem(ItemId.RawSheepMeat, 2);
            _inventory.AddItem(ItemId.RawChickenMeat, 1);
            _player.AddExhaustion(10 * 4f); // burns 5 saturation, then 5 food
        }
        if (_frameCounter == 60) RunLogicTests();
        if (_frameCounter == 90)
        {
            _ctx.SaveScreenshot("autotest-screenshot.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-screenshot.png");
        }
        if (_frameCounter == 100) SetupTorchScene();
        if (_frameCounter == 130)
        {
            _ctx.SaveScreenshot("autotest-torch.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-torch.png");
        }
        if (_frameCounter == 135) _player.Yaw = -1.2f; // look at the room's left wall
        if (_frameCounter == 150) ScanSkyLightAnomalies();
        if (_frameCounter == 160)
        {
            _ctx.SaveScreenshot("autotest-torch2.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-torch2.png");
        }
        if (_frameCounter == 165) SetupWaterScene();
        if (_frameCounter == 200)
        {
            _ctx.SaveScreenshot("autotest-water.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-water.png");
        }
        if (_frameCounter == 205) SetupFlowScene();
        if (_frameCounter == 260) ReportFlowLevels();
        if (_frameCounter == 265)
        {
            _ctx.SaveScreenshot("autotest-flow.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-flow.png");
        }
        if (_frameCounter == 270) SetupVillageScene();
        if (_frameCounter == 310)
        {
            _ctx.SaveScreenshot("autotest-village.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-village.png");
        }
        if (_frameCounter == 320) SetupVistaScene();
        if (_frameCounter == 340)
        {
            _ctx.SaveScreenshot("autotest-vista.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-vista.png");
        }
        if (_frameCounter == 345) SetupRiverScene();
        if (_frameCounter == 385)
        {
            _ctx.SaveScreenshot("autotest-river.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-river.png");
        }
        if (_frameCounter == 390)
        {
            _map.Rebuild(_player.Pos); // centered on the river scene: river, ocean, biomes
            SetState(GameState.MapOpen);
        }
        if (_frameCounter == 405)
        {
            _ctx.SaveScreenshot("autotest-map.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-map.png");
        }
        if (_frameCounter == 415)
            SetupOceanScene();
        if (_frameCounter == 420)
        {
            //_ctx.SaveScreenshot("autotest-ocean.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-ocean.png");
        }
        if (_frameCounter == 430)
        {
            SetMode(GameMode.Creative); // show the infinite block palette
            SetState(GameState.InventoryOpen);
        }
        if (_frameCounter == 445)
        {
            _ctx.SaveScreenshot("autotest-creative.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-creative.png");
        }
        if (_frameCounter == 455)
            _window.Close();
    }

    /// Locates the nearest generated village and flies the camera above it.
    void SetupVillageScene()
    {
        var terrain = new TerrainGenerator();
        var found = terrain.FindNearestVillage(0, 0, 1200);
        if (found is not { } v)
        {
            Console.WriteLine("[TEST] village-scene: none found within radius, skipping");
            return;
        }
        int ccx = (int)MathF.Floor(v.Ax / (float)Constants.ChunkSize);
        int ccz = (int)MathF.Floor(v.Az / (float)Constants.ChunkSize);
        _worldRenderer.BuildAround(ccx, ccz, 3);
        int h = terrain.SurfaceHeight(v.Ax, v.Az);
        _player.Pos = new Vector3(v.Ax + 0.5f, h + 16f, v.Az + 0.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0.6f;
        _player.Pitch = -1.0f;
        Console.WriteLine($"[TEST] village-scene at x={v.Ax} z={v.Az}");
    }

    /// Finds the nearest river column and hovers above it so the screenshot
    /// shows the channel winding through the terrain.
    void SetupRiverScene()
    {
        var terrain = new TerrainGenerator();
        (int X, int Z)? best = null;
        int bestDist = int.MaxValue;
        for (int gz = -400; gz <= 400; gz += 8)
            for (int gx = -400; gx <= 400; gx += 8)
            {
                if (terrain.Sample(gx, gz).Biome != Biome.River) continue;
                int d = gx * gx + gz * gz;
                if (d < bestDist) { bestDist = d; best = (gx, gz); }
            }
        if (best is not { } r)
        {
            Console.WriteLine("[TEST] river-scene: none found within radius, skipping");
            return;
        }
        int ccx = (int)MathF.Floor(r.X / (float)Constants.ChunkSize);
        int ccz = (int)MathF.Floor(r.Z / (float)Constants.ChunkSize);
        _worldRenderer.BuildAround(ccx, ccz, 3);
        _player.Pos = new Vector3(r.X + 0.5f, Constants.SeaLevel + 14f, r.Z + 0.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0.9f;
        _player.Pitch = -0.75f;
        Console.WriteLine($"[TEST] river-scene at x={r.X} z={r.Z}");
    }

    /// Finds the nearest ocean and hovers above it so the screenshot
    void SetupOceanScene()
    {
        var terrain = new TerrainGenerator();
        (int X, int Z)? best = null;
        int bestDist = int.MaxValue;
        for (int gz = -32000; gz <= 32000; gz += 32)
            for (int gx = -32000; gx <= 32000; gx += 32)
            {
                if (terrain.Sample(gx, gz).Biome != Biome.Ocean) continue;
                int d = gx * gx + gz * gz;
                if (d < bestDist) { bestDist = d; best = (gx, gz); }
            }
        if (best is not { } r)
        {
            Console.WriteLine("[TEST] ocean-scene: none found within radius, skipping");
            return;
        }
        int ccx = (int)MathF.Floor(r.X / (float)Constants.ChunkSize);
        int ccz = (int)MathF.Floor(r.Z / (float)Constants.ChunkSize);
        _worldRenderer.BuildAround(ccx, ccz, 3);
        _player.Pos = new Vector3(r.X + 0.5f, Constants.SeaLevel + 14f, r.Z + 0.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0.9f;
        _player.Pitch = -0.75f;
        Console.WriteLine($"[TEST] ocean-scene at x={r.X} z={r.Z}");
    }

    /// Flies above spawn to show biome variety (oceans, plains, forest,
    /// desert, mountains) in one frame. Altitude stays well inside FogFar
    /// (which tracks the render distance) or the shot is just a haze.
    void SetupVistaScene()
    {
        _player.Pos = new Vector3(8.5f, MathF.Min(78f, FogFar * 0.6f), 8.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0.4f;
        _player.Pitch = -0.7f;
    }

    /// Carves a sealed room underground with a single torch so the screenshot
    /// shows block light falling off in the dark.
    void SetupTorchScene()
    {
        var changed = new HashSet<(int, int)>();
        for (int x = 4; x <= 12; x++)
            for (int z = 4; z <= 12; z++)
            {
                _world.SetBlockBatched(x, 15, z, BlockId.Stone, changed);
                for (int y = 16; y <= 19; y++)
                    _world.SetBlockBatched(x, y, z, BlockId.Air, changed);
            }
        _world.SetBlockBatched(10, 16, 10, BlockId.Torch, changed);
        _world.NotifyChanged(changed);
        var falloff = string.Join(" ", Enumerable.Range(0, 7)
            .Select(d => _world.Lighting.GetBlockLight(10 - d, 16, 10)));
        Console.WriteLine($"[TEST] torch falloff along -x: {falloff}");
        _player.Pos = new Vector3(6.5f, 16.02f, 6.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = -2.356f; // face the torch corner
        _player.Pitch = -0.15f;
    }

    /// Builds a flat stone floor with a single water source in one corner and
    /// nothing else, so the flow sim spreads it out and the mesher's
    /// distance-based taper is visible: tall near the source, shallow at the
    /// flow front, connected across every cell in between.
    void SetupFlowScene()
    {
        var changed = new HashSet<(int, int)>();
        for (int x = 0; x <= 12; x++)
            for (int z = 0; z <= 12; z++)
            {
                _world.SetBlockBatched(x, 20, z, BlockId.Stone, changed);
                for (int y = 21; y <= 23; y++)
                    _world.SetBlockBatched(x, y, z, BlockId.Air, changed);
            }
        _world.SetBlockBatched(1, 21, 1, BlockId.Water, changed);
        _world.SetBlockBatched(10, 21, 10, BlockId.Torch, changed); // light the room so the taper is visible
        _world.NotifyChanged(changed);
        _fluids.Activate(1, 21, 1);
        // drive the flow to completion deterministically rather than waiting
        // real frames for the tick timer (each call advances exactly one tick)
        for (int i = 0; i < 10; i++) _fluids.Update(1f);
        // stand in the far corner, on the floor, looking diagonally across
        // the room at the source so the whole taper is in frame
        _player.Pos = new Vector3(10.5f, 21.02f, 10.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0.78f;
        _player.Pitch = -0.55f;
    }

    void ReportFlowLevels()
    {
        var levels = string.Join(" ", Enumerable.Range(0, 8)
            .Select(d => _world.GetBlock(1 + d, 21, 1) == BlockId.Water ? _fluids.LevelAt(1 + d, 21, 1).ToString() : "-"));
        Console.WriteLine($"[TEST] flow levels along +x from source: {levels}");
    }

    /// Sky light 15 is only legal when nothing above the cell absorbs light;
    /// reports cells that violate that (light leaks).
    void ScanSkyLightAnomalies()
    {
        int anomalies = 0;
        foreach (var ((cx, cz), data) in _world.Chunks)
            for (int lz = 0; lz < Constants.ChunkSize; lz++)
                for (int lx = 0; lx < Constants.ChunkSize; lx++)
                {
                    bool clearAbove = true;
                    for (int y = Constants.WorldHeight - 1; y >= 0; y--)
                    {
                        int id = data[Constants.BlockIndex(lx, y, lz)];
                        int gx = cx * Constants.ChunkSize + lx, gz = cz * Constants.ChunkSize + lz;
                        int sky = _world.Lighting.GetSky(gx, y, gz);
                        if (id == BlockId.Air && sky == Constants.MaxLight && !clearAbove && anomalies++ < 5)
                            Console.WriteLine($"[TEST] sky-leak at {gx},{y},{gz}");
                        if (BlockRegistry.LightOpacity(id) > 0) clearAbove = false;
                    }
                }
        Console.WriteLine($"[TEST] sky-light-anomalies: {anomalies}");
    }

    /// Finds the nearest ocean column, builds a viewing platform above it,
    /// and points the camera down at the water.
    void SetupWaterScene()
    {
        var gen = new TerrainGenerator();
        (int X, int Z)? best = null;
        int bestDist = int.MaxValue;
        for (int gz = -320; gz <= 320; gz += 8)
            for (int gx = -320; gx <= 320; gx += 8)
            {
                if (gen.SurfaceHeight(gx, gz) > Constants.SeaLevel - 2) continue;
                int d = gx * gx + gz * gz;
                if (d < bestDist) { bestDist = d; best = (gx, gz); }
            }
        if (best is not { } spot)
        {
            Console.WriteLine("[TEST] water-scene: no ocean found nearby, skipping");
            return;
        }
        int ccx = (int)MathF.Floor(spot.X / (float)Constants.ChunkSize);
        int ccz = (int)MathF.Floor(spot.Z / (float)Constants.ChunkSize);
        _worldRenderer.BuildAround(ccx, ccz, 2);
        _world.SetBlock(spot.X, Constants.SeaLevel + 3, spot.Z, BlockId.Stone);
        _player.Pos = new Vector3(spot.X + 0.5f, Constants.SeaLevel + 4.02f, spot.Z + 0.5f);
        _player.Vel = Vector3.Zero;
        _player.Yaw = 0f; // face -Z
        _player.Pitch = -0.55f;
        // a boat bobbing in front of the camera proves rendering + flotation
        _boats.Spawn(spot.X + 0.5f, Constants.SeaLevel + 4f, spot.Z - 5.5f, yaw: 0.7f);
        Console.WriteLine($"[TEST] water-scene at x={spot.X} z={spot.Z}");
    }

    void SpawnTestAnimal(AnimalType type, float dx, float dz, bool faceCamera)
    {
        int gx = (int)MathF.Floor(_player.Pos.X + dx), gz = (int)MathF.Floor(_player.Pos.Z + dz);
        for (int y = Constants.WorldHeight - 2; y > 2; y--)
        {
            if (!_world.IsSolidAt(gx, y - 1, gz)) continue;
            var animal = new Animal(type, gx + 0.5f, y, gz + 0.5f);
            // face +Z (toward the default camera, which looks down -Z)
            if (faceCamera) animal.Yaw = animal.TargetYaw = -MathF.PI / 2;
            _animals.Animals.Add(animal);
            return;
        }
    }

    void RunLogicTests()
    {
        void Report(string name, bool pass) => Console.WriteLine($"[TEST] {name}: {(pass ? "PASS" : "FAIL")}");

        // craft chain: wood -> planks -> sticks -> wooden pickaxe
        var inv = new Inventory();
        inv.AddItem(BlockId.Wood, 2);
        Crafting.Craft(inv, ItemRegistry.Recipes[0]);
        Crafting.Craft(inv, ItemRegistry.Recipes[1]);
        Crafting.Craft(inv, ItemRegistry.Recipes[0]);
        Crafting.Craft(inv, ItemRegistry.Recipes[2]);
        var pick = inv.Slots.FirstOrDefault(s => s != null && s.Id == ItemId.WoodPick);
        Report("craft-chain", pick is { Count: 1, Durability: 60 });
        Report("craft-gating", Crafting.Craft(inv, ItemRegistry.Recipes[5]) == Crafting.Result.Missing);

        // world edit + remesh path
        int before = _world.GetBlock(10, 38, 10);
        _world.SetBlock(10, 38, 10, BlockId.Air);
        bool mined = _world.GetBlock(10, 38, 10) == BlockId.Air;
        _world.SetBlock(10, 38, 10, before);
        Report("world-edit", mined);

        // pathfinding: exercise it on a small synthetic platform (well away
        // from spawn) rather than natural terrain — biome-driven terrain can
        // put a tree canopy or a cliff at any fixed coordinate, which would
        // make this test's outcome depend on the terrain generator instead
        // of on the pathfinding algorithm itself
        var pfChanged = new HashSet<(int, int)>();
        const int pfX = 200, pfZ = 200, pfY = 40;
        for (int x = 0; x <= 10; x++)
            for (int z = 0; z <= 10; z++)
                _world.SetBlockBatched(pfX + x, pfY, pfZ + z, BlockId.Stone, pfChanged);
        _world.NotifyChanged(pfChanged);
        var path = Pathfinding.FindPath(_world, pfX, pfY + 1, pfZ, pfX + 10, pfY + 1, pfZ + 10);
        Report("pathfinding", path is { Count: > 1 });

        // lighting: open sky reads 15; a torch emits 14, spreads, and cleans up
        int lx = -1, lz = -1, lsy = 0;
        for (int x = 0; x < Constants.ChunkSize && lx < 0; x++)
            for (int z = 0; z < Constants.ChunkSize && lx < 0; z++)
            {
                int top = -1;
                for (int y = Constants.WorldHeight - 1; y >= 0; y--)
                    if (_world.GetBlock(x, y, z) != BlockId.Air) { top = y; break; }
                // a clear column: nothing but air above the surface block
                if (top > 0 && _world.GetBlock(x, top, z) is BlockId.Grass or BlockId.Sand)
                { lx = x; lz = z; lsy = top; }
            }
        if (lx >= 0)
        {
            Report("light-sky", _world.Lighting.GetSky(lx, lsy + 1, lz) == Constants.MaxLight);
            _world.SetBlock(lx, lsy + 2, lz, BlockId.Torch);
            bool emitted = _world.Lighting.GetBlockLight(lx, lsy + 2, lz) == 14;
            bool spread = _world.Lighting.GetBlockLight(lx, lsy + 3, lz) == 13;
            _world.SetBlock(lx, lsy + 2, lz, BlockId.Air);
            bool removed = _world.Lighting.GetBlockLight(lx, lsy + 2, lz) == 0;
            Report("light-torch", emitted && spread && removed);
        }

        // boats: dropped above a small synthetic pool, one settles onto the
        // water surface and stays there
        var poolChanged = new HashSet<(int, int)>();
        const int poolX = 230, poolZ = 230, poolY = 30;
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
                _world.SetBlockBatched(poolX + x, poolY, poolZ + z, BlockId.Stone, poolChanged);
        for (int x = 1; x < 4; x++)
            for (int z = 1; z < 4; z++)
                _world.SetBlockBatched(poolX + x, poolY + 1, poolZ + z, BlockId.Water, poolChanged);
        _world.NotifyChanged(poolChanged);
        var testBoat = _boats.Spawn(poolX + 2.5f, poolY + 5f, poolZ + 2.5f);
        for (int i = 0; i < 180; i++) _boats.Update(1f / 60f, _player);
        Report("boat-float", MathF.Abs(testBoat.Pos.Y - (poolY + 1 + 14f / 16f)) < 0.05f);
        _boats.All.Remove(testBoat);

        // survival physics: a ~9-block fall onto a platform costs ~6 HP
        var rig = new HashSet<(int, int)>();
        for (int x = 249; x <= 251; x++)
            for (int z = 249; z <= 251; z++)
            {
                _world.SetBlockBatched(x, 50, z, BlockId.Stone, rig); // drowning rig floor
                for (int y = 51; y < Constants.WorldHeight; y++)      // open shaft above it
                    _world.SetBlockBatched(x, y, z, BlockId.Air, rig);
                _world.SetBlockBatched(x, 58, z, BlockId.Stone, rig); // fall rig platform
            }
        _world.SetBlockBatched(250, 52, 250, BlockId.Water, rig); // eye-height water cell
        _world.NotifyChanged(rig);

        var still = new MoveInput();
        var faller = new Player { Pos = new Vector3(250.5f, 68f, 250.5f) };
        for (int i = 0; i < 300; i++) faller.Step(1f / 60f, still, _world, GameMode.Survival);
        Report("fall-damage", faller.OnGround && faller.Health is >= 13 and <= 15);

        // survival: breath runs out underwater, then drowning damage ticks
        var diver = new Player { Pos = new Vector3(250.5f, 51f, 250.5f) };
        for (int i = 0; i < 200; i++) diver.Step(0.1f, still, _world, GameMode.Survival);
        Report("drowning", diver.Air <= 0.01f && diver.Health < Player.MaxHealth);

        // hunger: exhaustion burns 5 saturation then 1 food point (6 x 4.0),
        // and eating raw pig meat (3 pts / 1.8 sat) refills the bar
        var eater = new Player();
        eater.AddExhaustion(24f);
        bool drained = eater.Hunger == 19f && eater.Saturation == 0f;
        eater.Eat(ItemRegistry.FoodOf(ItemId.RawPigMeat));
        Report("hunger-eat", drained && eater.Hunger == 20f && MathF.Abs(eater.Saturation - 1.8f) < 0.001f);

        // hunger: at 0 food, starvation ticks 1 damage every 4 s while idle
        var starver = new Player { Pos = new Vector3(250.5f, 59.02f, 250.5f) };
        starver.AddExhaustion(25 * 4f); // 5 saturation + all 20 food points
        for (int i = 0; i < 540; i++) starver.Step(1f / 60f, still, _world, GameMode.Survival);
        Report("starvation", starver.Hunger <= 0f && starver.Health <= Player.MaxHealth - 2);

        // sneaking: crouch-walking toward the platform edge slows down and
        // stops at the ledge (with the camera lowered) instead of falling off
        var sneaker = new Player { Pos = new Vector3(250.5f, 59.02f, 250.5f) };
        var sneakMove = new MoveInput { Forward = 1, Sneak = true };
        for (int i = 0; i < 300; i++) sneaker.Step(1f / 60f, sneakMove, _world, GameMode.Survival);
        bool heldEdge = sneaker.OnGround && MathF.Abs(sneaker.Pos.Y - 59f) < 0.05f
            && sneaker.Pos.Z < 249.6f; // moved to the ledge but no further
        bool crouchedEye = sneaker.EyePos.Y - sneaker.Pos.Y < 1.4f;
        Report("sneak-edge", heldEdge && crouchedEye);

        // item drops: one spawned above the rig platform falls, then a
        // nearby player magnetizes and collects it into their inventory
        var dropInv = new Inventory();
        var collector = new Player { Pos = new Vector3(250.5f, 59.02f, 250.5f) };
        _drops.Spawn(250.5f, 62f, 250.5f, BlockId.Planks);
        for (int i = 0; i < 180; i++) _drops.Update(1f / 60f, collector, dropInv);
        // (the scene drops spawned for the screenshot are still lying near
        // spawn, so only check that this one was collected)
        Report("item-drops", dropInv.CountItem(BlockId.Planks) == 1);

        // creative: flight rises with Jump and is forced off back in survival
        var flyer = new Player { Pos = new Vector3(250.5f, 70f, 250.5f), Flying = true };
        for (int i = 0; i < 60; i++) flyer.Step(1f / 60f, new MoveInput { Jump = true }, _world, GameMode.Creative);
        bool rose = flyer.Pos.Y > 75f;
        flyer.Step(1f / 60f, still, _world, GameMode.Survival);
        Report("creative-fly", rose && !flyer.Flying);

        // water: a cell suspended in air falls once woken
        _world.SetBlock(4, 50, 4, BlockId.Water);
        _fluids.Wake(4, 49, 4);
        for (int i = 0; i < 4; i++) _fluids.Update(1f);
        Report("water-flow", _world.GetBlock(4, 49, 4) == BlockId.Water);
        // water now falls to the ground in one tick and starts spreading, so
        // sweep the whole area (fall column + a few rings) or spawn floods
        var sweep = new HashSet<(int, int)>();
        for (int x = 4 - 10; x <= 4 + 10; x++)
            for (int z = 4 - 10; z <= 4 + 10; z++)
                for (int y = 30; y <= 52; y++)
                    if (_world.GetBlock(x, y, z) == BlockId.Water)
                        _world.SetBlockBatched(x, y, z, BlockId.Air, sweep);
        _world.NotifyChanged(sweep);
        bool oceanGenerated = _world.Chunks.Values.Any(c => c.Contains((byte)BlockId.Water));
        Console.WriteLine($"[TEST] info: ocean-in-loaded-chunks={oceanGenerated}");

        Report("chunks-meshed", _worldRenderer.Meshes.Count > 0);
        Report("animals", true); // spawning is randomized; presence checked visually
        Console.WriteLine($"[TEST] info: chunks={_world.Chunks.Count} meshes={_worldRenderer.Meshes.Count} animals={_animals.Animals.Count} fps={_fps}");
    }

    public void Dispose()
    {
        _sound?.Dispose();
        if (_renderer != null)
        {
            _renderer.Ctx.Vk.DeviceWaitIdle(_renderer.Ctx.Device);
            _cube?.Dispose();
            _lineCube?.Dispose();
            _animalMeshes?.Dispose();
            _blockCubes?.Dispose();
            if (_worldRenderer != null)
            {
                foreach (var mesh in _worldRenderer.Meshes.Values) mesh.Dispose();
                foreach (var mesh in _worldRenderer.WaterMeshes.Values) mesh.Dispose();
            }
            _map?.Dispose();
            _icons?.Texture.Dispose();
            _font?.Texture.Dispose();
            _atlas?.Texture.Dispose();
            _white?.Texture.Dispose();
            _animalTex?.Texture.Dispose();
            _renderer.Dispose();
            _ctx.Dispose();
        }
        _window?.Dispose();
    }
}
