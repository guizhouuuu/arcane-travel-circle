using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using ArcaneTravelCircle.Models;
using ArcaneTravelCircle.Services;
using Microsoft.Xna.Framework;

if (args.Length != 2)
    throw new ArgumentException("Usage: SmokeTests <game directory> <built mod directory>");

string[] paths = { Path.GetFullPath(args[0]), Path.Combine(Path.GetFullPath(args[0]), "smapi-internal"), Path.GetFullPath(args[1]) };
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (string directory in paths)
    {
        string candidate = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(candidate))
            return context.LoadFromAssemblyPath(candidate);
    }
    return null;
};

RunChecks(args[0]);

[MethodImpl(MethodImplOptions.NoInlining)]
static void RunChecks(string gameDirectory)
{
    Checks.Run(gameDirectory);
}

internal static class Checks
{
    private static int count;

    public static void Run(string gameDirectory)
    {
        BuildingData();
        MapGeometry();
        CandidateSearch();
        EscapeConnectivity();
        CollisionFieldContract();
        MiniCircleLicenses();
        NetworkLayout();
        ActualMapRegressions(gameDirectory);
        BeachResolverRegression.Run(gameDirectory, Assert);
        MountainLandingRegression.Run(gameDirectory, Assert);
        InteractionRegression.Run(Assert);
        var mini = MiniCircleService.CreateCatalogueData("Mini", "Take outside the farm");
        Assert(MiniCircleService.Price == 200_000 && mini.BuildCost == 200_000,
            "Mini catalogue and actual shop price both use the latest 200,000g price");
        Assert(mini.Builder == "Wizard" && mini.Size == new Point(1, 1) && mini.BuildMaterials.Count == 0,
            "Independent mini catalogue card belongs to the native Wizard menu, with no materials");
        NpcMapContract(gameDirectory);
        string? framework = typeof(ArcaneTravelCircle.ModEntry).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Assert(framework == ".NETCoreApp,Version=v6.0", "Release assembly targets the game's .NET 6 runtime");
        Console.WriteLine($"PASS: {count} headless assertions. No game process, save, or Mods folder was changed.");
    }

    private static void BuildingData()
    {
        var data = ArcaneTravelCircle.Services.CircleRegistry.CreateData("法师传送阵", "description");
        Assert(data.Builder == "Wizard", "Building is offered by the vanilla Wizard builder");
        Assert(data.BuildCost == 2_000_000, "Building costs exactly two million gold");
        Assert(data.BuildMaterials.Count == 0, "No additional building materials");
        Assert(data.BuildDays == 0 && data.MagicalConstruction, "Magical construction is instant");
        Assert(data.Size == new Microsoft.Xna.Framework.Point(3, 3), "Footprint is three by three");
        Assert(data.CollisionMap.Split('\n').Length == 3 && data.CollisionMap.Replace("\n", "") == "OOOOOOOOO", "All nine footprint tiles are walkable");
        Assert(data.HumanDoor.X == -1 && data.IndoorMap is null && data.BuildingType is null, "Uses a save-compatible vanilla building with no interior");
        Assert(!data.DrawShadow && data.SortTileOffset >= 3, "Flat texture renders behind feet on all rows");
    }

