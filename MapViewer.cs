using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace VoxelMiner;

using VoxelMiner.Engine;
using VoxelMiner.UI;
using VoxelMiner.World;

/// Standalone world-map explorer (`dotnet run -- --map`): its own window,
/// no game — just the terrain generator's view of the infinite world as a
/// scrollable, zoomable 2D map. Drag to pan, scroll (or +/-) to zoom, Esc
/// to quit. The map fills progressively (a pixel budget per frame) so
/// panning and zooming never stall; freshly exposed areas sweep in.
sealed class MapViewerApp : IDisposable
{
    const int PixelBudgetPerFrame = 24000;
    static readonly int[] ZoomLevels = { 1, 2, 4, 8, 16, 32 }; // world blocks per map pixel
    static readonly Vector4 Background = new(0.05f, 0.07f, 0.10f, 1f);
    static readonly Vector4 White = new(1, 1, 1, 1);

    readonly bool _autotest;
    readonly IWindow _window;
    readonly TerrainGenerator _terrain = new();
    readonly HudBatcher _batch = new();
    readonly Queue<(int Y, int X0, int X1)> _work = new(); // row spans awaiting color
    readonly List<(int X, int Z)> _villages = new();

    VulkanContext _ctx;
    Renderer _renderer;
    FontAtlas _font;
    TextureHandle _white;
    GpuTexture _mapTexture;
    TextureHandle _mapHandle;

    int _mapW, _mapH;       // map texture size (half-resolution: 1 texel = 2x2 screen px)
    byte[] _pixels;
    int _bpp = 2;           // current zoom: world blocks per map pixel
    int _originX, _originZ; // world block at map pixel (0, 0)
    bool _texDirty;

    bool _dragging;
    Vector2 _lastMouse;
    float _panX, _panY;     // sub-pixel pan remainder, in map pixels

    bool _villagesStale = true;
    float _settleTimer;
    int _frame;

