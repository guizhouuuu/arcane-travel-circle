using ArcaneTravelCircle.Models;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ArcaneTravelCircle.Services;

/// <summary>Projects actual built circles into a shared travel network.</summary>
public sealed class TravelNetwork
{
    private readonly MiniCircleService minis;
    private readonly ITranslationHelper translations;

    public TravelNetwork(MiniCircleService minis, ITranslationHelper translations)
    {
        this.minis = minis;
        this.translations = translations;
    }

    public IReadOnlyList<TravelNode> GetNodes()
    {
        var nodes = new List<TravelNode>();
        if (!Context.IsWorldReady)
            return nodes;

        Farm farm = Game1.getFarm();
        int number = 0;
        foreach (var building in farm.buildings.Where(building =>
                     building.buildingType.Value == CircleRegistry.BuildingId
                     && !building.isUnderConstruction(ignoreUpgrades: false))
                 .OrderBy(building => building.tileY.Value).ThenBy(building => building.tileX.Value))
        {
            number++;
            Point tile = new(building.tileX.Value + building.tilesWide.Value / 2,
                building.tileY.Value + building.tilesHigh.Value / 2);
            nodes.Add(new TravelNode($"large:{building.tileX.Value},{building.tileY.Value}", farm, tile,
                this.translations.Get("network.large", new { number }), true));
        }

        foreach (var circle in this.minis.GetPlaced().OrderBy(circle => circle.Number))
        {
            GameLocation? location = Game1.getLocationFromName(circle.LocationName);
            if (location is null || !location.IsOutdoors || location.IsTemporary)
                continue;
            nodes.Add(new TravelNode(circle.Id, location, circle.Tile,
                this.translations.Get("network.mini", new { number = circle.Number, location = location.DisplayName }), false));
        }
        return nodes;
    }

    public bool TryGetAt(GameLocation location, Point clicked, out TravelNode? node)
    {
        node = this.GetNodes().FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Location, location) && GetFootprint(candidate).Contains(clicked));
        return node is not null;
    }

    public bool IsAvailableOrigin(TravelNode origin, Farmer player)
    {
        TravelNode? existing = this.GetNodes().FirstOrDefault(node => node.Id == origin.Id);
        return existing is not null && ReferenceEquals(existing.Location, player.currentLocation)
            && IsWithinReach(GetFootprint(existing), player.TilePoint);
    }

    public static Rectangle GetFootprint(TravelNode node)
        => node.IsLarge ? new Rectangle(node.Tile.X - 1, node.Tile.Y - 1, 3, 3)
            : new Rectangle(node.Tile.X, node.Tile.Y, 1, 1);

    /// <summary>Vanilla-style interaction from an adjacent tile, or from on the circle.</summary>
    public static bool IsWithinReach(Rectangle footprint, Point playerTile)
    {
        footprint.Inflate(1, 1);
        return footprint.Contains(playerTile);
    }
}