    private static void MapGeometry()
    {
        var pixels = new Microsoft.Xna.Framework.Rectangle(400, 200, 388, 320);
        var tiles = new Microsoft.Xna.Framework.Rectangle(0, 0, 120, 120);
        bool Project(int x, int y, out Microsoft.Xna.Framework.Point tile)
            => ArcaneTravelCircle.Services.MapTargetResolver.TryProjectToTile(pixels, tiles, new(x, y), out tile);
        Assert(Project(400, 200, out var first) && first == new Microsoft.Xna.Framework.Point(0, 0), "Upper-left pixel projects to first tile");
        Assert(Project(787, 519, out var last) && last == new Microsoft.Xna.Framework.Point(119, 119), "Last interior pixel projects inside the tile bounds");
        Assert(!Project(788, 519, out _), "Right-exclusive pixel boundary is rejected");
        Assert(!Project(400, 520, out _), "Bottom-exclusive pixel boundary is rejected");
        Assert(Project(450, 250, out var near) && Project(730, 450, out var far) && near != far, "Two far-apart clicks inside the same region resolve to different tiles");
        Assert(ArcaneTravelCircle.Services.MapTargetResolver.TryProjectToTile(pixels, new(95, 11, 36, 24), new(400, 200), out var shifted) && shifted == new Microsoft.Xna.Framework.Point(95, 11), "Nonzero tile-area origins are preserved");
        Assert(!ArcaneTravelCircle.Services.MapTargetResolver.TryProjectToTile(Microsoft.Xna.Framework.Rectangle.Empty, tiles, new(0, 0), out _), "Empty pixel mappings are rejected");
        Assert(!ArcaneTravelCircle.Services.MapTargetResolver.TryProjectToTile(pixels, Microsoft.Xna.Framework.Rectangle.Empty, new(400, 200), out _), "Empty tile mappings are rejected");
        var canvas = ArcaneTravelCircle.Services.MapViewAdapter.GetCanvasBounds(new(100, 50, 300, 180), 4);
        Assert(canvas == new Microsoft.Xna.Framework.Rectangle(100, 50, 1200, 720), "Mixed-unit vanilla mapBounds scales its size, not its origin");
        Assert(canvas.Contains(1299, 769) && !canvas.Contains(1300, 769), "Expanded canvas hit testing uses the full drawn map");
        var movedCanvas = ArcaneTravelCircle.Services.MapViewAdapter.GetCanvasBounds(new(250, 140, 300, 180), 4);
        Assert(movedCanvas.Width == canvas.Width && movedCanvas.Height == canvas.Height && movedCanvas.Location != canvas.Location, "Window recentering preserves canvas dimensions");
    }

    private static void CandidateSearch()
    {
        var origin = new Microsoft.Xna.Framework.Point(30, 40);
        var candidates = ArcaneTravelCircle.Services.SafeLandingFinder.GetCandidates(origin, 8).ToArray();
        Assert(candidates.Length == 197 && candidates.Distinct().Count() == candidates.Length, "Radius-eight search is finite and contains no duplicate candidates");
        Assert(candidates[0] == origin, "Requested tile is searched first");
        int Distance(Microsoft.Xna.Framework.Point point) => (point.X - origin.X) * (point.X - origin.X) + (point.Y - origin.Y) * (point.Y - origin.Y);
        Assert(candidates.All(p => Distance(p) <= 64), "Every candidate stays inside the configured search radius");
        Assert(candidates.Zip(candidates.Skip(1), (a, b) => Distance(a) <= Distance(b)).All(x => x), "Safety search tries nearer candidates before farther ones");
        Assert(ArcaneTravelCircle.Services.SafeLandingFinder.GetCandidates(origin, 0).Single() == origin, "Radius zero does not move the requested point");
    }

    private static void NpcMapContract(string gameDirectory)
    {
        string dll = Path.Combine(gameDirectory, "Mods", "NPCMapLocations", "NPCMapLocations.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("SKIP: Installed NPC Map Locations compatibility contract (not installed).");
            return;
        }
        Type type = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll).GetType("NPCMapLocations.Framework.Menus.ModMapPage", throwOnError: true)!;
        Assert(type.IsSubclassOf(typeof(StardewValley.Menus.MapPage)), "Installed NPC Map Locations uses a vanilla MapPage subclass");
        Assert(type.GetMethod("receiveLeftClick", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null, "Installed NPC map does not replace the inherited click handler");
        Assert(typeof(StardewValley.Menus.MapPage).GetField("mapBounds")?.IsPublic == true, "Shared map layout is available without Harmony/reflection hacks");
    }

