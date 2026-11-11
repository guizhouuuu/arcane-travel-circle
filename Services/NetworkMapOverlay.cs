using ArcaneTravelCircle.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.WorldMaps;

namespace ArcaneTravelCircle.Services;

/// <summary>Draws destination buttons over the existing map without replacing its page or markers.</summary>
public sealed class NetworkMapOverlay
{
    private const int MarkerSize = 30;
    private const int PanelPadding = 8;
    private const int HeaderHeight = 27;
    private const int RowHeight = 34;
    private const int FooterHeight = 23;
    private readonly IModHelper helper;
    private Texture2D? circleTexture;
    private int scrollOffset;

    public NetworkMapOverlay(IModHelper helper)
    {
        this.helper = helper;
    }

    /// <summary>SMAPI requires PNG decoding to happen outside the draw loop.</summary>
    public void PreloadTexture()
        => this.circleTexture ??= this.helper.ModContent.Load<Texture2D>("assets/circle.png");

    public void Draw(MapPage page, IReadOnlyList<TravelNode> nodes, Point uiMouse, SpriteBatch batch)
    {
        if (this.circleTexture is null)
            return;
        Layout layout = this.CreateLayout(page, nodes);

        foreach (Marker marker in layout.Markers)
        {
            if (!marker.InSidebar && marker.Bounds.Center != marker.Anchor)
            {
                DrawLine(batch, marker.Anchor.ToVector2(), marker.Bounds.Center.ToVector2(),
                    GetColor(marker.Node) * 0.8f);
                batch.Draw(Game1.staminaRect, new Rectangle(marker.Anchor.X - 2, marker.Anchor.Y - 2, 4, 4),
                    GetColor(marker.Node));
            }
        }

        foreach (Marker marker in layout.Markers.Where(marker => !marker.InSidebar))
            this.DrawMarker(batch, marker, marker.Bounds.Contains(uiMouse));

        if (layout.SidebarBounds is not Rectangle sidebar)
            return;

        batch.Draw(Game1.staminaRect, sidebar, new Color(22, 18, 36) * 0.95f);
        DrawBorder(batch, sidebar, new Color(137, 109, 180), 2);
        string title = this.Translate("network.sidebar.title", "Other-region circles");
        DrawLabel(batch, title, new Vector2(sidebar.X + PanelPadding, sidebar.Y + 7),
            sidebar.Width - PanelPadding * 2, Color.White, 0.67f);

        foreach (Marker marker in layout.Markers.Where(marker => marker.InSidebar))
        {
            bool hovered = marker.Bounds.Contains(uiMouse);
            if (hovered)
                batch.Draw(Game1.staminaRect, marker.Bounds, new Color(87, 65, 119) * 0.9f);
            Rectangle icon = new(marker.Bounds.X + 3, marker.Bounds.Y + 4, 24, 24);
            this.DrawCircle(batch, marker.Node, icon, hovered);
            DrawLabel(batch, marker.Node.Name,
                new Vector2(marker.Bounds.X + 34, marker.Bounds.Y + 7), marker.Bounds.Width - 39,
                hovered ? Color.White : new Color(235, 225, 247), 0.66f);
        }

        if (layout.SidebarCount > 0)
        {
            int first = layout.FirstSidebarIndex + 1;
            int last = Math.Min(layout.SidebarCount, layout.FirstSidebarIndex + layout.VisibleRows);
            string caption = $"{first}–{last}/{layout.SidebarCount}";
            if (layout.SidebarCount > layout.VisibleRows)
                caption += "  " + this.Translate("network.sidebar.scroll", "Scroll for more");
            DrawLabel(batch, caption, new Vector2(sidebar.X + PanelPadding, sidebar.Bottom - FooterHeight + 3),
                sidebar.Width - PanelPadding * 2, new Color(201, 189, 218), 0.55f);
        }
    }

