using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Netcode;
using StardewValley;
using StardewValley.Locations;

namespace ArcaneTravelCircle.Services;

/// <summary>Finds a nearby arrival tile with a verified walkable route out.</summary>
public sealed class SafeLandingFinder
{
    private const int TileSize = 64;
    private static readonly Point[] Neighbors = { new(0, -1), new(-1, 0), new(1, 0), new(0, 1) };
    private readonly int searchRadius;
    private readonly Func<GameLocation, Point, bool>? isAllowed;
    private readonly Func<GameLocation, Point, bool>? isTravelAnchor;

    public SafeLandingFinder(int maximumRadius = 8, Func<GameLocation, Point, bool>? isAllowed = null,
        Func<GameLocation, Point, bool>? isTravelAnchor = null)
    {
        if (maximumRadius < 0 || maximumRadius > 32)
            throw new ArgumentOutOfRangeException(nameof(maximumRadius));
        this.searchRadius = maximumRadius;
        this.isAllowed = isAllowed;
        this.isTravelAnchor = isTravelAnchor;
    }

    public bool TryFind(GameLocation location, Point requested, Farmer player, out Point safeTile)
    {
        safeTile = requested;
        // Vanilla's rafting collision branch can move a player even when its
        // skipCollisionEffects argument is true. Do not enter that branch.
        if (location is null || player is null || location.Map is null || player.isRafting)
            return false;
        // Remote maps may not have been entered since loading the save. Apply
        // the same idempotent map edits as native entry before testing repaired
        // bridge tiles; don't invoke entry/reset hooks, events, or NPC spawning.
        if (location is Mountain mountain)
            mountain.MakeMapModifications();
        var back = location.Map.GetLayer("Back");
        if (back is null || location.Map.GetLayer("Buildings") is null)
            return false;

        Rectangle oldBounds = player.GetBoundingBox();
        int offsetX = oldBounds.X - (int)player.Position.X;
        int offsetY = oldBounds.Y - (int)player.Position.Y;
        var walkCache = new Dictionary<Point, bool>();
        var componentCache = new Dictionary<Point, bool>();
        var transitionCache = new Dictionary<Point, bool>();
        var transitions = new HashSet<Point>();

        // Only concrete, currently usable outdoor warps count as exits. An
        // indoor doorway or an unsupported/locked target is not proof of escape.
        foreach (Warp warp in location.warps)
        {
            if (warp.npcOnly.Value)
                continue;
            GameLocation? destination = Game1.getLocationFromName(warp.TargetName);
            Point arrival = new(warp.TargetX, warp.TargetY);
            if (destination is not null && destination.IsOutdoors && !destination.IsTemporary
                && Game1.isLocationAccessible(destination.Name)
                && (this.isAllowed is null || this.isAllowed(destination, arrival)))
                transitions.Add(new Point(warp.X, warp.Y));
        }

        // Verified from Desert.performTouchAction: DesertBus asks the player
        // whether to return, then drives to BusStop without charging a fare.
        if (location is Desert && Game1.isLocationAccessible("Desert"))
        {
            for (int y = 0; y < back.LayerHeight; y++)
                for (int x = 0; x < back.LayerWidth; x++)
                    if (FirstWord(location.doesTileHaveProperty(x, y, "TouchAction", "Back")) == "DesertBus")
                        transitions.Add(new Point(x, y));
        }

        Rectangle? sourceCircle = null;
        if (ReferenceEquals(location, player.currentLocation))
        {
            var circle = CircleRegistry.FindUnderPlayer(player);
            if (circle is not null)
                sourceCircle = new Rectangle(circle.tileX.Value, circle.tileY.Value, circle.tilesWide.Value, circle.tilesHigh.Value);
        }

        Rectangle GetBounds(Point tile)
        {
            int arrivalY = tile.Y * TileSize - (player.Sprite.getHeight() - 32) + 16;
            return new Rectangle(tile.X * TileSize + offsetX, arrivalY + offsetY, oldBounds.Width, oldBounds.Height);
        }

        bool CanOccupy(Point tile, bool allowTransition)
        {
            if (!location.isTileOnMap(tile) || (this.isAllowed is not null && !this.isAllowed(location, tile)))
                return false;
            // Use the actual vanilla warp callback's sprite-height adjustment.
            Rectangle bounds = GetBounds(tile);
            if (bounds.Left < 0 || bounds.Top < 0 || bounds.Width <= 0 || bounds.Height <= 0
                || bounds.Right > back.LayerWidth * TileSize || bounds.Bottom > back.LayerHeight * TileSize)
                return false;

            for (int y = bounds.Top / TileSize; y <= (bounds.Bottom - 1) / TileSize; y++)
            {
                for (int x = bounds.Left / TileSize; x <= (bounds.Right - 1) / TileSize; x++)
                {
                    // Water marks the underlying water, including below a dock
                    // or bridge. An explicit passable Buildings-layer floor is
                    // walkable; a naked water tile is not a valid arrival.
                    if (!IsStaticTileWalkable(location.Map, new Point(x, y))
                        || location.IsTileOccupiedBy(new Vector2(x, y), CollisionMask.All & ~CollisionMask.Farmers, CollisionMask.All))
                        return false;
                    if (!allowTransition && (!string.IsNullOrWhiteSpace(location.doesTileHaveProperty(x, y, "TouchAction", "Back"))
                        || !string.IsNullOrWhiteSpace(location.doesTileHaveProperty(x, y, "TouchAction", "Buildings"))))
                        return false;
                }
            }

            if (HasSpecialCollision(location, bounds))
                return false;
            if (!allowTransition && location.isCollidingWithWarpOrDoor(bounds, player) is Warp crossing
                && !transitions.Contains(new Point(crossing.X, crossing.Y)))
                return false;

            // Stock Forest and IslandLocation DROP skipCollisionEffects when
            // calling base. Their extra collisions are explicitly checked above.
            // Vanilla also samples Right/Bottom inclusively at the map edge.
            if (location is Forest or IslandLocation
                || bounds.Right >= back.LayerWidth * TileSize || bounds.Bottom >= back.LayerHeight * TileSize)
                return true;

            return !location.isCollidingPosition(bounds, Game1.viewport, isFarmer: true, damagesFarmer: 0,
                glider: false, character: player, pathfinding: false, projectile: false,
                ignoreCharacterRequirement: false, skipCollisionEffects: true);
        }

        bool CanWalk(Point tile)
        {
            if (!walkCache.TryGetValue(tile, out bool result))
                walkCache[tile] = result = CanOccupy(tile, allowTransition: false);
            return result;
        }

        bool CanUseTransition(Point tile)
        {
            if (transitionCache.TryGetValue(tile, out bool result))
                return result;
            if (location.isTileOnMap(tile))
                result = CanOccupy(tile, allowTransition: true);
            else
                // Boundary warps may lie exactly one tile outside the map. Their
                // adjacent source node has already passed the walkability check.
                result = (tile.X == -1 || tile.X == back.LayerWidth) && tile.Y >= 0 && tile.Y < back.LayerHeight
                    || (tile.Y == -1 || tile.Y == back.LayerHeight) && tile.X >= 0 && tile.X < back.LayerWidth;
            transitionCache[tile] = result;
            return result;
        }

        bool IsExit(Point tile)
        {
            if (sourceCircle?.Contains(tile) == true || this.isTravelAnchor?.Invoke(location, tile) == true)
                return true;
            // Vanilla samples the bounding box's bottom edge inclusively, so
            // entering a downward warp can happen from the preceding tile.
            // Such a node is a valid terminal in the walk graph, never a landing.
            if (location.isCollidingWithWarpOrDoor(GetBounds(tile), player) is Warp crossing
                && transitions.Contains(new Point(crossing.X, crossing.Y))
                && CanUseTransition(new Point(crossing.X, crossing.Y)))
                return true;
            foreach (Point direction in Neighbors)
            {
                Point next = new(tile.X + direction.X, tile.Y + direction.Y);
                if (transitions.Contains(next) && CanUseTransition(next))
                    return true;
            }
            return false;
        }

        foreach (Point candidate in GetCandidates(requested, this.searchRadius))
        {
            // These are coordinate rewrites in warpFarmer, not collisions.
            if (candidate.X >= back.LayerWidth - 1
                || (location.Name == "BusStop" && candidate.X < 10)
                || (location.Name == "IslandSouth" && candidate.X <= 15 && candidate.Y <= 6)
                || !CanWalk(candidate)
                || location.isCollidingWithWarpOrDoor(GetBounds(candidate), player) is not null
                || !HasReachableExit(candidate, CanWalk, IsExit, componentCache))
                continue;
            safeTile = candidate;
            return true;
        }
        return false;
    }