    public MapViewerApp(bool autotest = false)
    {
        _autotest = autotest;
        _window = Window.Create(WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Voxel Miner - World Map",
        });
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += _ => { if (_ctx != null) _ctx.FramebufferResized = true; };
    }

    public void Run() => _window.Run();

    // ------------------------------------------------------------- init

    void OnLoad()
    {
        _ctx = new VulkanContext(_window);
        _renderer = new Renderer(_ctx);
        _font = new FontAtlas(_renderer);
        _white = _renderer.RegisterTexture(new GpuTexture(_ctx, new byte[] { 255, 255, 255, 255 }, 1, 1, srgb: false));

        _mapW = Math.Max((int)_ctx.SwapExtent.Width / 2, 320);
        _mapH = Math.Max((int)_ctx.SwapExtent.Height / 2, 180);
        _pixels = new byte[_mapW * _mapH * 4];
        _mapTexture = new GpuTexture(_ctx, _pixels, _mapW, _mapH);
        _mapHandle = _renderer.RegisterTexture(_mapTexture);

        CenterOn(0, 0); // world spawn

        var input = _window.CreateInput();
        foreach (var kb in input.Keyboards)
            kb.KeyDown += (_, key, _) =>
            {
                if (key == Key.Escape) _window.Close();
                if (key is Key.Equal or Key.KeypadAdd) Zoom(-1);
                if (key is Key.Minus or Key.KeypadSubtract) Zoom(1);
            };
        var mouse = input.Mice[0];
        mouse.MouseDown += (_, b) => { if (b == MouseButton.Left) { _dragging = true; _lastMouse = mouse.Position; } };
        mouse.MouseUp += (_, b) => { if (b == MouseButton.Left) _dragging = false; };
        mouse.MouseMove += (_, pos) =>
        {
            if (_dragging)
            {
                var d = pos - _lastMouse;
                Pan(-d.X, -d.Y); // content follows the cursor
            }
            _lastMouse = pos;
        };
        mouse.Scroll += (_, wheel) => Zoom(wheel.Y > 0 ? -1 : 1);
    }

    // ------------------------------------------------------------- view state

    float DrawScale() =>
        MathF.Min(_ctx.SwapExtent.Width / (float)_mapW, _ctx.SwapExtent.Height / (float)_mapH);

    void CenterOn(long wx, long wz)
    {
        _originX = (int)(wx - _mapW / 2 * _bpp);
        _originZ = (int)(wz - _mapH / 2 * _bpp);
        _work.Clear();
        EnqueueRect(0, 0, _mapW, _mapH);
        MarkViewChanged();
    }

    /// Moves the view by a screen-space delta, shifting already-computed
    /// pixels and queueing only the newly exposed strips.
    void Pan(float screenDx, float screenDy)
    {
        float s = DrawScale();
        _panX += screenDx / s;
        _panY += screenDy / s;
        int px = (int)_panX, py = (int)_panY;
        if (px == 0 && py == 0) return;
        _panX -= px;
        _panY -= py;

        _originX += px * _bpp;
        _originZ += py * _bpp;
        ShiftBuffer(-px, -py);
        MarkViewChanged();
    }

    void Zoom(int dir)
    {
        int i = Array.IndexOf(ZoomLevels, _bpp);
        int ni = Math.Clamp(i + dir, 0, ZoomLevels.Length - 1);
        if (ni == i) return;
        long cx = _originX + (long)(_mapW / 2) * _bpp;
        long cz = _originZ + (long)(_mapH / 2) * _bpp;
        _bpp = ZoomLevels[ni];
        CenterOn(cx, cz);
    }

    void MarkViewChanged()
    {
        _villagesStale = true;
        _settleTimer = 0;
    }

    // ------------------------------------------------------------- map build

    /// Slides the pixel buffer by (dx, dy) map pixels. Array.Copy is
    /// memmove-safe; row-wrap garbage at the edges lands exactly in the
    /// strips that get queued for recompute below.
    void ShiftBuffer(int dx, int dy)
    {
        if (Math.Abs(dx) >= _mapW || Math.Abs(dy) >= _mapH)
        {
            _work.Clear();
            EnqueueRect(0, 0, _mapW, _mapH);
            return;
        }
        int offset = (dy * _mapW + dx) * 4;
        if (offset > 0) Array.Copy(_pixels, 0, _pixels, offset, _pixels.Length - offset);
        else if (offset < 0) Array.Copy(_pixels, -offset, _pixels, 0, _pixels.Length + offset);

        if (dx > 0) EnqueueRect(0, 0, dx, _mapH);
        else if (dx < 0) EnqueueRect(_mapW + dx, 0, _mapW, _mapH);
        if (dy > 0) EnqueueRect(0, 0, _mapW, dy);
        else if (dy < 0) EnqueueRect(0, _mapH + dy, _mapW, _mapH);
        _texDirty = true;
    }

    void EnqueueRect(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, _mapW); y1 = Math.Min(y1, _mapH);
        for (int y = y0; y < y1; y++)
            if (x0 < x1) _work.Enqueue((y, x0, x1));
    }

    void ProcessWork()
    {
        int budget = PixelBudgetPerFrame;
        while (budget > 0 && _work.Count > 0)
        {
            var (y, x0, x1) = _work.Dequeue();
            int take = Math.Min(x1 - x0, budget);
            for (int x = x0; x < x0 + take; x++)
            {
                var (r, g, b) = MapColors.Generated(_terrain, _originX + x * _bpp, _originZ + y * _bpp);
                int i = (y * _mapW + x) * 4;
                _pixels[i] = r;
                _pixels[i + 1] = g;
                _pixels[i + 2] = b;
                _pixels[i + 3] = 255;
            }
            budget -= take;
            if (x0 + take < x1) _work.Enqueue((y, x0 + take, x1));
            _texDirty = true;
        }
    }

    // ------------------------------------------------------------- frame

    void OnRender(double delta)
    {
        _frame++;
        _settleTimer += (float)delta;
        ProcessWork();
        if (_texDirty)
        {
            _mapTexture.Update(_pixels);
            _texDirty = false;
        }
        // village markers are a heavier query: refresh only once the view
        // has settled and the map itself has finished filling
        if (_villagesStale && _settleTimer > 0.35f && _work.Count == 0)
        {
            _villages.Clear();
            _villages.AddRange(_terrain.VillagesIn(
                _originX, _originZ, _originX + _mapW * _bpp, _originZ + _mapH * _bpp));
            _villagesStale = false;
        }

        float w = _ctx.SwapExtent.Width, h = _ctx.SwapExtent.Height;
        if (w == 0 || h == 0) return;
        var ubo = new GlobalUbo
        {
            ViewProj = Matrix4x4.Identity,
            FogColor = Background,
            FogParams = new Vector4(1, 2, 0, 1),
        };
        if (!_renderer.BeginFrame(in ubo, out _)) return;

        _batch.Clear();
        float s = DrawScale();
        float drawW = _mapW * s, drawH = _mapH * s;
        float x0 = (w - drawW) / 2, y0 = (h - drawH) / 2;
        _batch.Quad(_mapHandle, x0, y0, drawW, drawH, 0, 0, 1, 1, White);

        float ToScreenX(long wx) => x0 + (wx - _originX) / (float)_bpp * s;
        float ToScreenY(long wz) => y0 + (wz - _originZ) / (float)_bpp * s;
        bool OnMap(float mx, float my) => mx >= x0 && mx <= x0 + drawW && my >= y0 && my <= y0 + drawH;

        foreach (var (vx, vz) in _villages)
        {
            float mx = ToScreenX(vx), my = ToScreenY(vz);
            if (!OnMap(mx, my)) continue;
            _batch.SolidQuad(_white, mx - 4, my - 4, 8, 8, new Vector4(0.35f, 0.22f, 0.1f, 1));
            _batch.SolidQuad(_white, mx - 3, my - 3, 6, 6, new Vector4(0.92f, 0.76f, 0.45f, 1));
        }
        // spawn marker
        {
            float mx = ToScreenX(0), my = ToScreenY(0);
            if (OnMap(mx, my))
            {
                _batch.SolidQuad(_white, mx - 4, my - 4, 8, 8, White);
                _batch.SolidQuad(_white, mx - 3, my - 3, 6, 6, new Vector4(0.85f, 0.15f, 0.15f, 1));
            }
        }

        long cx = _originX + (long)(_mapW / 2) * _bpp, cz = _originZ + (long)(_mapH / 2) * _bpp;
        _font.Draw(_batch, $"center x {cx} z {cz}   1 px = {_bpp} block{(_bpp > 1 ? "s" : "")}   " +
                           "drag to pan, scroll to zoom, Esc to quit", 10, 8, White);
        if (_work.Count > 0)
            _font.Draw(_batch, "mapping...", 10, 8 + _font.CellH + 4, new Vector4(1f, 0.84f, 0.29f, 1));

        _renderer.DrawHud(_batch, w, h);
        _renderer.EndFrame();

        RunAutotestHooks();
    }

    void RunAutotestHooks()
    {
        if (!_autotest) return;
        if (_frame == 30)
            while (_bpp < 8) Zoom(1); // zoom out to a continent-scale view
        if (_frame == 250)
        {
            _ctx.SaveScreenshot("autotest-mapviewer.png");
            Console.WriteLine("[TEST] screenshot saved: autotest-mapviewer.png");
        }
        if (_frame == 260) _window.Close();
    }

    public void Dispose()
    {
        if (_renderer != null)
        {
            _ctx.Vk.DeviceWaitIdle(_ctx.Device);
            _mapTexture?.Dispose();
            _font?.Texture.Dispose();
            _white?.Texture.Dispose();
            _renderer.Dispose();
            _ctx.Dispose();
        }
        _window?.Dispose();
    }
}