    /// <summary>Uses exactly the same layout as drawing, including displaced and off-region buttons.</summary>
    public TravelNode? HitTest(MapPage page, IReadOnlyList<TravelNode> nodes, Point uiMouse)
    {
        if (!MapViewAdapter.IsInsideCanvas(page, uiMouse))
            return null;

        Layout layout = this.CreateLayout(page, nodes);
        return layout.Markers.FirstOrDefault(marker => marker.Bounds.Contains(uiMouse))?.Node;
    }

    /// <summary>Includes the header and footer so those clicks never pass through to the underlying map.</summary>
    public bool IsOverSidebar(MapPage page, IReadOnlyList<TravelNode> nodes, Point uiMouse)
    {
        if (!MapViewAdapter.IsInsideCanvas(page, uiMouse))
            return false;
        return this.CreateLayout(page, nodes).SidebarBounds?.Contains(uiMouse) == true;
    }

    public void ScrollSidebar(int wheelDelta)
    {
        if (wheelDelta != 0)
            this.scrollOffset = Math.Max(0, this.scrollOffset + (wheelDelta > 0 ? -1 : 1));
    }

    /// <summary>
    /// Pure placement helper: shifts crowded markers at most four grid steps, preserves the canvas
    /// boundary and leaves a gap around occupied controls/buttons. Null means use the named sidebar.
    /// </summary>
    public static Rectangle? FindMarkerBounds(Point anchor, Rectangle canvas,
        IReadOnlyList<Rectangle> occupied, int markerSize = MarkerSize)
    {
        const int inset = 3;
        if (markerSize <= 0 || canvas.Width < markerSize + inset * 2 || canvas.Height < markerSize + inset * 2)
            return null;

        int step = markerSize + 6;
        IEnumerable<Point> offsets = Enumerable.Range(-4, 9)
            .SelectMany(y => Enumerable.Range(-4, 9).Select(x => new Point(x, y)))
            .Where(offset => offset.X * offset.X + offset.Y * offset.Y <= 16)
            .OrderBy(offset => offset.X * offset.X + offset.Y * offset.Y)
            .ThenBy(offset => offset.Y).ThenBy(offset => offset.X);

        foreach (Point offset in offsets)
        {
            int x = Math.Clamp(anchor.X - markerSize / 2 + offset.X * step,
                canvas.Left + inset, canvas.Right - inset - markerSize);
            int y = Math.Clamp(anchor.Y - markerSize / 2 + offset.Y * step,
                canvas.Top + inset, canvas.Bottom - inset - markerSize);
            Rectangle candidate = new(x, y, markerSize, markerSize);
            Rectangle padded = candidate;
            padded.Inflate(2, 2);
            if (occupied.All(rectangle => !padded.Intersects(rectangle)))
                return candidate;
        }
        return null;
    }

    private Layout CreateLayout(MapPage page, IReadOnlyList<TravelNode> nodes)
    {
        Rectangle canvas = MapViewAdapter.GetCanvasBounds(page.mapBounds, Game1.pixelZoom);
        // UI zoom can crop the vanilla map on small windows. Off-screen endpoints still need
        // a visible named button, and the sidebar must stay inside the actual UI viewport.
        if (Game1.uiViewport.Width > 0 && Game1.uiViewport.Height > 0)
            canvas = Rectangle.Intersect(canvas, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height));
        List<Rectangle> controls = GetControlBounds(page);
        List<ProjectedNode> projected = new();
        List<TravelNode> offRegion = new();
        foreach (TravelNode node in nodes.OrderByDescending(node => node.IsLarge).ThenBy(node => node.Id, StringComparer.Ordinal))
        {
            MapAreaPositionWithContext? position = WorldMapManager.GetPositionData(node.Location, node.Tile);
            if (position is not MapAreaPositionWithContext known
                || !ReferenceEquals(known.Location, node.Location)
                || !string.Equals(known.Data.Region.Id, page.mapRegion.Id, StringComparison.Ordinal))
            {
                offRegion.Add(node);
                continue;
            }

            // Valley's Ginger Island overview and the desert's one-pixel marker do not encode
            // an endpoint's actual tile. Keep those destinations named and selectable in the
            // sidebar instead of drawing every endpoint at a misleading shared overview dot.
            Rectangle mappedArea = known.Data.GetPixelArea();
            if (mappedArea.Width <= Game1.pixelZoom && mappedArea.Height <= Game1.pixelZoom)
            {
                offRegion.Add(node);
                continue;
            }

            Vector2 pixel = known.GetMapPixelPosition();
            if (!float.IsFinite(pixel.X) || !float.IsFinite(pixel.Y))
            {
                offRegion.Add(node);
                continue;
            }
            Point anchor = new(page.mapBounds.X + (int)MathF.Round(pixel.X),
                page.mapBounds.Y + (int)MathF.Round(pixel.Y));
            if (!canvas.Contains(anchor))
            {
                offRegion.Add(node);
                continue;
            }
            projected.Add(new ProjectedNode(node, anchor));
        }

