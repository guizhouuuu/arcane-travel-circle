using System.Reflection;
using System.Runtime.Serialization;
using ArcaneTravelCircle.Services;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.WorldMaps;
using StardewValley.Menus;
using StardewValley.WorldMaps;

internal static class BeachResolverRegression
{
    public static void Run(string gameDirectory, Action<bool, string> check)
    {
        // Read actual game data and exercise the complete resolver in an isolated,
        // headless Game1. There is no graphics device, game process, or save.
        Game1.game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        Game1.game1._locations = new List<GameLocation>();
        using var content = new LocalizedContentManager(new GameServiceContainer(), Path.Combine(gameDirectory, "Content"));
        Game1.content = content;
        Game1.locationData = content.Load<Dictionary<string, StardewValley.GameData.Locations.LocationData>>("Data/Locations");
        Game1.locationContextData = content.Load<Dictionary<string, StardewValley.GameData.LocationContexts.LocationContextData>>("Data/LocationContexts");
        Game1._locationLookup.Clear();
        foreach (string name in new[] { "Beach", "BeachNightMarket", "ElliottHouse", "FishShop", "Submarine", "MermaidHouse" })
        {
            var location = new GameLocation();
            // Name has no setter in the actual game; populate only this isolated fixture.
#pragma warning disable AvoidNetField
            location.name.Value = name;
#pragma warning restore AvoidNetField
            location.map = content.Load<xTile.Map>("Maps/" + (name == "BeachNightMarket" ? "Beach-NightMarket" : name));
            location.IsOutdoors = name.StartsWith("Beach", StringComparison.Ordinal);
            location.DisplayName = name;
            string[] tokens = location.map.Properties.TryGetValue("Warp", out var warps)
                ? warps.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            for (int i = 0; i < tokens.Length; i += 5)
                location.warps.Add(new Warp(int.Parse(tokens[i]), int.Parse(tokens[i + 1]), tokens[i + 2],
                    int.Parse(tokens[i + 3]), int.Parse(tokens[i + 4]), false));
            Game1._locationLookup[name] = location;
            Game1.locations.Add(location);
        }
        var data = content.Load<Dictionary<string, WorldMapRegionData>>("Data/WorldMap");
        var region = new MapRegion("Valley", data["Valley"]);
        MapArea beach = new(region, region.Data.MapAreas.Single(area => area.Id == "Beach"));
        MapArea night = new(region, region.Data.MapAreas.Single(area => area.Id == "BeachNightMarket"));
        check(beach.Data.PixelArea == night.Data.PixelArea
            && night.Data.WorldPositions.All(position => position.Condition is null),
            "Actual WorldMap: unconditional Night Market positions overlap the entire regular Beach");
        check(MapTargetResolver.IsVanillaAlternateBeach(night) && !MapTargetResolver.IsVanillaAlternateBeach(beach),
            "Only the exact alternate Night Market area is excluded from regular Beach arbitration");
        var page = (MapPage)FormatterServices.GetUninitializedObject(typeof(MapPage));
        typeof(MapPage).GetField("mapRegion")!.SetValue(page, region);
        typeof(MapPage).GetField("mapAreas")!.SetValue(page, new[] { beach, night });
        page.mapBounds = new Rectangle(145, 68, 300, 180);
        var resolver = new MapTargetResolver();
        Point Screen(int x, int y) => new(page.mapBounds.X + x * 4, page.mapBounds.Y + y * 4);
        bool Resolve(int x, int y, out ArcaneTravelCircle.Models.TravelTarget? target)
            => resolver.TryResolve(page, Screen(x, y), out target, out _);
        check(Resolve(205, 145, out var first) && first!.Location.Name == "Beach",
            "Full resolver + overlapping actual data: sand beside Elliott's cabin resolves to Beach");
        check(Resolve(222, 146, out var second) && second!.Location.Name == "Beach"
            && first!.Tile != second.Tile,
            "Full resolver: separated Beach clicks keep different requested tiles");
        check(Resolve(210, 140, out var cabin) && cabin!.MappingId.EndsWith(":outdoor-exit")
            && cabin.Location.Name == "Beach"
            && Game1._locationLookup["ElliottHouse"].warps.Any(warp => warp.TargetName == "Beach"
                && new Point(warp.TargetX, warp.TargetY) == cabin.Tile),
            "Full resolver: formerly dead cabin icon edge uses the actual ElliottHouse outdoor warp");
        Point beforeMove = first!.Tile;
        page.mapBounds = new Rectangle(300, 180, 300, 180);
        check(Resolve(205, 145, out var shifted) && shifted!.Tile == beforeMove,
            "Full resolver: relocating the map for a different window preserves the Beach destination");
        check(!resolver.TryResolve(page, new Point(page.mapBounds.X - 1, page.mapBounds.Y), out _, out _),
            "Full resolver: a click outside the map cannot become a Beach destination");
        // The same real requested sand tile must connect to a true exterior exit.
        var map = Game1._locationLookup["Beach"].Map;
        var exits = Game1._locationLookup["Beach"].warps.Where(warp => warp.TargetName == "Town")
            .Select(warp => new Point(warp.X, warp.Y)).ToHashSet();
        bool Walk(Point tile) => SafeLandingFinder.IsStaticTileWalkable(map, tile);
        bool Exit(Point tile) => new[] { new Point(tile.X - 1, tile.Y), new(tile.X + 1, tile.Y),
            new(tile.X, tile.Y - 1), new(tile.X, tile.Y + 1) }.Any(exits.Contains);
        bool found = SafeLandingFinder.GetCandidates(first.Tile, 8).Any(tile => Walk(tile)
            && SafeLandingFinder.HasReachableExit(tile, Walk, Exit, new Dictionary<Point, bool>()));
        check(found, "Actual clicked Beach destination has a nearby walkable route to the real Town exit");
        Game1.game1 = null;
    }
}