    /// <summary>Pure bounded search; only fully classified components are cached.</summary>
    public static bool HasReachableExit(Point start, Func<Point, bool> canWalk, Func<Point, bool> isExit,
        IDictionary<Point, bool> componentCache, int maximumVisited = 16384)
    {
        if (maximumVisited <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumVisited));
        if (componentCache.TryGetValue(start, out bool cached))
            return cached;
        if (!canWalk(start))
            return false;
        var seen = new HashSet<Point> { start };
        var queue = new Queue<Point>();
        queue.Enqueue(start);
        int processed = 0;
        bool result = false;
        while (queue.Count > 0)
        {
            if (processed++ >= maximumVisited)
                return false;
            Point current = queue.Dequeue();
            if (componentCache.TryGetValue(current, out cached))
            {
                result = cached;
                break;
            }
            if (isExit(current))
            {
                result = true;
                break;
            }
            foreach (Point direction in Neighbors)
            {
                Point next = new(current.X + direction.X, current.Y + direction.Y);
                if (canWalk(next) && seen.Add(next))
                    queue.Enqueue(next);
            }
        }
        foreach (Point visited in seen)
            componentCache[visited] = result;
        return result;
    }

    public static IEnumerable<Point> GetCandidates(Point requested, int radius)
    {
        if (radius < 0 || radius > 32)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var candidates = new List<Point>();
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius)
                    candidates.Add(new Point(requested.X + dx, requested.Y + dy));
        return candidates.OrderBy(point => (long)(point.X - requested.X) * (point.X - requested.X)
                + (long)(point.Y - requested.Y) * (point.Y - requested.Y))
            .ThenBy(point => point.Y).ThenBy(point => point.X);
    }

    /// <summary>
    /// Reads the real map's collision properties without a running game or a
    /// graphics device. Water underneath an explicit passable deck is allowed;
    /// ordinary open water, voids, walls and temporary barriers remain blocked.
    /// Dynamic occupants and scene-specific blockers are checked separately.
    /// </summary>
    public static bool IsStaticTileWalkable(xTile.Map map, Point tile)
    {
        var back = map.GetLayer("Back");
        var buildings = map.GetLayer("Buildings");
        if (back is null || buildings is null || tile.X < 0 || tile.Y < 0
            || tile.X >= back.LayerWidth || tile.Y >= back.LayerHeight)
            return false;
        var ground = back.Tiles[tile.X, tile.Y];
        var wall = buildings.Tiles[tile.X, tile.Y];
        if (ground is null || HasProperty(ground, "Passable") || HasProperty(ground, "TemporaryBarrier"))
            return false;
        bool hasDeck = wall is not null && HasProperty(wall, "Passable");
        if (wall is not null && !hasDeck && !HasProperty(wall, "Shadow"))
            return false;
        return !HasProperty(ground, "Water") || hasDeck;
    }

    private static bool HasProperty(xTile.Tiles.Tile tile, string property)
        => tile.Properties.ContainsKey(property) || tile.TileIndexProperties.ContainsKey(property);

    private static bool HasSpecialCollision(GameLocation location, Rectangle bounds)
    {
        if (location is Forest forest && forest.travelingMerchantBounds.Any(bounds.Intersects))
            return true;
        // Mountain and Beach put these in the SIX-argument overload, bypassed
        // by the side-effect-free ten-argument overload used here.
        if (location is Mountain mountain
            // These net fields are initialized when Mountain is created, and
            // cleared by resetSharedState on entry at days 5 / 31. On an
            // unvisited destination they can still contain the day-zero value.
            // Predict native entry's collision state without writing net fields.
            && (Game1.stats.DaysPlayed < 5 && ReadField<NetBool>(mountain, "landslide").Value
                    && bounds.Intersects(ReadField<Rectangle>(mountain, "landSlideRect"))
                || Game1.stats.DaysPlayed < 31 && ReadField<NetBool>(mountain, "railroadAreaBlocked").Value
                    && bounds.Intersects(ReadField<Rectangle>(mountain, "railroadBlockRect"))))
            return true;
        if (location is Beach beach && ReadField<NPC?>(beach, "oldMariner") is NPC mariner && bounds.Intersects(mariner.GetBoundingBox()))
            return true;
        if (location is IslandLocation island && island.parrotPlatforms.Any(platform => platform.CheckCollisions(bounds)))
            return true;
        if (location is IslandSouth south
            && (ReadField<bool>(south, "_exitsBlocked") && bounds.Intersects(ReadField<Rectangle>(south, "turtle1Spot"))
                || !south.westernTurtleMoved.Value && bounds.Intersects(ReadField<Rectangle>(south, "turtle2Spot"))))
            return true;
        return false;
    }

    private static readonly Dictionary<(Type, string), FieldInfo> Fields = new();

    private static T ReadField<T>(object instance, string name)
    {
        var key = (instance.GetType(), name);
        if (!Fields.TryGetValue(key, out FieldInfo? field))
        {
            for (Type? type = key.Item1; type is not null && field is null; type = type.BaseType)
                field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Fields[key] = field ?? throw new MissingFieldException(key.Item1.FullName, name);
        }
        return (T)field.GetValue(instance)!;
    }

    private static string FirstWord(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