        // A very crowded group may overflow into the sidebar. Re-layout after enlarging the panel,
        // so its final footprint never hides a button that was already placed underneath it.
        HashSet<string> overflow = new(StringComparer.Ordinal);
        while (true)
        {
            List<TravelNode> sidebarNodes = offRegion.Concat(projected.Where(item => overflow.Contains(item.Node.Id))
                    .Select(item => item.Node)).OrderBy(node => node.Name, StringComparer.CurrentCulture)
                .ThenBy(node => node.Id, StringComparer.Ordinal).ToList();
            Rectangle? sidebar = sidebarNodes.Count > 0 ? GetSidebarBounds(canvas, sidebarNodes.Count, controls) : null;
            List<Rectangle> occupied = new(controls);
            if (sidebar.HasValue)
                occupied.Add(sidebar.Value);
            List<Marker> markers = new();
            bool needsAnotherPass = false;
            foreach (ProjectedNode item in projected.Where(item => !overflow.Contains(item.Node.Id)))
            {
                Rectangle? bounds = FindMarkerBounds(item.Anchor, canvas, occupied);
                if (!bounds.HasValue)
                {
                    overflow.Add(item.Node.Id);
                    needsAnotherPass = true;
                    continue;
                }
                occupied.Add(bounds.Value);
                markers.Add(new Marker(item.Node, bounds.Value, item.Anchor, false));
            }
            if (needsAnotherPass)
                continue;

            int visibleRows = sidebar.HasValue ? Math.Max(0,
                (sidebar.Value.Height - PanelPadding * 2 - HeaderHeight - FooterHeight) / RowHeight) : 0;
            this.scrollOffset = Math.Clamp(this.scrollOffset, 0, Math.Max(0, sidebarNodes.Count - visibleRows));
            if (sidebar.HasValue)
            {
                for (int row = 0; row < visibleRows && this.scrollOffset + row < sidebarNodes.Count; row++)
                {
                    Rectangle bounds = new(sidebar.Value.X + PanelPadding,
                        sidebar.Value.Y + PanelPadding + HeaderHeight + row * RowHeight,
                        sidebar.Value.Width - PanelPadding * 2, RowHeight);
                    markers.Add(new Marker(sidebarNodes[this.scrollOffset + row], bounds, bounds.Center, true));
                }
            }
            return new Layout(markers, sidebar, sidebarNodes.Count, visibleRows, this.scrollOffset);
        }
    }

    private static Rectangle? GetSidebarBounds(Rectangle canvas, int count, IReadOnlyList<Rectangle> controls)
    {
        const int margin = 9;
        int width = Math.Min(284, canvas.Width - margin * 2);
        int maxRows = Math.Min(6, (canvas.Height - margin * 2 - HeaderHeight - FooterHeight - PanelPadding * 2) / RowHeight);
        if (width < 96 || maxRows < 1)
            return null;
        int height = PanelPadding * 2 + HeaderHeight + FooterHeight + Math.Min(count, maxRows) * RowHeight;
        Rectangle[] corners =
        {
            new(canvas.Right - width - margin, canvas.Bottom - height - margin, width, height),
            new(canvas.Left + margin, canvas.Bottom - height - margin, width, height),
            new(canvas.Right - width - margin, canvas.Top + margin, width, height),
            new(canvas.Left + margin, canvas.Top + margin, width, height)
        };
        foreach (Rectangle corner in corners)
        {
            if (controls.All(control => !corner.Intersects(control)))
                return corner;
        }
        return null;
    }

    private static List<Rectangle> GetControlBounds(MapPage page)
    {
        List<Rectangle> bounds = new();
        if (Game1.activeClickableMenu is GameMenu menu)
        {
            bounds.AddRange(menu.tabs.Select(tab => tab.bounds));
            if (menu.upperRightCloseButton is not null)
                bounds.Add(menu.upperRightCloseButton.bounds);
        }
        if (page.upperRightCloseButton is not null)
            bounds.Add(page.upperRightCloseButton.bounds);
        return bounds;
    }

    private void DrawMarker(SpriteBatch batch, Marker marker, bool hovered)
    {
        this.DrawCircle(batch, marker.Node, marker.Bounds, hovered);
        string badge = marker.Node.IsLarge ? "L" : GetSmallBadge(marker.Node.Name);
        Vector2 size = Game1.tinyFont.MeasureString(badge);
        batch.DrawString(Game1.tinyFont, badge,
            new Vector2(marker.Bounds.Center.X - size.X / 2, marker.Bounds.Bottom - size.Y + 2),
            Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 1f);
    }

    private void DrawCircle(SpriteBatch batch, TravelNode node, Rectangle bounds, bool hovered)
    {
        Color color = GetColor(node);
        batch.Draw(Game1.staminaRect, bounds, hovered ? Color.White : color);
        Rectangle inner = bounds;
        inner.Inflate(-2, -2);
        batch.Draw(Game1.staminaRect, inner, new Color(30, 22, 46));
        inner.Inflate(-1, -1);
        batch.Draw(this.circleTexture!, inner, Color.White);
    }

    private static Color GetColor(TravelNode node) => node.IsLarge ? new Color(245, 194, 72) : new Color(77, 195, 247);

    private static string GetSmallBadge(string name)
    {
        int start = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] >= '0' && name[i] <= '9')
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return "S";
        int end = start;
        while (end < name.Length && name[end] >= '0' && name[end] <= '9')
            end++;
        string number = name[start..end];
        return number.Length <= 3 ? number : "S";
    }

    private string Translate(string key, string fallback)
    {
        string value = this.helper.Translation.Get(key).ToString();
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private static void DrawLabel(SpriteBatch batch, string text, Vector2 position, int width, Color color, float scale)
    {
        string label = text;
        if (Game1.smallFont.MeasureString(label).X * scale > width)
        {
            while (label.Length > 0 && Game1.smallFont.MeasureString(label + "…").X * scale > width)
                label = label[..^1];
            label += "…";
        }
        batch.DrawString(Game1.smallFont, label, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }

    private static void DrawBorder(SpriteBatch batch, Rectangle bounds, Color color, int thickness)
    {
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), color);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), color);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), color);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), color);
    }

    private static void DrawLine(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
    {
        Vector2 delta = end - start;
        batch.Draw(Game1.staminaRect, start, null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero,
            new Vector2(delta.Length(), 2f), SpriteEffects.None, 0.99f);
    }

    private sealed record ProjectedNode(TravelNode Node, Point Anchor);
    private sealed record Marker(TravelNode Node, Rectangle Bounds, Point Anchor, bool InSidebar);
    private sealed record Layout(IReadOnlyList<Marker> Markers, Rectangle? SidebarBounds, int SidebarCount,
        int VisibleRows, int FirstSidebarIndex);
}
