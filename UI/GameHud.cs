using System.Numerics;

namespace VoxelMiner.UI;

using VoxelMiner.Core;
using VoxelMiner.Engine;
using VoxelMiner.Gameplay;
using VoxelMiner.World;

/// All 2D drawing: crosshair, hotbar, stats, toasts, the inventory panel with
/// its crafting list, and the pause overlay. Also hit-testing for UI clicks.
public sealed class GameHud
{
    const float Slot = 48, Gap = 4;
    const float RecipeRowH = 26;

    static readonly Vector4 White = new(1, 1, 1, 1);
    static readonly Vector4 Yellow = new(1f, 0.84f, 0.29f, 1);
    static readonly Vector4 SlotBg = new(0, 0, 0, 0.45f);
    static readonly Vector4 SlotSelBg = new(0.24f, 0.2f, 0, 0.55f);
    static readonly Vector4 BorderCol = new(1, 1, 1, 0.35f);
    static readonly Vector4 PanelBg = new(0.086f, 0.1f, 0.14f, 0.94f);
    static readonly Vector4 OverlayBg = new(0.04f, 0.06f, 0.1f, 0.85f);

    public readonly HudBatcher Batch = new();
    readonly FontAtlas _font;
    readonly IconAtlas _icons;
    readonly TextureHandle _white;
    readonly Inventory _inventory;

    /// Creative swaps the crafting list for an infinite block palette and
    /// hides the survival health bar.
    public GameMode Mode;

    // one of every placeable block plus the boat, offered in creative
    static readonly int[] PaletteIds =
    {
        Core.BlockId.Grass, Core.BlockId.Dirt, Core.BlockId.Stone, Core.BlockId.Sand,
        Core.BlockId.Wood, Core.BlockId.Leaves, Core.BlockId.Planks, Core.BlockId.Torch,
        Core.BlockId.TallGrass, Core.BlockId.FlowerYellow, Core.BlockId.FlowerRed,
        Core.BlockId.Coal, Core.BlockId.Iron, Core.BlockId.Gold, Core.BlockId.Diamond,
        Core.BlockId.Bedrock, Core.ItemId.Boat,
        Core.BlockId.PlankStairs, Core.BlockId.StoneStairs,
        Core.BlockId.PlankSlab, Core.BlockId.StoneSlab,
        Core.BlockId.PlankSlabVert, Core.BlockId.StoneSlabVert,
        Core.BlockId.Door, Core.BlockId.Trapdoor, Core.BlockId.Chest, Core.BlockId.Furnace,
        Core.BlockId.Snow, Core.BlockId.DryGrass,
        Core.BlockId.BirchWood, Core.BlockId.SpruceWood, Core.BlockId.Cactus, Core.BlockId.Ice,
        Core.BlockId.RedSand, Core.BlockId.Terracotta, Core.BlockId.TerracottaRed,
        Core.BlockId.Mycelium, Core.BlockId.MushroomCap, Core.BlockId.MushroomStem,
        Core.BlockId.Fence, Core.BlockId.Glass,
        Core.BlockId.RedstoneOre, Core.BlockId.Dust, Core.BlockId.Lever, Core.BlockId.Button,
        Core.BlockId.Repeater, Core.BlockId.Comparator, Core.BlockId.Observer,
        Core.BlockId.Piston, Core.BlockId.Piston + 12, // sticky piston
        Core.BlockId.Honey, Core.BlockId.Slime, Core.BlockId.CraftingTable,
    };

    float _screenW, _screenH;

    public GameHud(FontAtlas font, IconAtlas icons, TextureHandle white, Inventory inventory)
    {
        _font = font;
        _icons = icons;
        _white = white;
        _inventory = inventory;
    }

    public void BeginFrame(float screenW, float screenH)
    {
        _screenW = screenW;
        _screenH = screenH;
        Batch.Clear();
    }

    // ------------------------------------------------------------- gameplay HUD

    public void DrawCrosshair()
    {
        float cx = _screenW / 2, cy = _screenH / 2;
        Batch.SolidQuad(_white, cx - 1, cy - 10, 2, 20, new Vector4(1, 1, 1, 0.85f));
        Batch.SolidQuad(_white, cx - 10, cy - 1, 20, 2, new Vector4(1, 1, 1, 0.85f));
    }

    public void DrawMineProgress(double progress)
    {
        float cx = _screenW / 2, y = _screenH / 2 + 26;
        Batch.SolidQuad(_white, cx - 33, y, 66, 8, new Vector4(0, 0, 0, 0.5f));
        Batch.SolidQuad(_white, cx - 32, y + 1, 64 * (float)progress, 6, Yellow);
    }

