using ArcaneTravelCircle.Models;
using Microsoft.Xna.Framework;

namespace ArcaneTravelCircle.Services;

/// <summary>Pure license invariants shared by the real purchase/placement flow and tests.</summary>
public static class MiniCircleLedger
{
    public const int PurchaseLimit = 10;

    public static bool CanPurchase(int purchasedCount, bool hasBuiltMainCircle)
        => hasBuiltMainCircle && purchasedCount is >= 0 and < PurchaseLimit;

    public static bool TryRecordPurchase(ICollection<MiniCircle> circles, bool hasBuiltMainCircle,
        string id, out MiniCircle? circle)
    {
        circle = null;
        if (!CanPurchase(circles.Count, hasBuiltMainCircle) || string.IsNullOrWhiteSpace(id)
            || circles.Any(existing => existing.Id == id))
            return false;

        int number = Enumerable.Range(1, PurchaseLimit)
            .FirstOrDefault(value => circles.All(existing => existing.Number != value));
        if (number == 0)
            return false;
        circle = new MiniCircle { Id = id, Number = number };
        circles.Add(circle);
        return true;
    }

    public static bool TryMarkPlaced(IReadOnlyList<MiniCircle> circles, string id, int tokenStack,
        string locationName, Point tile, out MiniCircle? circle)
    {
        circle = circles.FirstOrDefault(existing => existing.Id == id);
        if (tokenStack != 1 || circle is null || circle.IsPlaced || string.IsNullOrWhiteSpace(locationName)
            || circles.Any(existing => existing.IsPlaced && existing.LocationName == locationName && existing.Tile == tile))
        {
            circle = null;
            return false;
        }
        circle.LocationName = locationName;
        circle.TileX = tile.X;
        circle.TileY = tile.Y;
        return true;
    }

    public static void MarkPickedUp(MiniCircle circle)
    {
        circle.LocationName = "";
        circle.TileX = 0;
        circle.TileY = 0;
    }
}
