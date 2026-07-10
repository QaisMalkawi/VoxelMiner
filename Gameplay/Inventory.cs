namespace VoxelMiner.Gameplay;

using VoxelMiner.World;

public sealed class ItemStack
{
    public int Id;
    public int Count;
    public int Durability; // remaining, for tools
}

/// Inventory model: 36 slots (0-8 hotbar), stacking, cursor stack for
/// drag-and-drop, tool durability. Pure state — no rendering.
public sealed class Inventory
{
    public const int Size = 36;
    public const int HotbarSize = 9;

    public readonly ItemStack[] Slots = new ItemStack[Size];
    public int Selected;
    public ItemStack Cursor; // stack held by the mouse while the panel is open

    public ItemStack SelectedItem => Slots[Selected];

    /// Empties everything (leaving a world for the menu).
    public void Reset()
    {
        Array.Clear(Slots, 0, Slots.Length);
        Cursor = null;
        Selected = 0;
    }

    public void SelectSlot(int i) => Selected = i;

    public void CycleSelection(int dir) => Selected = (Selected + dir + HotbarSize) % HotbarSize;

    /// Returns true if everything fit.
    public bool AddItem(int id, int n = 1)
    {
        int max = ItemRegistry.StackOf(id);
        for (int i = 0; i < Size && n > 0; i++)
        {
            var s = Slots[i];
            if (s != null && s.Id == id && s.Count < max)
            {
                int t = Math.Min(max - s.Count, n);
                s.Count += t;
                n -= t;
            }
        }
        for (int i = 0; i < Size && n > 0; i++)
        {
            if (Slots[i] == null)
            {
                int t = Math.Min(max, n);
                Slots[i] = new ItemStack { Id = id, Count = t, Durability = ItemRegistry.MaxDurability(id) };
                n -= t;
            }
        }
        return n == 0;
    }

    public void RemoveItem(int id, int n)
    {
        for (int i = 0; i < Size && n > 0; i++)
        {
            var s = Slots[i];
            if (s != null && s.Id == id)
            {
                int t = Math.Min(s.Count, n);
                s.Count -= t;
                n -= t;
                if (s.Count == 0) Slots[i] = null;
            }
        }
    }

    public int CountItem(int id)
    {
        int n = 0;
        foreach (var s in Slots) if (s != null && s.Id == id) n += s.Count;
        return n;
    }

    public bool CanFit(int id, int n)
    {
        int max = ItemRegistry.StackOf(id);
        int cap = 0;
        foreach (var s in Slots)
        {
            if (s == null) cap += max;
            else if (s.Id == id) cap += max - s.Count;
            if (cap >= n) return true;
        }
        return false;
    }

    /// Pick up / place / merge / swap with the cursor stack.
    public void ClickSlot(int i) => ClickSlotIn(Slots, i);

    /// Same cursor interaction against any slot array (chest, furnace...).
    public void ClickSlotIn(ItemStack[] slots, int i)
    {
        var s = slots[i];
        if (Cursor == null)
        {
            if (s != null) { Cursor = s; slots[i] = null; }
        }
        else if (s == null)
        {
            slots[i] = Cursor;
            Cursor = null;
        }
        else if (s.Id == Cursor.Id)
        {
            int t = Math.Min(ItemRegistry.StackOf(s.Id) - s.Count, Cursor.Count);
            if (t <= 0) { slots[i] = Cursor; Cursor = s; } // full or unstackable → swap
            else
            {
                s.Count += t;
                Cursor.Count -= t;
                if (Cursor.Count <= 0) Cursor = null;
            }
        }
        else
        {
            slots[i] = Cursor;
            Cursor = s;
        }
    }

    /// Take-only slot (furnace output): picks the stack up or merges it into
    /// a matching cursor stack, never puts anything in.
    public void TakeFromSlot(ItemStack[] slots, int i)
    {
        var s = slots[i];
        if (s == null) return;
        if (Cursor == null)
        {
            Cursor = s;
            slots[i] = null;
        }
        else if (Cursor.Id == s.Id)
        {
            int t = Math.Min(ItemRegistry.StackOf(s.Id) - Cursor.Count, s.Count);
            Cursor.Count += t;
            s.Count -= t;
            if (s.Count <= 0) slots[i] = null;
        }
    }

    /// Put whatever is on the cursor back into the bags (called on panel close).
    public void ReturnCursor()
    {
        if (Cursor == null) return;
        var c = Cursor;
        Cursor = null;
        AddItem(c.Id, c.Count);
    }

    /// Decrement the selected stack by one (placing a block).
    public void ConsumeSelected()
    {
        var s = Slots[Selected];
        if (s == null) return;
        s.Count--;
        if (s.Count <= 0) Slots[Selected] = null;
    }

    /// Apply one point of wear to the held tool. Returns broken tool name, or null.
    public string WearSelectedTool()
    {
        var s = Slots[Selected];
        if (s == null || ItemRegistry.ToolOf(s.Id) == null) return null;
        s.Durability--;
        if (s.Durability <= 0)
        {
            string name = ItemRegistry.NameOf(s.Id);
            Slots[Selected] = null;
            return name;
        }
        return null;
    }

    public bool IsTool(ItemStack item, out ToolSpec tool)
    {
        tool = null;

        if (item == null) return false;
        tool = ItemRegistry.ToolOf(item.Id);
        return tool != null;
    }
}

/// Crafting: recipe affordability and execution against an Inventory.
public static class Crafting
{
    public enum Result { Ok, Missing, Full }

    public static bool CanCraft(Inventory inv, Recipe r)
    {
        foreach (var (id, n) in r.In) if (inv.CountItem(id) < n) return false;
        return true;
    }

    public static Result Craft(Inventory inv, Recipe r)
    {
        if (!CanCraft(inv, r)) return Result.Missing;
        foreach (var (id, n) in r.In) inv.RemoveItem(id, n);
        if (!inv.CanFit(r.Out.Id, r.Out.Count))
        {
            foreach (var (id, n) in r.In) inv.AddItem(id, n); // refund
            return Result.Full;
        }
        inv.AddItem(r.Out.Id, r.Out.Count);
        return Result.Ok;
    }
}
