using System.Reflection;
using System.Runtime.Serialization;
using ArcaneTravelCircle.Services;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;

internal static class MountainLandingRegression
{
    public static void Run(string directory, Action<bool, string> check)
    {
        Game1.game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        Game1.game1._locations = new List<GameLocation>();
        Game1.netWorldState = new Netcode.NetRoot<StardewValley.Network.NetWorldState>(new StardewValley.Network.NetWorldState());
        using var content = new LocalizedContentManager(new GameServiceContainer(), Path.Combine(directory, "Content"));
        Game1.content = content;
        Game1.locationData = content.Load<Dictionary<string, StardewValley.GameData.Locations.LocationData>>("Data/Locations");
        Game1.locationContextData = content.Load<Dictionary<string, StardewValley.GameData.LocationContexts.LocationContextData>>("Data/LocationContexts");
        // Avoid FarmerTeam's cosmetic texture loading; retain the real Farmer,
        // sprite geometry, net fields and unmodified collision methods.
        var player = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
        foreach (string fieldName in new[] { "position", "sprite", "currentLocationRef", "mailReceived", "mailForTomorrow", "stats", "onBridge", "netMount", "netGender", "name" })
        {
            FieldInfo? field = null;
            for (Type? type = typeof(Farmer); type is not null && field is null; type = type.BaseType)
                field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            field!.SetValue(player, Activator.CreateInstance(field.FieldType));
        }
        player.Sprite = new FarmerSprite();
        player.TemporaryPassableTiles = new StardewValley.Util.BoundingBoxGroup();
        typeof(Game1).GetField("_player", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, player);
        player.stats.DaysPlayed = 0;
        player.mailReceived.Add("ccCraftsRoom");
        Game1._locationLookup.Clear();
        var mountain = new Mountain();
        player.stats.DaysPlayed = 100;
#pragma warning disable AvoidNetField
        mountain.name.Value = "Mountain";
#pragma warning restore AvoidNetField
        mountain.map = content.Load<xTile.Map>("Maps/Mountain");
        mountain.IsOutdoors = true;
        // Match native loadObjects' real Paths-layer bush markers, rather than
        // testing an empty terrain collection around the narrow lake paths.
        var paths = mountain.Map.GetLayer("Paths");
        for (int y = 0; y < paths.LayerHeight; y++)
            for (int x = 0; x < paths.LayerWidth; x++)
            {
                int marker = paths.Tiles[x, y]?.TileIndex ?? -1;
                if (marker is not (24 or 25 or 26 or 33)) continue;
                var bush = new StardewValley.TerrainFeatures.Bush { Tile = new Vector2(x, y), Location = mountain };
#pragma warning disable AvoidNetField
                bush.size.Value = marker switch { 24 => 2, 25 => 1, 33 => 4, _ => 0 };
#pragma warning restore AvoidNetField
                mountain.largeTerrainFeatures.Add(bush);
            }
        Game1._locationLookup["Mountain"] = mountain;
        foreach (Point tile in new[] { new Point(69, 10), new(67, 20), new(57, 19) })
        {
            var tree = new StardewValley.TerrainFeatures.Tree { Tile = tile.ToVector2(), Location = mountain };
#pragma warning disable AvoidNetField
            tree.growthStage.Value = 5;
#pragma warning restore AvoidNetField
            mountain.terrainFeatures.Add(tile.ToVector2(), tree);
        }
        Game1.locations.Add(mountain);
        foreach (string name in new[] { "Town", "Backwoods", "Railroad", "AdventureGuild" })
        {
            var location = new GameLocation();
#pragma warning disable AvoidNetField
            location.name.Value = name;
#pragma warning restore AvoidNetField
            location.map = content.Load<xTile.Map>("Maps/" + name);
            location.IsOutdoors = name != "AdventureGuild";
            location.DisplayName = name;
            Game1._locationLookup[name] = location;
            Game1.locations.Add(location);
        }
        foreach (GameLocation location in Game1.locations)
        {
            string[] warps = location.Map.Properties["Warp"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < warps.Length; i += 5)
                location.warps.Add(new Warp(int.Parse(warps[i]), int.Parse(warps[i + 1]), warps[i + 2],
                    int.Parse(warps[i + 3]), int.Parse(warps[i + 4]), false));
        }
        Game1.game1.instanceGameLocation = Game1._locationLookup["Town"];
        player.currentLocation = Game1.currentLocation;
        player.Position = new Vector2(74 * 64, 12 * 64);
        var finder = new SafeLandingFinder(isAllowed: MapTargetResolver.IsTileUnlocked);
        var mapData = content.Load<Dictionary<string, StardewValley.GameData.WorldMaps.WorldMapRegionData>>("Data/WorldMap");
        var region = new StardewValley.WorldMaps.MapRegion("Valley", mapData["Valley"]);
        var area = new StardewValley.WorldMaps.MapArea(region, region.Data.MapAreas.Single(item => item.Id == "Mountain"));
        var page = (StardewValley.Menus.MapPage)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.MapPage));
        typeof(StardewValley.Menus.MapPage).GetField("mapRegion")!.SetValue(page, region);
        typeof(StardewValley.Menus.MapPage).GetField("mapAreas")!.SetValue(page, new[] { area });
        page.mapBounds = new Rectangle(125, 73, 300, 180);
        var resolver = new MapTargetResolver();
        bool Click(int x, int y, out Point safe)
        {
            safe = default;
            return resolver.TryResolve(page, new Point(125 + x * 4, 73 + y * 4), out var target, out _)
                && finder.TryFind(target!.Location, target.Tile, player, out safe);
        }
        check(Click(220, 27, out var guildClick) && guildClick == new Point(76, 9),
            "Full native WorldMap Guild click resolves and lands safely before first Mountain visit");
        check(Click(250, 36, out var quarryClick), "Full native Quarry map click passes collision and exit search");
        check(Click(258, 40, out var otherQuarryClick) && otherQuarryClick != quarryClick,
            "Separated native Quarry map clicks reach different safe tiles");
        check(finder.TryFind(mountain, new Point(76, 9), player, out Point guildTile)
            && guildTile == new Point(76, 9),
            "Full native Mountain search accepts the Guild before first visit despite stale day-zero gates");
        check(finder.TryFind(mountain, new Point(114, 25), player, out _),
            "Unvisited unlocked Quarry becomes reachable after the real bridge map update");
        check(finder.TryFind(mountain, new Point(120, 30), player, out _),
            "A second distant Quarry click also retains a reachable nearby landing");
        check(finder.TryFind(mountain, new Point(95, 29), player, out Point bridgeTile)
            && bridgeTile.X >= 92 && bridgeTile.X <= 100,
            "Repaired quarry bridge landing stays on the actual bridge deck");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        check(((Netcode.NetBool)typeof(Mountain).GetField("landslide", flags)!.GetValue(mountain)!).Value
            && ((Netcode.NetBool)typeof(Mountain).GetField("railroadAreaBlocked", flags)!.GetValue(mountain)!).Value,
            "Predicting entry collision does not mutate either Mountain progress net field");
        MethodInfo special = typeof(SafeLandingFinder).GetMethod("HasSpecialCollision", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Blocks(Rectangle bounds) => (bool)special.Invoke(null, new object[] { mountain, bounds })!;
        player.stats.DaysPlayed = 4;
        check(Blocks(new Rectangle(3200, 256, 48, 32)), "Landslide still blocks before native day-five opening");
        player.stats.DaysPlayed = 5;
        check(!Blocks(new Rectangle(3200, 256, 48, 32)), "Landslide prediction opens exactly on native day five");
        player.stats.DaysPlayed = 30;
        check(Blocks(new Rectangle(512, 64, 48, 32)), "Railroad still blocks before native day-thirty-one opening");
        player.stats.DaysPlayed = 31;
        check(!Blocks(new Rectangle(512, 64, 48, 32)), "Railroad prediction opens exactly on native day thirty-one");
        player.stats.DaysPlayed = 100;
        player.mailReceived.Remove("ccCraftsRoom");
        check(!MapTargetResolver.IsTileUnlocked(mountain, new Point(95, 29)),
            "Bridge collision preparation cannot bypass the actual Crafts Room unlock");
        Game1.game1 = null;
    }
}