    private static void EscapeConnectivity()
    {
        static bool InSquare(Microsoft.Xna.Framework.Point p) => p.X >= 0 && p.X < 20 && p.Y >= 0 && p.Y < 20;
        var trapped = new Dictionary<Microsoft.Xna.Framework.Point, bool>();
        Assert(!ArcaneTravelCircle.Services.SafeLandingFinder.HasReachableExit(new(10, 10), InSquare, _ => false, trapped), "A large 20x20 enclosure without an exit is rejected");
        Assert(trapped.TryGetValue(new(10, 10), out bool closed) && !closed, "An exhaustively searched closed component is cached as unsafe");
        var open = new Dictionary<Microsoft.Xna.Framework.Point, bool>();
        Assert(ArcaneTravelCircle.Services.SafeLandingFinder.HasReachableExit(new(10, 10), InSquare, p => p == new Microsoft.Xna.Framework.Point(19, 10), open), "A traversable path to a real exit is accepted");
        Assert(open.TryGetValue(new(10, 10), out bool escaped) && escaped, "A component with a proven exit is cached as safe");
        var capped = new Dictionary<Microsoft.Xna.Framework.Point, bool>();
        Assert(!ArcaneTravelCircle.Services.SafeLandingFinder.HasReachableExit(new(10, 10), InSquare, p => p.X == 19, capped, maximumVisited: 8), "Connectivity work stops safely at its hard visit limit");
        Assert(!capped.ContainsKey(new(10, 10)), "An incomplete search is not cached as a proven closed component");
        static bool Split(Microsoft.Xna.Framework.Point p) => InSquare(p) && p.X != 12;
        Assert(!ArcaneTravelCircle.Services.SafeLandingFinder.HasReachableExit(new(10, 10), Split, p => p.X == 19, new Dictionary<Microsoft.Xna.Framework.Point, bool>()), "An exit on the far side of an impassable wall does not make a trapped component safe");
    }

