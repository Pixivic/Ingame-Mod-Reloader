using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace ModReloader;

public sealed class ReloaderUI : ModSystem
{
    private static readonly Color PanelFill = new Color(63, 82, 151) * 0.82f;
    private static readonly Color ListFill = new Color(34, 44, 92) * 0.88f;
    private static readonly Color SelectedRowFill = new(82, 102, 190);
    private static readonly Color SelectedRowBorder = new(248, 207, 105);
    private static readonly Color TextColor = Color.White;

    private Asset<Texture2D> _reloaderIcon;
    private Asset<Texture2D> _panelBackground;
    private Asset<Texture2D> _panelBorder;
    private Asset<Texture2D> _buttonBacking;
    private Asset<Texture2D> _starOn;
    private Asset<Texture2D> _starOff;
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModEntry> _entries = new();
    private bool _open;
    private bool _confirm;
    private bool _dragging;
    private int _selected = -1;
    private int _firstRow;
    private LegacyGameInterfaceLayer _layer;
    private string _countLabel;
    private int _labelWidth;
    private bool _ownsMouse;
    private Point _dragOffset;
    private Point _panelPosition;

    private const string PanelTitle = "Mod Reloader";
    private const int RowHeight = 29;
    private const int HeaderHeight = 36;
    private const int ListPadding = 10;
    private const int FooterGap = 5;
    private const int FooterHeight = 32;
    private const int BottomPadding = 8;
    private const int PanelChrome = HeaderHeight + ListPadding + FooterGap + FooterHeight + BottomPadding;
    private const float RowTextScale = 0.8f;
    private const int RowLabelOffset = 36;
    private const int StarInset = 30;

    private int VisibleRows => Math.Clamp((Main.screenHeight - PanelChrome) / RowHeight,
        1, Math.Clamp(_entries.Count, 1, 9));
    private static int PanelWidth => Math.Min(432, Main.screenWidth);
    private int PanelHeight => PanelChrome + VisibleRows * RowHeight;
    private int LastFirstRow => Math.Max(0, _entries.Count - VisibleRows);
    private bool HasSelection => _selected >= 0 && _selected < _entries.Count;
    private bool CanReload => HasSelection && !ReloadSession.Busy && Main.netMode == NetmodeID.SinglePlayer;

    private static bool CanShowInventory => !Main.gameMenu && Main.playerInventory && !Main.mapFullscreen
        && !Main.ingameOptionsWindow && !Main.inFancyUI && !Main.hideUI;

    public override void Load()
    {
        if (Main.dedServ)
            return;

        _reloaderIcon = LoadAsset("ReloaderIcon");
        _panelBackground = LoadAsset("PanelBackground");
        _panelBorder = LoadAsset("PanelBorder");
        _buttonBacking = LoadAsset("ButtonBacking");
        _starOn = LoadAsset("ButtonFavoriteActive");
        _starOff = LoadAsset("ButtonFavoriteInactive");
        LoadFavorites();
        _layer = new LegacyGameInterfaceLayer($"{ModIdentity.Name}: {PanelTitle}", DrawInventoryLayer, InterfaceScaleType.UI);
        On_Main.DrawInterface_27_Inventory += DrawInventory;
    }

    public override void Unload()
    {
        if (!Main.dedServ)
            On_Main.DrawInterface_27_Inventory -= DrawInventory;

        _layer = null;
        _entries.Clear();
        _favorites.Clear();
    }

    public override void PostUpdateInput()
    {
        _ownsMouse = false;
        if (!CanShowInventory)
        {
            ClosePanel();
            return;
        }

        PlayerInput.SetZoom_UI();
        try
        {
            UpdateInventoryPointer();
        }
        finally
        {
            PlayerInput.SetZoom_Unscaled();
        }
    }