    public void DrawHotbar()
    {
        if (Mode == GameMode.Spectator) return;
        
        float totalW = 9 * Slot + 8 * Gap;
        float x0 = (_screenW - totalW) / 2, y = _screenH - Slot - 12;
        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            DrawSlot(x0 + i * (Slot + Gap), y, _inventory.Slots[i], i == _inventory.Selected, (i + 1).ToString());
        }
    }

    public void DrawStats(string line1, (string Text, Vector4 Color)[] counters)
    {
        _font.Draw(Batch, line1, 10, 8, White);
        //float x = 10;
        //foreach (var (text, color) in counters)
        //{
        //    _font.Draw(Batch, text, x, 8 + _font.CellH, color);
        //    x += _font.Measure(text) + 12;
        //}
    }

    public void DrawToast(string text, float alpha)
    {
        if (string.IsNullOrEmpty(text) || alpha <= 0) return;
        float w = _font.Measure(text);
        _font.Draw(Batch, text, (_screenW - w) / 2, _screenH - Slot - 50, Yellow with { W = alpha });
    }

    // ------------------------------------------------------------- health bar

    /// Survival health hearts and (when diving) breath bubbles, above the hotbar.
    public void DrawHealth(float health, float air, float maxAir)
    {
        float totalW = 9 * Slot + 8 * Gap;
        float x0 = (_screenW - totalW) / 2, y = _screenH - Slot - 12 - 24;
        for (int i = 0; i < 10; i++)
        {
            int id = health >= i * 2 + 2 ? IconAtlas.HeartFull
                   : health >= i * 2 + 1 ? IconAtlas.HeartHalf
                   : IconAtlas.HeartEmpty;
            _icons.Draw(Batch, id, x0 + i * 20, y, 18);
        }
        if (air < maxAir - 0.01f)
        {
            int bubbles = (int)MathF.Ceiling(air / maxAir * 10);
            for (int i = 0; i < 10; i++)
            {
                var color = i < bubbles ? new Vector4(0.5f, 0.75f, 1f, 0.95f) : new Vector4(0.2f, 0.3f, 0.45f, 0.6f);
                Batch.SolidQuad(_white, x0 + totalW - 12 - i * 16, y + 4, 10, 10, color);
            }
        }
    }
    /// Survival hunger bar, above the hotbar: right-aligned with the hotbar's
    /// edge and mirrored (drains from its left end), like Minecraft's.
    public void DrawHunger(float hunger)
    {
        float totalW = 9 * Slot + 8 * Gap;
        float xRight = (_screenW + totalW) / 2 - 18, y = _screenH - Slot - 12 - 24;
        for (int i = 0; i < 10; i++)
        {
            int id = hunger >= i * 2 + 2 ? IconAtlas.HungerFull
                   : hunger >= i * 2 + 1 ? IconAtlas.HungerHalf
                   : IconAtlas.HungerEmpty;
            _icons.Draw(Batch, id, xRight - i * 20, y, 18);
        }
    }

    // ------------------------------------------------------------- inventory panel

    /// Opened via a crafting table: the advanced recipes unlock. The pocket
    /// (E) panel offers only hand recipes.
    public bool TableOpen;

    float BottomSectionH() => Mode == GameMode.Creative
        ? MathF.Ceiling(PaletteIds.Length / 9f) * (Slot + Gap)
        : MathF.Ceiling(ItemRegistry.Recipes.Length / 9f) * (Slot + Gap);

    (float X, float Y) PanelOrigin()
    {
        float panelW = 9 * Slot + 8 * Gap + 24;
        float panelH = 30 + 3 * (Slot + Gap) + 24 + (Slot + Gap) + 24 + BottomSectionH() + 16;
        return ((_screenW - panelW) / 2, (_screenH - panelH) / 2);
    }

    (float X, float Y, float W, float H) SlotRect(int index)
    {
        var (px, py) = PanelOrigin();
        float gx = px + 12, gy = py + 30;
        if (index >= Inventory.HotbarSize)
        {
            int i = index - Inventory.HotbarSize;
            return (gx + i % 9 * (Slot + Gap), gy + i / 9 * (Slot + Gap), Slot, Slot);
        }
        float hotY = gy + 3 * (Slot + Gap) + 24;
        return (gx + index * (Slot + Gap), hotY, Slot, Slot);
    }

    float BottomSectionY() => PanelOrigin().Y + 30 + 3 * (Slot + Gap) + 24 + (Slot + Gap) + 24;

    (float X, float Y, float W, float H) RecipeRect(int index)
    {
        var (px, _) = PanelOrigin();
        return (px + 12 + index % 9 * (Slot + Gap), BottomSectionY() + index / 9 * (Slot + Gap), Slot, Slot);
    }

    (float X, float Y, float W, float H) PaletteRect(int index)
    {
        var (px, _) = PanelOrigin();
        return (px + 12 + index % 9 * (Slot + Gap), BottomSectionY() + index / 9 * (Slot + Gap), Slot, Slot);
    }

    public void DrawInventoryPanel(float mouseX, float mouseY)
    {
        if (Mode == GameMode.Spectator) return;

        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, new Vector4(0, 0, 0, 0.45f));
        var (px, py) = PanelOrigin();
        float panelW = 9 * Slot + 8 * Gap + 24;
        float panelH = 30 + 3 * (Slot + Gap) + 24 + (Slot + Gap) + 24 + BottomSectionH() + 16;
        Batch.SolidQuad(_white, px, py, panelW, panelH, PanelBg);
        DrawBorder(px, py, panelW, panelH, 2, new Vector4(1, 1, 1, 0.25f));

        _font.Draw(Batch, "Inventory  (E or Esc to close)", px + 12, py + 8, White);
        for (int i = 0; i < Inventory.Size; i++)
        {
            var r = SlotRect(i);
            DrawSlotAt(r.X, r.Y, _inventory.Slots[i], i == _inventory.Selected && i < Inventory.HotbarSize,
                i < Inventory.HotbarSize ? (i + 1).ToString() : null);
        }
        float hotLabelY = py + 30 + 3 * (Slot + Gap) + 4;
        _font.Draw(Batch, "Hotbar", px + 12, hotLabelY, White);

        if (Mode == GameMode.Creative) DrawPalette(mouseX, mouseY);
        else DrawRecipes(mouseX, mouseY);

        // stack held by the mouse
        if (_inventory.Cursor != null)
        {
            _icons.Draw(Batch, _inventory.Cursor.Id, mouseX + 8, mouseY + 8, 32);
            if (_inventory.Cursor.Count > 1)
                _font.Draw(Batch, _inventory.Cursor.Count.ToString(), mouseX + 28, mouseY + 28, White);
        }
    }

    /// Creative: one of every block, click to grab a full stack.
    void DrawPalette(float mouseX, float mouseY)
    {
        var (px, _) = PanelOrigin();
        _font.Draw(Batch, "Blocks  (click for a stack)", px + 12, BottomSectionY() - 20, White);
        for (int i = 0; i < PaletteIds.Length; i++)
        {
            var r = PaletteRect(i);
            bool hover = mouseX >= r.X && mouseX < r.X + r.W && mouseY >= r.Y && mouseY < r.Y + r.H;
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H, hover ? SlotSelBg : SlotBg);
            DrawBorder(r.X, r.Y, r.W, r.H, 2, hover ? Yellow : BorderCol);
            _icons.Draw(Batch, PaletteIds[i], r.X + 8, r.Y + 8, 32);
        }
    }

    static readonly Vector4 CraftableGreen = new(0.35f, 0.9f, 0.4f, 0.8f);
    static readonly Vector4 LockedOrange = new(1f, 0.65f, 0.25f, 1f);

    /// Recipe grid: one cell per recipe (green border = craftable now, table
    /// badge = needs a crafting table). Hover shows a tooltip with the
    /// ingredients as have/need; click crafts one, shift-click crafts a batch.
    void DrawRecipes(float mouseX, float mouseY)
    {
        var (px, _) = PanelOrigin();
        var (_, ry, _, _) = RecipeRect(0);
        _font.Draw(Batch, TableOpen ? "Crafting Table" : "Crafting  (advanced recipes need a table)",
            px + 12, ry - 20, White);

        int hovered = -1;
        for (int i = 0; i < ItemRegistry.Recipes.Length; i++)
        {
            var recipe = ItemRegistry.Recipes[i];
            var r = RecipeRect(i);
            bool locked = !recipe.Hand && !TableOpen;
            bool affordable = !locked && Crafting.CanCraft(_inventory, recipe);
            bool hover = mouseX >= r.X && mouseX < r.X + r.W && mouseY >= r.Y && mouseY < r.Y + r.H;
            if (hover) hovered = i;

            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H, hover ? SlotSelBg : SlotBg);
            DrawBorder(r.X, r.Y, r.W, r.H, 2, hover ? Yellow : affordable ? CraftableGreen : BorderCol);
            _icons.Draw(Batch, recipe.Out.Id, r.X + 8, r.Y + 8, 32, locked ? 0.3f : affordable ? 1f : 0.5f);
            if (recipe.Out.Count > 1)
                _font.Draw(Batch, recipe.Out.Count.ToString(), r.X + r.W - 16, r.Y + r.H - 18, White);
            if (locked) // badge: this one needs the crafting table
                _icons.Draw(Batch, Core.BlockId.CraftingTable, r.X + r.W - 15, r.Y + 3, 12, 0.9f);
        }

        if (hovered >= 0) DrawRecipeTooltip(ItemRegistry.Recipes[hovered], mouseX, mouseY);
    }

    /// Floating ingredient card: output name, then each ingredient with the
    /// player's have/need count (red when short), plus the table requirement.
    void DrawRecipeTooltip(Recipe recipe, float mouseX, float mouseY)
    {
        bool locked = !recipe.Hand && !TableOpen;
        string title = (recipe.Out.Count > 1 ? recipe.Out.Count + "x " : "") + ItemRegistry.NameOf(recipe.Out.Id);
        const float lineH = 22, pad = 10;
        float w = MathF.Max(_font.Measure(title) + 20, 190);
        foreach (var (id, n) in recipe.In)
            w = MathF.Max(w, 30 + _font.Measure($"{_inventory.CountItem(id)}/{n}  {ItemRegistry.NameOf(id)}") + 20);
        float h = pad * 2 + lineH + recipe.In.Length * lineH + (locked ? lineH : 0)
                + (Mode == GameMode.Survival ? lineH : 0);

        float tx = MathF.Min(mouseX + 16, _screenW - w - 4);
        float ty = MathF.Min(mouseY + 16, _screenH - h - 4);
        Batch.SolidQuad(_white, tx, ty, w, h, new Vector4(0.06f, 0.06f, 0.09f, 0.95f));
        DrawBorder(tx, ty, w, h, 2, new Vector4(1, 1, 1, 0.35f));

        float y = ty + pad;
        _icons.Draw(Batch, recipe.Out.Id, tx + pad, y, 18);
        _font.Draw(Batch, title, tx + pad + 24, y + 2, White);
        y += lineH;
        foreach (var (id, n) in recipe.In)
        {
            int have = _inventory.CountItem(id);
            var color = have >= n ? new Vector4(0.55f, 0.95f, 0.55f, 1) : new Vector4(0.95f, 0.45f, 0.4f, 1);
            _icons.Draw(Batch, id, tx + pad + 4, y, 16);
            _font.Draw(Batch, $"{have}/{n}  {ItemRegistry.NameOf(id)}", tx + pad + 26, y + 1, color);
            y += lineH;
        }
        if (locked)
        {
            _font.Draw(Batch, "Needs a crafting table", tx + pad, y + 1, LockedOrange);
            y += lineH;
        }
        else if (Mode == GameMode.Survival)
        {
            _font.Draw(Batch, "Click: craft   Shift: batch", tx + pad, y + 1, new Vector4(1, 1, 1, 0.55f));
        }
    }

    /// Returns the slot index under the mouse, or null.
    public int? HitSlot(float mx, float my)
    {
        for (int i = 0; i < Inventory.Size; i++)
        {
            var r = SlotRect(i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return i;
        }
        return null;
    }

    /// Returns the recipe index under the mouse, or null (survival panel only).
    public int? HitRecipe(float mx, float my)
    {
        if (Mode == GameMode.Creative) return null;
        for (int i = 0; i < ItemRegistry.Recipes.Length; i++)
        {
            var r = RecipeRect(i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return i;
        }
        return null;
    }

    /// Returns the item id of the palette cell under the mouse, or null
    /// (creative panel only).
    public int? HitPalette(float mx, float my)
    {
        if (Mode != GameMode.Creative) return null;
        for (int i = 0; i < PaletteIds.Length; i++)
        {
            var r = PaletteRect(i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return PaletteIds[i];
        }
        return null;
    }

    // ------------------------------------------------------------- container panels
    // Chest and furnace screens: block-specific slots on top, then the
    // player's inventory (main grid + hotbar) below, sharing the mouse cursor
    // stack with the regular inventory panel.

    const float ChestContentH = 3 * (Slot + Gap);
    const float FurnaceContentH = Slot + Gap + 20;

    (float X, float Y, float W, float H) ContainerPanel(float contentH)
    {
        float w = 9 * Slot + 8 * Gap + 24;
        float h = 30 + contentH + 24 + 3 * (Slot + Gap) + 24 + (Slot + Gap) + 12;
        return ((_screenW - w) / 2, (_screenH - h) / 2, w, h);
    }

    /// y of the player's main grid inside a container panel.
    float ContainerPlayerY(float contentH) => ContainerPanel(contentH).Y + 30 + contentH + 24;

    void DrawContainerFrame(float contentH, string title)
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, new Vector4(0, 0, 0, 0.45f));
        var (px, py, w, h) = ContainerPanel(contentH);
        Batch.SolidQuad(_white, px, py, w, h, PanelBg);
        DrawBorder(px, py, w, h, 2, new Vector4(1, 1, 1, 0.25f));
        _font.Draw(Batch, title, px + 12, py + 8, White);

        float gy = ContainerPlayerY(contentH);
        _font.Draw(Batch, "Inventory", px + 12, gy - 18, White);
        for (int i = Inventory.HotbarSize; i < Inventory.Size; i++)
        {
            var r = ContainerPlayerSlotRect(contentH, i);
            DrawSlotAt(r.X, r.Y, _inventory.Slots[i], false, null);
        }
        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            var r = ContainerPlayerSlotRect(contentH, i);
            DrawSlotAt(r.X, r.Y, _inventory.Slots[i], i == _inventory.Selected, (i + 1).ToString());
        }
    }

    (float X, float Y, float W, float H) ContainerPlayerSlotRect(float contentH, int index)
    {
        float gx = ContainerPanel(contentH).X + 12, gy = ContainerPlayerY(contentH);
        if (index >= Inventory.HotbarSize)
        {
            int i = index - Inventory.HotbarSize;
            return (gx + i % 9 * (Slot + Gap), gy + i / 9 * (Slot + Gap), Slot, Slot);
        }
        return (gx + index * (Slot + Gap), gy + 3 * (Slot + Gap) + 18, Slot, Slot);
    }

    int? HitContainerPlayerSlot(float contentH, float mx, float my)
    {
        for (int i = 0; i < Inventory.Size; i++)
        {
            var r = ContainerPlayerSlotRect(contentH, i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return i;
        }
        return null;
    }

    void DrawCursorStack(float mouseX, float mouseY)
    {
        if (_inventory.Cursor == null) return;
        _icons.Draw(Batch, _inventory.Cursor.Id, mouseX + 8, mouseY + 8, 32);
        if (_inventory.Cursor.Count > 1)
            _font.Draw(Batch, _inventory.Cursor.Count.ToString(), mouseX + 28, mouseY + 28, White);
    }

    // ------------------------------------------------------------- chest

    (float X, float Y, float W, float H) ChestSlotRect(int index)
    {
        var (px, py, _, _) = ContainerPanel(ChestContentH);
        return (px + 12 + index % 9 * (Slot + Gap), py + 30 + index / 9 * (Slot + Gap), Slot, Slot);
    }

    public void DrawChestPanel(ItemStack[] chest, float mouseX, float mouseY)
    {
        DrawContainerFrame(ChestContentH, "Chest  (E or Esc to close)");
        for (int i = 0; i < chest.Length; i++)
        {
            var r = ChestSlotRect(i);
            DrawSlotAt(r.X, r.Y, chest[i], false, null);
        }
        DrawCursorStack(mouseX, mouseY);
    }

    public int? HitChestSlot(float mx, float my)
    {
        for (int i = 0; i < BlockEntities.ChestSlots; i++)
        {
            var r = ChestSlotRect(i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return i;
        }
        return null;
    }

    public int? HitChestPlayerSlot(float mx, float my) => HitContainerPlayerSlot(ChestContentH, mx, my);

    // ------------------------------------------------------------- furnace

    (float X, float Y, float W, float H) FurnaceSlotRect(int index)
    {
        var (px, py, _, _) = ContainerPanel(FurnaceContentH);
        float col = index switch { FurnaceState.Input => 1, FurnaceState.Fuel => 3, _ => 6 };
        return (px + 12 + col * (Slot + Gap), py + 30, Slot, Slot);
    }

    public void DrawFurnacePanel(FurnaceState furnace, float mouseX, float mouseY)
    {
        DrawContainerFrame(FurnaceContentH, "Furnace  (E or Esc to close)");
        string[] labels = { "Smelt", "Fuel", "Result" };
        for (int i = 0; i < 3; i++)
        {
            var r = FurnaceSlotRect(i);
            DrawSlotAt(r.X, r.Y, furnace.Slots[i], false, null);
            _font.Draw(Batch, labels[i], r.X, r.Y + Slot + 4, new Vector4(1, 1, 1, 0.7f), 0.9f);
        }

        // burn meter under the fuel slot, progress arrow toward the result
        var fuel = FurnaceSlotRect(FurnaceState.Fuel);
        if (furnace.Lit)
        {
            float pct = furnace.FuelLeft / furnace.FuelTotal;
            Batch.SolidQuad(_white, fuel.X, fuel.Y - 9, Slot, 6, new Vector4(0, 0, 0, 0.5f));
            Batch.SolidQuad(_white, fuel.X, fuel.Y - 8, Slot * pct, 4, new Vector4(1f, 0.55f, 0.15f, 1));
        }
        var outR = FurnaceSlotRect(FurnaceState.Output);
        float ax0 = fuel.X + Slot + 14, ax1 = outR.X - 14, ay = fuel.Y + Slot / 2 - 3;
        var recipe = furnace.Slots[FurnaceState.Input] != null ? Smelting.For(furnace.Slots[FurnaceState.Input].Id) : null;
        float progress = recipe != null ? Math.Clamp(furnace.Progress / recipe.Seconds, 0, 1) : 0;
        Batch.SolidQuad(_white, ax0, ay, ax1 - ax0, 6, new Vector4(1, 1, 1, 0.15f));
        Batch.SolidQuad(_white, ax0, ay, (ax1 - ax0) * progress, 6, Yellow);

        DrawCursorStack(mouseX, mouseY);
    }

    public int? HitFurnaceSlot(float mx, float my)
    {
        for (int i = 0; i < 3; i++)
        {
            var r = FurnaceSlotRect(i);
            if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H) return i;
        }
        return null;
    }

    public int? HitFurnacePlayerSlot(float mx, float my) => HitContainerPlayerSlot(FurnaceContentH, mx, my);

    // ------------------------------------------------------------- main menu

    const float MenuRowW = 620, MenuRowH = 52, MenuRowGap = 8;
    const int MenuMaxRows = 8;
    static readonly Vector4 TitleGreen = new(0.44f, 0.84f, 0.44f, 1);

    static bool Inside((float X, float Y, float W, float H) r, float mx, float my) =>
        mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H;

    (float X, float Y, float W, float H) MenuRowRect(int i) =>
        ((_screenW - MenuRowW) / 2, _screenH * 0.32f + i * (MenuRowH + MenuRowGap), MenuRowW, MenuRowH);

    public void DrawMainMenu(IReadOnlyList<SaveInfo> worlds, float mx, float my)
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, OverlayBg);
        DrawCentered("VOXEL MINER", _screenH * 0.13f, 3f, TitleGreen);
        DrawCentered(worlds.Count > 0 ? "Select a world" : "No worlds yet - create one!",
            _screenH * 0.13f + 64, 1.1f, White);

        int rows = Math.Min(worlds.Count, MenuMaxRows);
        for (int i = 0; i < rows; i++)
        {
            var r = MenuRowRect(i);
            bool hover = Inside(r, mx, my);
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H, hover ? SlotSelBg : SlotBg);
            DrawBorder(r.X, r.Y, r.W, r.H, 2, hover ? Yellow : BorderCol);
            _font.Draw(Batch, worlds[i].Name, r.X + 14, r.Y + 14, White, 1.2f);
            string detail = $"seed {worlds[i].Seed}   {worlds[i].Modified:yyyy-MM-dd HH:mm}";
            _font.Draw(Batch, detail, r.X + r.W - 14 - _font.Measure(detail, 0.9f), r.Y + 18,
                new Vector4(1, 1, 1, 0.6f), 0.9f);
        }

        var nb = MenuRowRect(rows);
        bool nHover = Inside(nb, mx, my);
        Batch.SolidQuad(_white, nb.X, nb.Y, nb.W, nb.H,
            nHover ? new Vector4(0.16f, 0.32f, 0.14f, 0.92f) : new Vector4(0.10f, 0.22f, 0.10f, 0.85f));
        DrawBorder(nb.X, nb.Y, nb.W, nb.H, 2, nHover ? Yellow : TitleGreen with { W = 0.6f });
        string label = "+ Create New World";
        _font.Draw(Batch, label, nb.X + (nb.W - _font.Measure(label, 1.2f)) / 2, nb.Y + 14, White, 1.2f);

        var sb = MenuRowRect(rows + 1);
        bool sHover = Inside(sb, mx, my);
        Batch.SolidQuad(_white, sb.X, sb.Y, sb.W, sb.H, sHover ? SlotSelBg : SlotBg);
        DrawBorder(sb.X, sb.Y, sb.W, sb.H, 2, sHover ? Yellow : BorderCol);
        string settings = "Settings";
        _font.Draw(Batch, settings, sb.X + (sb.W - _font.Measure(settings, 1.2f)) / 2, sb.Y + 14, White, 1.2f);

        DrawCentered("Esc quits", sb.Y + MenuRowH + 28, 0.95f, new Vector4(1, 1, 1, 0.5f));
    }

    /// Row under the mouse: 0..worldCount-1 selects a world, worldCount is
    /// the New World button, worldCount+1 is Settings, null is nothing.
    public int? HitMenuRow(int worldCount, float mx, float my)
    {
        int rows = Math.Min(worldCount, MenuMaxRows);
        for (int i = 0; i <= rows + 1; i++)
            if (Inside(MenuRowRect(i), mx, my))
                return i >= rows ? worldCount + (i - rows) : i;
        return null;
    }

    // ------------------------------------------------------------- create world

    (float X, float Y, float W, float H) CreateFieldRect(int i) =>
        ((_screenW - MenuRowW) / 2, _screenH * 0.30f + i * 110, MenuRowW, 46);

    (float X, float Y, float W, float H) CreateButtonRect(int i)
    {
        float w = (MenuRowW - 20) / 2;
        return ((_screenW - MenuRowW) / 2 + i * (w + 20), _screenH * 0.30f + 220, w, 50);
    }

    public void DrawCreateWorld(string name, string seed, int activeField, float mx, float my)
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, OverlayBg);
        DrawCentered("NEW WORLD", _screenH * 0.13f, 2.4f, TitleGreen);

        string[] labels = { "World name", "Seed (leave empty for random)" };
        string[] values = { name, seed };
        for (int i = 0; i < 2; i++)
        {
            var r = CreateFieldRect(i);
            bool active = activeField == i;
            _font.Draw(Batch, labels[i], r.X, r.Y - 24, White);
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H, new Vector4(0, 0, 0, 0.55f));
            DrawBorder(r.X, r.Y, r.W, r.H, 2, active ? Yellow : BorderCol);
            _font.Draw(Batch, values[i] + (active ? "_" : ""), r.X + 12, r.Y + 12, White, 1.1f);
        }

        string[] buttons = { "Create", "Back" };
        for (int i = 0; i < 2; i++)
        {
            var r = CreateButtonRect(i);
            bool hover = Inside(r, mx, my);
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H,
                i == 0 ? (hover ? new Vector4(0.16f, 0.32f, 0.14f, 0.92f) : new Vector4(0.10f, 0.22f, 0.10f, 0.85f))
                       : (hover ? SlotSelBg : SlotBg));
            DrawBorder(r.X, r.Y, r.W, r.H, 2, hover ? Yellow : BorderCol);
            _font.Draw(Batch, buttons[i], r.X + (r.W - _font.Measure(buttons[i], 1.2f)) / 2, r.Y + 13, White, 1.2f);
        }

        DrawCentered("Click a field to edit  -  Tab switches  -  Enter creates",
            CreateButtonRect(0).Y + 76, 0.95f, new Vector4(1, 1, 1, 0.5f));
    }

    /// 0 = name field, 1 = seed field, 2 = Create, 3 = Back.
    public int? HitCreateWorld(float mx, float my)
    {
        for (int i = 0; i < 2; i++)
            if (Inside(CreateFieldRect(i), mx, my)) return i;
        for (int i = 0; i < 2; i++)
            if (Inside(CreateButtonRect(i), mx, my)) return 2 + i;
        return null;
    }

    // ------------------------------------------------------------- settings

    const float ArrowW = 44;

    (float X, float Y, float W, float H) SettingsRowRect(int i) =>
        ((_screenW - MenuRowW) / 2, _screenH * 0.26f + i * (MenuRowH + MenuRowGap), MenuRowW, MenuRowH);

    (float X, float Y, float W, float H) SettingsBackRect(int rowCount)
    {
        var last = SettingsRowRect(rowCount);
        return (last.X + (MenuRowW - 220) / 2, last.Y + 16, 220, 50);
    }

    public void DrawSettings(IReadOnlyList<(string Label, string Value)> rows, float mx, float my)
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, OverlayBg);
        DrawCentered("SETTINGS", _screenH * 0.11f, 2.4f, TitleGreen);

        for (int i = 0; i < rows.Count; i++)
        {
            var r = SettingsRowRect(i);
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H, SlotBg);
            DrawBorder(r.X, r.Y, r.W, r.H, 2, BorderCol);
            _font.Draw(Batch, rows[i].Label, r.X + 14, r.Y + 14, White, 1.1f);

            // "<  value  >" cluster, right-aligned
            float valueW = 170;
            float vx = r.X + r.W - ArrowW - valueW - ArrowW - 8;
            var la = (X: vx, Y: r.Y + 4, W: ArrowW, H: r.H - 8);
            var ra = (X: vx + ArrowW + valueW, Y: r.Y + 4, W: ArrowW, H: r.H - 8);
            foreach (var (a, txt) in new[] { (la, "<"), (ra, ">") })
            {
                bool hover = Inside((a.X, a.Y, a.W, a.H), mx, my);
                Batch.SolidQuad(_white, a.X, a.Y, a.W, a.H, hover ? SlotSelBg : new Vector4(1, 1, 1, 0.08f));
                _font.Draw(Batch, txt, a.X + (a.W - _font.Measure(txt, 1.2f)) / 2, a.Y + 10, hover ? Yellow : White, 1.2f);
            }
            string v = rows[i].Value;
            _font.Draw(Batch, v, vx + ArrowW + (valueW - _font.Measure(v, 1.1f)) / 2, r.Y + 14, Yellow, 1.1f);
        }

        var back = SettingsBackRect(rows.Count);
        bool bHover = Inside(back, mx, my);
        Batch.SolidQuad(_white, back.X, back.Y, back.W, back.H, bHover ? SlotSelBg : SlotBg);
        DrawBorder(back.X, back.Y, back.W, back.H, 2, bHover ? Yellow : BorderCol);
        _font.Draw(Batch, "Back", back.X + (back.W - _font.Measure("Back", 1.2f)) / 2, back.Y + 13, White, 1.2f);
    }

    /// (row, direction) for an arrow click (-1 / +1); (rowCount, 0) for Back.
    public (int Row, int Dir)? HitSettings(int rowCount, float mx, float my)
    {
        for (int i = 0; i < rowCount; i++)
        {
            var r = SettingsRowRect(i);
            if (!Inside(r, mx, my)) continue;
            float valueW = 170;
            float vx = r.X + r.W - ArrowW - valueW - ArrowW - 8;
            if (mx >= vx && mx < vx + ArrowW) return (i, -1);
            if (mx >= vx + ArrowW + valueW && mx < vx + ArrowW + valueW + ArrowW) return (i, +1);
            return (i, +1); // clicking the row itself steps forward (handy for toggles)
        }
        if (Inside(SettingsBackRect(rowCount), mx, my)) return (rowCount, 0);
        return null;
    }

    // ------------------------------------------------------------- overlays

    public void DrawDeathOverlay()
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, new Vector4(0.35f, 0.02f, 0.02f, 0.72f));
        DrawCentered("You died!", _screenH * 0.38f, 2.6f, White);
        DrawCentered("- Click to respawn -", _screenH * 0.38f + 60, 1.2f, Yellow);
    }

    static readonly string[] PauseButtons = { "Resume", "Settings", "Save & Quit to Menu" };

    (float X, float Y, float W, float H) PauseButtonRect(int i) =>
        ((_screenW - 380) / 2, _screenH * 0.24f + i * 62, 380, 50);

    public void DrawPauseOverlay(float mx, float my)
    {
        Batch.SolidQuad(_white, 0, 0, _screenW, _screenH, OverlayBg);
        float cy = _screenH * 0.10f;
        DrawCentered("VOXEL MINER", cy, 2.6f, TitleGreen);
        DrawCentered("Dig deep - diamonds hide below y=6...", cy + 44, 1f, Yellow);

        for (int i = 0; i < PauseButtons.Length; i++)
        {
            var r = PauseButtonRect(i);
            bool hover = Inside(r, mx, my);
            Batch.SolidQuad(_white, r.X, r.Y, r.W, r.H,
                i == 0 ? (hover ? new Vector4(0.16f, 0.32f, 0.14f, 0.92f) : new Vector4(0.10f, 0.22f, 0.10f, 0.85f))
                       : (hover ? SlotSelBg : SlotBg));
            DrawBorder(r.X, r.Y, r.W, r.H, 2, hover ? Yellow : BorderCol);
            _font.Draw(Batch, PauseButtons[i],
                r.X + (r.W - _font.Measure(PauseButtons[i], 1.2f)) / 2, r.Y + 13, White, 1.2f);
        }

        string[] lines =
        {
            "W A S D          Move",
            "Space            Jump",
            "Shift            Sneak / fly down",
            "Ctrl             Sprint",
            "Hold Left Click  Mine block",
            "Right Click      Place block / use door, chest...",
            "1-9 / Wheel      Select item",
            "E                Inventory & crafting",
            "M                World map",
            "G                Creative / survival mode",
            "Space x2         Fly (creative)",
            "F5               Save world (also auto-saves on exit)",
            "Esc              Pause / release mouse",
        };
        float y = PauseButtonRect(PauseButtons.Length - 1).Y + 74;
        foreach (var line in lines)
        {
            DrawCentered(line, y, 0.95f, White with { W = 0.85f });
            y += _font.CellH + 2;
        }
    }

    /// 0 = Resume, 1 = Settings, 2 = Save & Quit to Menu.
    public int? HitPauseButton(float mx, float my)
    {
        for (int i = 0; i < PauseButtons.Length; i++)
            if (Inside(PauseButtonRect(i), mx, my)) return i;
        return null;
    }

    void DrawCentered(string text, float y, float scale, Vector4 color)
    {
        float w = _font.Measure(text, scale);
        _font.Draw(Batch, text, (_screenW - w) / 2, y, color, scale);
    }

    // ------------------------------------------------------------- slot helpers

    void DrawSlot(float x, float y, ItemStack stack, bool selected, string key) =>
        DrawSlotAt(x, y, stack, selected, key);

    void DrawSlotAt(float x, float y, ItemStack stack, bool selected, string key)
    {
        Batch.SolidQuad(_white, x, y, Slot, Slot, selected ? SlotSelBg : SlotBg);
        DrawBorder(x, y, Slot, Slot, 2, selected ? Yellow : BorderCol);
        if (key != null)
            _font.Draw(Batch, key, x + 3, y + 1, new Vector4(1, 1, 1, 0.6f), 0.8f);
        if (stack == null) return;

        _icons.Draw(Batch, stack.Id, x + 8, y + 8, 32);
        if (stack.Count > 1)
        {
            string count = stack.Count.ToString();
            _font.Draw(Batch, count, x + Slot - 4 - _font.Measure(count), y + Slot - _font.CellH, White);
        }
        int maxDur = ItemRegistry.MaxDurability(stack.Id);
        if (maxDur > 0 && stack.Durability < maxDur)
        {
            float pct = stack.Durability / (float)maxDur;
            Batch.SolidQuad(_white, x + 5, y + Slot - 7, Slot - 10, 3, new Vector4(0, 0, 0, 0.6f));
            var color = pct > 0.5f ? new Vector4(0.3f, 0.8f, 0.2f, 1) : pct > 0.25f ? new Vector4(0.9f, 0.8f, 0.1f, 1) : new Vector4(0.9f, 0.2f, 0.1f, 1);
            Batch.SolidQuad(_white, x + 5, y + Slot - 7, (Slot - 10) * pct, 3, color);
        }
    }

    void DrawBorder(float x, float y, float w, float h, float t, Vector4 color)
    {
        Batch.SolidQuad(_white, x, y, w, t, color);
        Batch.SolidQuad(_white, x, y + h - t, w, t, color);
        Batch.SolidQuad(_white, x, y + t, t, h - 2 * t, color);
        Batch.SolidQuad(_white, x + w - t, y + t, t, h - 2 * t, color);
    }
}
