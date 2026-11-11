using Microsoft.Xna.Framework;
using StardewValley;

namespace ArcaneTravelCircle.Services;

public static class InputSurface
{
    /// <summary>Let native HUD controls consume toolbar clicks before any world action.</summary>
    public static bool IsOverHud(Point uiMouse)
        => Game1.IsHudDrawn && Game1.onScreenMenus.Any(menu => menu.isWithinBounds(uiMouse.X, uiMouse.Y));
}