    private void UpdateInventoryPointer()
    {
        Point mouse = Mouse;
        Rectangle panel = _open ? GetPanelRect() : Rectangle.Empty;
        bool overPanel = panel.Contains(mouse);
        bool overButton = GetReloaderButtonRect().Contains(mouse);
        _ownsMouse = overPanel || overButton || _dragging;
        if (!_ownsMouse)
            return;

        Main.LocalPlayer.mouseInterface = true;
        PlayerInput.LockVanillaMouseScroll(ModIdentity.Name);

        if (_dragging)
        {
            _dragging = PlayerInput.Triggers.Current.MouseLeft;
            if (_dragging)
            {
                _panelPosition = mouse - _dragOffset;
                ClampPanelPosition();
            }
            return;
        }

        bool clicked = TakePanelClick();
        if (overPanel)
        {
            UpdatePanel(panel, mouse, clicked);
            return;
        }

        if (!clicked)
            return;

        if (_open)
        {
            ClosePanel();
            return;
        }

        _open = true;
        RefreshList();
        _panelPosition = new Point((Main.screenWidth - PanelWidth) / 2,
            (Main.screenHeight - PanelHeight) / 2);
    }

    private static bool TakePanelClick()
    {
        if (!PlayerInput.Triggers.JustPressed.MouseLeft)
            return false;

        Main.mouseLeftRelease = false;
        return true;
    }

    private void DrawInventory(On_Main.orig_DrawInterface_27_Inventory orig, Main self)
    {
        if (!_ownsMouse || !CanShowInventory)
        {
            orig(self);
            return;
        }

        Point mouse = Mouse;
        Main.mouseX = Main.mouseY = -10000;
        try
        {
            orig(self);
        }
        finally
        {
            Main.mouseX = mouse.X;
            Main.mouseY = mouse.Y;
        }
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (!CanShowInventory || _layer == null)
            return;

        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Inventory");
        if (index >= 0)
            layers.Insert(index + 1, _layer);
    }

    private bool DrawInventoryLayer()
    {
        Point mouse = Mouse;
        Rectangle button = GetReloaderButtonRect();
        Rectangle panel = _open ? GetPanelRect() : Rectangle.Empty;
        bool hovered = button.Contains(mouse) && !panel.Contains(mouse) && !_dragging;

        Main.spriteBatch.Draw(_reloaderIcon.Value, button, hovered ? Color.White : new Color(228, 228, 228));
        if (hovered)
            Main.instance.MouseText(PanelTitle);

        if (_open)
            DrawPanel(panel, mouse);

        return true;
    }

    private Rectangle GetReloaderButtonRect()
    {
        const int height = 42;
        int width = (int)MathF.Round(height * _reloaderIcon.Value.Width / (float)_reloaderIcon.Value.Height);
        return new Rectangle(18, Math.Max(4, Main.screenHeight - height - 20), width, height);
    }

    private static Point Mouse => new(Main.mouseX, Main.mouseY);

    private void ClosePanel()
    {
        _open = false;
        _confirm = false;
        _dragging = false;
    }

    private static Asset<Texture2D> LoadAsset(string file) =>
        ModContent.Request<Texture2D>($"{ModIdentity.Name}/Assets/{file}", AssetRequestMode.ImmediateLoad);

    private static void DrawNineSlice(Texture2D texture, Rectangle bounds, int corner, int bar, Color tint)
    {
        if (bounds.Width < corner * 2 || bounds.Height < corner * 2)
            return;

        int middleWidth = bounds.Width - corner * 2;
        int middleHeight = bounds.Height - corner * 2;
        int farEdge = corner + bar;
        SpriteBatch batch = Main.spriteBatch;

        void Patch(Rectangle source, Rectangle destination)
        {
            if (source.Width > 0 && source.Height > 0 && destination.Width > 0 && destination.Height > 0)
                batch.Draw(texture, destination, source, tint);
        }

        Patch(new Rectangle(0, 0, corner, corner),
            new Rectangle(bounds.Left, bounds.Top, corner, corner));
        Patch(new Rectangle(farEdge, 0, corner, corner),
            new Rectangle(bounds.Right - corner, bounds.Top, corner, corner));
        Patch(new Rectangle(0, farEdge, corner, corner),
            new Rectangle(bounds.Left, bounds.Bottom - corner, corner, corner));
        Patch(new Rectangle(farEdge, farEdge, corner, corner),
            new Rectangle(bounds.Right - corner, bounds.Bottom - corner, corner, corner));

        Patch(new Rectangle(corner, 0, bar, corner),
            new Rectangle(bounds.Left + corner, bounds.Top, middleWidth, corner));
        Patch(new Rectangle(corner, farEdge, bar, corner),
            new Rectangle(bounds.Left + corner, bounds.Bottom - corner, middleWidth, corner));
        Patch(new Rectangle(0, corner, corner, bar),
            new Rectangle(bounds.Left, bounds.Top + corner, corner, middleHeight));
        Patch(new Rectangle(farEdge, corner, corner, bar),
            new Rectangle(bounds.Right - corner, bounds.Top + corner, corner, middleHeight));
        Patch(new Rectangle(corner, corner, bar, bar),
            new Rectangle(bounds.Left + corner, bounds.Top + corner, middleWidth, middleHeight));
    }