    private static void CollisionFieldContract()
    {
        var fields = new (Type Type, string Name, string Expected)[]
        {
            (typeof(StardewValley.Locations.Mountain), "landslide", "Netcode.NetBool"),
            (typeof(StardewValley.Locations.Mountain), "railroadAreaBlocked", "Netcode.NetBool"),
            (typeof(StardewValley.Locations.Mountain), "landSlideRect", "Microsoft.Xna.Framework.Rectangle"),
            (typeof(StardewValley.Locations.Mountain), "railroadBlockRect", "Microsoft.Xna.Framework.Rectangle"),
            (typeof(StardewValley.Locations.Beach), "oldMariner", "StardewValley.NPC"),
            (typeof(StardewValley.Locations.IslandSouth), "_exitsBlocked", "System.Boolean"),
            (typeof(StardewValley.Locations.IslandSouth), "turtle1Spot", "Microsoft.Xna.Framework.Rectangle"),
            (typeof(StardewValley.Locations.IslandSouth), "turtle2Spot", "Microsoft.Xna.Framework.Rectangle")
        };
        foreach (var specification in fields)
        {
            var field = specification.Type.GetField(specification.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(field?.FieldType.FullName == specification.Expected,
                $"Actual collision field {specification.Type.Name}.{specification.Name} matches the supported game version");
        }
    }

    private static void MiniCircleLicenses()
    {
        var licenses = new List<MiniCircle>();
        Assert(!MiniCircleLedger.TryRecordPurchase(licenses, false, "first", out _) && licenses.Count == 0,
            "A small circle cannot be purchased before a large circle is built");
        Assert(MiniCircleService.Price == 200_000 && MiniCircleService.PurchaseLimit == 10,
            "Small-circle price and lifetime purchase cap match the requested values");
        Assert(MiniCircleLedger.TryRecordPurchase(licenses, true, "first", out MiniCircle? first) && first!.Number == 1,
            "A purchase creates exactly one numbered license");
        Assert(!MiniCircleLedger.TryRecordPurchase(licenses, true, "first", out _) && licenses.Count == 1,
            "A duplicated purchase identifier does not create a second license");
        for (int i = 2; i <= 10; i++)
            if (!MiniCircleLedger.TryRecordPurchase(licenses, true, "license-" + i, out _))
                throw new InvalidOperationException("Failed to create a permitted license.");
        Assert(licenses.Count == 10 && licenses.Select(c => c.Number).Distinct().Count() == 10,
            "Exactly ten distinct circle numbers can be purchased");
        Assert(!MiniCircleLedger.TryRecordPurchase(licenses, true, "eleventh", out _) && licenses.Count == 10,
            "The eleventh purchase is rejected without changing the ledger");
        Assert(MiniCircleLedger.TryMarkPlaced(licenses, "first", 1, "Town", new(40, 50), out _) && first!.IsPlaced,
            "A valid single licensed item becomes one persistent outdoor endpoint");
        Assert(!MiniCircleLedger.TryMarkPlaced(licenses, "first", 1, "Beach", new(20, 20), out _)
            && first!.LocationName == "Town", "A copied item cannot place its already-built license twice");
        Assert(!MiniCircleLedger.TryMarkPlaced(licenses, "license-2", 1, "Town", new(40, 50), out _),
            "Two circles cannot occupy the same scene tile");
        Assert(!MiniCircleLedger.TryMarkPlaced(licenses, "license-2", 2, "Town", new(41, 50), out _),
            "An abnormal stacked license cannot bypass one-item placement");
        MiniCircleLedger.MarkPickedUp(first!);
        Assert(!first!.IsPlaced && licenses.Count == 10,
            "Picking a circle up clears its endpoint but preserves its purchase count");
        Assert(MiniCircleLedger.TryMarkPlaced(licenses, "first", 1, "Beach", new(20, 20), out _)
            && first.Number == 1 && licenses.Count == 10, "A picked-up circle can be moved without a new purchase");
        var saved = new MiniCircleSaveData { Circles = licenses };
        var restored = System.Text.Json.JsonSerializer.Deserialize<MiniCircleSaveData>(
            System.Text.Json.JsonSerializer.Serialize(saved))!;
        Assert(restored.Circles.Count == 10 && restored.Circles[0].Id == "first"
            && restored.Circles[0].Tile == new Point(20, 20) && restored.Circles[0].LocationName == "Beach",
            "Primitive save-data identifiers and scene coordinates survive JSON round-trip");
        Assert(MiniCircleService.IsAdjacent(new(20, 20), new(21, 21))
            && !MiniCircleService.IsAdjacent(new(20, 20), new(22, 20)),
            "Placement/pickup requires proximity rather than a remote mouse click");
    }

    private static void NetworkLayout()
    {
        Rectangle canvas = new(100, 50, 600, 400);
        var occupied = new List<Rectangle>();
        for (int i = 0; i < 10; i++)
        {
            Rectangle? marker = NetworkMapOverlay.FindMarkerBounds(new(300, 200), canvas, occupied);
            if (!marker.HasValue || !canvas.Contains(marker.Value) || occupied.Any(r => r.Intersects(marker.Value)))
                throw new InvalidOperationException("Network markers overlap or escape the visible canvas.");
            occupied.Add(marker.Value);
        }
        Assert(occupied.Count == 10, "Ten endpoints at one map anchor remain separately clickable");
        Assert(NetworkMapOverlay.FindMarkerBounds(new(101, 51), canvas, Array.Empty<Rectangle>()) is Rectangle edge
            && canvas.Contains(edge), "Markers near a map edge stay inside the visible canvas");
        Assert(NetworkMapOverlay.FindMarkerBounds(new(300, 200), canvas, new[] { canvas }) is null,
            "A crowded map falls back to a named sidebar instead of hiding an endpoint");
        Assert(NetworkMapOverlay.FindMarkerBounds(new(0, 0), new(0, 0, 20, 20), Array.Empty<Rectangle>()) is null,
            "An undersized canvas does not create clipped or unclickable markers");
        Assert(TravelNetwork.IsWithinReach(new(10, 10, 3, 3), new(9, 11))
            && !TravelNetwork.IsWithinReach(new(10, 10, 3, 3), new(8, 11)),
            "A large circle can be right-clicked from beside it but not across the map");
        Assert(TravelNetwork.GetFootprint(new TravelNode("mini", null!, new(20, 30), "mini", false)) == new Rectangle(20, 30, 1, 1),
            "A small endpoint uses precisely one tile");
    }

    private static void ActualMapRegressions(string gameDirectory)
    {
        // Load the installed game's actual XNBs into an isolated content manager.
        // No Game1 instance, graphics device, live map, or save is modified.
        using var content = new Microsoft.Xna.Framework.Content.ContentManager(new GameServiceContainer(), Path.Combine(gameDirectory, "Content"));
        xTile.Map beach = content.Load<xTile.Map>("Maps/Beach");
        xTile.Map mountain = content.Load<xTile.Map>("Maps/Mountain");
        xTile.Map patch = content.Load<xTile.Map>("Maps/Mountain-BridgeFixed");
        xTile.Map guild = content.Load<xTile.Map>("Maps/AdventureGuild");
        bool Walk(xTile.Map map, int x, int y) => SafeLandingFinder.IsStaticTileWalkable(map, new(x, y));
        Assert(Walk(beach, 20, 20) && Walk(beach, 40, 15), "Actual Beach XNB: ordinary sand accepts a static landing");
        Assert(Walk(beach, 38, 0), "Actual Beach XNB: north Town exit approach is traversable");
        Assert(!Walk(beach, 10, 40), "Actual Beach XNB: open ocean remains blocked");
        Assert(Walk(mountain, 46, 7), "Actual Mountain XNB: water beneath a passable wooden bridge is allowed");
        Assert(!Walk(mountain, 95, 29), "Actual Mountain XNB: unrepaired quarry bridge remains blocked");
        Assert(!Walk(mountain, 90, 20), "Actual Mountain XNB: a mountain wall remains blocked");

        foreach (string layerName in new[] { "Back", "Buildings", "Front" })
        {
            var destination = mountain.GetLayer(layerName);
            var source = patch.GetLayer(layerName);
            var sheet = mountain.TileSheets.First(sheet => sheet.Id == "outdoors");
            for (int x = 0; x < source.LayerWidth; x++)
                for (int y = 0; y < source.LayerHeight; y++)
                {
                    var tile = source.Tiles[x, y];
                    destination.Tiles[x + 92, y + 24] = tile is null ? null
                        : new xTile.Tiles.StaticTile(destination, sheet, xTile.Tiles.BlendMode.Alpha, tile.TileIndex);
                }
        }
        Assert(Walk(mountain, 95, 26) && Walk(mountain, 95, 27) && Walk(mountain, 95, 29) && Walk(mountain, 95, 30),
            "Actual BridgeFixed XNB: the four repaired quarry deck rows accept static landings");
        Assert(!Walk(mountain, 95, 28), "Actual repaired bridge: railing is still rejected");
        Assert(Walk(mountain, 76, 9), "Actual Mountain XNB: Adventurer's Guild doorway approach is traversable");
        Assert(guild.Properties["Warp"].ToString().Contains("Mountain 76 9"), "Actual Guild XNB: its outdoor exit is Mountain (76,9)");

        var data = content.Load<Dictionary<string, StardewValley.GameData.WorldMaps.WorldMapRegionData>>("Data/WorldMap");
        var region = new StardewValley.WorldMaps.MapRegion("Valley", data["Valley"]);
        var areaData = region.Data.MapAreas.Single(area => area.Id == "Mountain");
        var area = new StardewValley.WorldMaps.MapArea(region, areaData);
        var mapping = new StardewValley.WorldMaps.MapAreaPosition(area, areaData.WorldPositions.Single(position => position.Id == "Default"));
        var guildPosition = new StardewValley.WorldMaps.MapAreaPosition(area, areaData.WorldPositions.Single(position => position.Id == "AdventureGuild"));
        Rectangle hit = MapTargetResolver.GetHitPixelArea(guildPosition);
        Assert(hit.Contains(new Point(216 * 4, 23 * 4)) && hit.Contains(new Point(224 * 4 + 3, 31 * 4 + 3)),
            "Actual world map: the full visible Guild icon resolves, including formerly dead edges");
        bool ProjectBridge(int x, out Point tile)
        {
            Point click = new(x, 39 * 4 + 1);
            if (!MapTargetResolver.TryGetVanillaQuarryBridgeMapping(mapping, click, out Rectangle pixels, out Rectangle tiles))
            {
                tile = default;
                return false;
            }
            return MapTargetResolver.TryProjectToTile(pixels, tiles, click, out tile);
        }
        Assert(ProjectBridge(232 * 4 + 4, out Point first) && ProjectBridge(240 * 4, out Point second)
            && first != second && Walk(mountain, first.X, first.Y) && Walk(mountain, second.X, second.Y),
            "Actual map bridge artwork: two separated clicks project onto different repaired deck tiles");
        var exits = new HashSet<Point>();
        string[] tokens = beach.Properties["Warp"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i += 5)
            if (tokens[i + 2] == "Town")
                exits.Add(new(int.Parse(tokens[i]), int.Parse(tokens[i + 1])));
        bool BeachExit(Point point) => new[] { new Point(point.X - 1, point.Y), new(point.X + 1, point.Y),
            new(point.X, point.Y - 1), new(point.X, point.Y + 1) }.Any(exits.Contains);
        Assert(SafeLandingFinder.HasReachableExit(new(20, 20), point => Walk(beach, point.X, point.Y), BeachExit, new Dictionary<Point, bool>()),
            "Actual Beach topology: ordinary sand connects to the real north Town exit");
    }


    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    }
}
