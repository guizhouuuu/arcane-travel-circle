using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace ArcaneTravelCircle.Services;

/// <summary>Reads the existing map page, including NPC Map Locations' MapPage subclass.</summary>
public static class MapViewAdapter
{
    public static bool TryGetActiveMap(out MapPage page)
    {
        page = null!;
        IClickableMenu? active = Game1.activeClickableMenu;
        if (active is null || active.GetChildMenu() is not null)
            return false;

        if (active is GameMenu menu)
        {
            if (menu.currentTab != GameMenu.mapTab || menu.GetCurrentPage() is not MapPage map)
                return false;

            page = map;
            return true;
        }

        if (active is MapPage standaloneMap)
        {
            page = standaloneMap;
            return true;
        }

        return false;
    }

    public static bool IsInsideCanvas(MapPage page, Point uiMouse)
    {
        // A tab or close control must keep its normal action even if a mod moves it over the map.
        if (Game1.activeClickableMenu is GameMenu menu)
        {
            if (menu.upperRightCloseButton?.containsPoint(uiMouse.X, uiMouse.Y) == true)
                return false;
            foreach (ClickableComponent tab in menu.tabs)
            {
                if (tab.containsPoint(uiMouse.X, uiMouse.Y))
                    return false;
            }
        }
        if (page.upperRightCloseButton?.containsPoint(uiMouse.X, uiMouse.Y) == true)
            return false;

        return GetCanvasBounds(page.mapBounds, Game1.pixelZoom).Contains(uiMouse);
    }

    /// <summary>
    /// In 1.6.15 mapBounds has a UI-pixel origin but unscaled width/height.
    /// MapPage.drawMap multiplies its size by pixelZoom when drawing the map.
    /// </summary>
    public static Rectangle GetCanvasBounds(Rectangle mapBounds, int pixelZoom)
    {
        return new Rectangle(mapBounds.X, mapBounds.Y, checked(mapBounds.Width * pixelZoom), checked(mapBounds.Height * pixelZoom));
    }
}