    private void DrawVanillaPanel(Rectangle rectangle, Color tint)
    {
        DrawNineSlice(_panelBackground.Value, rectangle, 12, 4, tint);
        DrawNineSlice(_panelBorder.Value, rectangle, 12, 4, Color.Black);
    }

    private void DrawVanillaButton(Rectangle bounds, bool hovered, bool selected = false)
    {
        Color tint = selected ? new Color(205, 223, 255) : hovered ? Color.White : new Color(210, 218, 241);
        DrawNineSlice(_buttonBacking.Value, bounds, 8, 6, tint);
    }

    private static void DrawLabel(string text, Vector2 position, float scale, Color? color = null) =>
        Utils.DrawBorderString(Main.spriteBatch, text, position, color ?? TextColor, scale);

    private static void DrawCentered(string text, Rectangle bounds, float scale, Color color)
    {
        Vector2 size = FontAssets.MouseText.Value.MeasureString(text);
        scale = Math.Min(scale, Math.Max(1, bounds.Width - 6) / size.X);
        DrawLabel(text, bounds.Center.ToVector2() - size * (scale * 0.5f), scale, color);
    }

    private static string FitLabel(string text, float maxWidth)
    {
        var font = FontAssets.MouseText.Value;
        if (font.MeasureString(text).X * RowTextScale <= maxWidth)
            return text;

        const string suffix = "...";
        if (font.MeasureString(suffix).X * RowTextScale > maxWidth)
            return string.Empty;

        int[] offsets = StringInfo.ParseCombiningCharacters(text);
        int first = 0;
        int last = offsets.Length - 1;
        while (first < last)
        {
            int middle = (first + last + 1) / 2;
            if (font.MeasureString(text[..offsets[middle]] + suffix).X * RowTextScale <= maxWidth)
                first = middle;
            else
                last = middle - 1;
        }
        return text[..offsets[first]] + suffix;
    }

    private static string FavoritesFile => Path.Combine(Main.SavePath, ModIdentity.Name + "_favorites.txt");

    private void LoadFavorites()
    {
        _favorites.Clear();
        try
        {
            string path = FavoritesFile;
            if (!File.Exists(path))
                return;

            foreach (string name in File.ReadAllLines(path))
            {
                if (!string.IsNullOrWhiteSpace(name))
                    _favorites.Add(name.Trim());
            }
        }
        catch (Exception ex)
        {
            Mod.Logger.Warn("Could not read favorites: " + ex.Message);
        }
    }

    private void SaveFavorites()
    {
        try
        {
            string path = FavoritesFile;
            Directory.CreateDirectory(Main.SavePath);
            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllLines(temporaryPath, _favorites.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex)
        {
            Mod.Logger.Warn("Could not save favorites: " + ex.Message);
        }
    }

    private void ToggleFavorite(string internalName)
    {
        string selectedName = HasSelection ? _entries[_selected].InternalName : null;
        bool added = _favorites.Add(internalName);
        if (!added)
            _favorites.Remove(internalName);
        SaveFavorites();
        SortEntries();
        _selected = selectedName == null ? -1 : _entries.FindIndex(e => e.InternalName == selectedName);
        if (added)
        {
            int index = _entries.FindIndex(e => e.InternalName == internalName);
            _firstRow = Math.Clamp(index, 0, LastFirstRow);
        }
    }

    private void SortEntries() => _entries.Sort((a, b) =>
    {
        int favoriteCompare = _favorites.Contains(b.InternalName).CompareTo(_favorites.Contains(a.InternalName));
        if (favoriteCompare != 0)
            return favoriteCompare;

        int nameCompare = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        return nameCompare != 0 ? nameCompare : StringComparer.Ordinal.Compare(a.InternalName, b.InternalName);
    });

    private void RefreshList()
    {
        _entries.Clear();
        foreach (Mod mod in ModLoader.Mods)
        {
            if (mod.Name is "ModLoader" or ModIdentity.Name)
                continue;

            var icon = mod.SmallModIcon ?? mod.ModIcon ?? Terraria.ModLoader.Mod.PlaceholderModIcon;
            _entries.Add(new ModEntry(mod.Name, mod.DisplayName, icon));
        }

        SortEntries();
        _countLabel = _entries.Count == 1 ? "1 Mod" : $"{_entries.Count} Mods";
        _labelWidth = -1;
        _selected = -1;
        _firstRow = 0;
    }

    private void ClampPanelPosition()
    {
        _panelPosition.X = Math.Clamp(_panelPosition.X, 0, Math.Max(0, Main.screenWidth - PanelWidth));
        _panelPosition.Y = Math.Clamp(_panelPosition.Y, 0, Math.Max(0, Main.screenHeight - PanelHeight));
    }

    private Rectangle GetPanelRect()
    {
        ClampPanelPosition();
        return new Rectangle(_panelPosition.X, _panelPosition.Y, PanelWidth, PanelHeight);
    }

    private static Rectangle GetPanelControlRect(Rectangle panel) => new(panel.Right - 34, panel.Y + 5, 26, 26);

    private Rectangle GetListRect(Rectangle panel) =>
        new(panel.X + 10, panel.Y + HeaderHeight, panel.Width - 20, VisibleRows * RowHeight + ListPadding);

    private static Rectangle GetRowRect(Rectangle list, int index) =>
        new(list.X + 5, list.Y + 5 + index * RowHeight, list.Width - 10, RowHeight - 2);

    private static Rectangle GetStarRect(Rectangle row) =>
        new(row.Right - StarInset, row.Y, 26, row.Height);

    private static Rectangle GetActionRect(Rectangle panel, Rectangle list)
    {
        int width = Math.Min(196, panel.Width / 2);
        return new Rectangle(panel.X + (panel.Width - width) / 2, list.Bottom + FooterGap, width, FooterHeight);
    }

    private void UpdatePanel(Rectangle panel, Point mouse, bool clicked)
    {
        Rectangle list = GetListRect(panel);
        _firstRow = Math.Clamp(_firstRow, 0, LastFirstRow);
        if (list.Contains(mouse))
        {
            int delta = PlayerInput.ScrollWheelDeltaForUI;
            if (delta != 0)
                _firstRow = Math.Clamp(_firstRow - Math.Sign(delta) * 3, 0, LastFirstRow);
        }

        if (!clicked)
            return;

        if (mouse.Y < list.Top)
        {
            if (GetPanelControlRect(panel).Contains(mouse))
            {
                ClosePanel();
                return;
            }

            _dragging = true;
            _dragOffset = mouse - panel.Location;
            return;
        }

        for (int i = 0; i < VisibleRows && _firstRow + i < _entries.Count; i++)
        {
            Rectangle row = GetRowRect(list, i);
            if (!row.Contains(mouse))
                continue;

            int index = _firstRow + i;
            if (GetStarRect(row).Contains(mouse))
                ToggleFavorite(_entries[index].InternalName);
            else
                _selected = index;
            _confirm = false;
            return;
        }

        if (!CanReload || !GetActionRect(panel, list).Contains(mouse))
            return;

        if (!_confirm)
        {
            _confirm = true;
            return;
        }

        if (ReloadSession.Start(_entries[_selected].InternalName))
            ClosePanel();
        else
            _confirm = false;
    }

    private void DrawPanel(Rectangle panel, Point mouse)
    {
        Rectangle control = GetPanelControlRect(panel);
        Rectangle list = GetListRect(panel);
        Rectangle action = GetActionRect(panel, list);

        DrawVanillaPanel(panel, PanelFill);
        int titleInset = panel.Right - control.Left;
        Rectangle title = new(panel.X + titleInset, panel.Y + 5, panel.Width - 2 * titleInset, HeaderHeight);
        DrawCentered(PanelTitle, title, 1f, TextColor);
        DrawPanelControl(control, mouse);

        DrawVanillaPanel(list, ListFill);
        Rectangle firstRowBounds = GetRowRect(list, 0);
        int maxLabelWidth = GetStarRect(firstRowBounds).Left - firstRowBounds.Left - RowLabelOffset - 4;
        if (_labelWidth != maxLabelWidth)
        {
            _labelWidth = maxLabelWidth;
            foreach (ModEntry entry in _entries)
                entry.Label = FitLabel(entry.DisplayName, maxLabelWidth);
        }

        int firstRow = Math.Clamp(_firstRow, 0, LastFirstRow);
        for (int i = 0; i < VisibleRows && firstRow + i < _entries.Count; i++)
            DrawEntryRow(GetRowRect(list, i), firstRow + i, mouse);

        if (_entries.Count == 0)
            DrawCentered("No loaded mods found", list, 0.82f, TextColor);

        DrawVanillaButton(action, action.Contains(mouse) && CanReload && !_dragging, _confirm);
        DrawCentered(_confirm ? "Confirm Reload" : "Reload Selected Mod", action, 0.78f,
            CanReload ? TextColor : new Color(185, 185, 185));

        Rectangle count = new(panel.X + 10, action.Y, action.Left - panel.X - 16, action.Height);
        Vector2 size = FontAssets.MouseText.Value.MeasureString(_countLabel);
        float scale = Math.Min(0.82f, Math.Max(1, count.Width) / size.X);
        DrawLabel(_countLabel, new Vector2(count.Left, count.Center.Y - size.Y * scale * 0.5f), scale);
    }

    private void DrawPanelControl(Rectangle button, Point mouse)
    {
        DrawVanillaButton(button, button.Contains(mouse) && !_dragging);
        DrawCentered("X", button, 0.72f, TextColor);
    }

    private void DrawEntryRow(Rectangle row, int entryIndex, Point mouse)
    {
        ModEntry entry = _entries[entryIndex];
        DrawVanillaButton(row, row.Contains(mouse) && !_dragging);
        if (_selected == entryIndex)
        {
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            Rectangle highlight = row;
            highlight.Inflate(-1, -1);
            Main.spriteBatch.Draw(pixel, highlight, SelectedRowBorder);
            highlight.Inflate(-1, -1);
            Main.spriteBatch.Draw(pixel, highlight, SelectedRowFill);
        }

        Texture2D icon = entry.Icon?.Value;
        if (icon != null)
        {
            float scale = 23f / Math.Max(icon.Width, icon.Height);
            Vector2 center = new(row.X + 18, row.Center.Y);
            Vector2 size = new(icon.Width * scale, icon.Height * scale);
            Main.spriteBatch.Draw(icon, center - size * 0.5f, null, Color.White, 0f,
                Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        Rectangle starBounds = GetStarRect(row);
        bool starHovered = starBounds.Contains(mouse) && !_dragging;
        bool isFavorite = _favorites.Contains(entry.InternalName);
        Texture2D star = (isFavorite ? _starOn : _starOff).Value;
        Vector2 starPosition = starBounds.Center.ToVector2() - new Vector2(star.Width, star.Height) * 0.5f;
        Main.spriteBatch.Draw(star, starPosition, starHovered ? Color.White : new Color(227, 227, 227));
        DrawLabel(entry.Label, new Vector2(row.X + RowLabelOffset, row.Y + 3), RowTextScale);

        if (starHovered)
            Main.instance.MouseText(isFavorite ? "Unfavorite" : "Favorite");
    }

    private sealed class ModEntry(string internalName, string displayName, Asset<Texture2D> icon)
    {
        public string InternalName { get; } = internalName;
        public string DisplayName { get; } = displayName;
        public Asset<Texture2D> Icon { get; } = icon;
        public string Label { get; set; }
    }
}
