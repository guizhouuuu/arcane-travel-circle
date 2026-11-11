using System;
using System.Collections.Generic;
using System.Linq;
using ArcaneTravelCircle.Models;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Network;
using StardewValley.TokenizableStrings;
using StardewValley.WorldMaps;

namespace ArcaneTravelCircle.Services;

/// <summary>Inverts valid world-map position rectangles without replacing the game's map.</summary>
public sealed class MapTargetResolver
{
    private static readonly HashSet<string> SupportedLocations = new(StringComparer.Ordinal)
    {
        "Farm", "Town", "Forest", "Mountain", "Beach", "BusStop", "Backwoods",
        "Railroad", "Woods", "Desert", "IslandEast", "IslandWest", "IslandSouth",
        "IslandSouthEast", "IslandNorth", "IslandShrine"
    };

    private sealed record Candidate(TravelTarget Target, long PixelArea);

    public bool TryResolve(MapPage page, Point uiMouse, out TravelTarget? target, out string errorKey)
    {
        target = null;
        errorKey = "target.unmapped";
        if (!MapViewAdapter.IsInsideCanvas(page, uiMouse))
            return false;
        if (page.mapRegion.Id is not ("Valley" or "GingerIsland"))
        {
            errorKey = "target.unsupported";
            return false;
        }

        Point mapMouse = new(uiMouse.X - page.mapBounds.X, uiMouse.Y - page.mapBounds.Y);
        List<Candidate> candidates = new();
        long blockedOverlayArea = long.MaxValue;
        string blockedOverlayError = "target.indoor";
        bool foundLocked = false;

        foreach (MapArea area in page.mapAreas)
        {
            // Data/WorldMap lists the alternate Night Market beach all year,
            // with no condition and exactly the same footprint as normal Beach.
            // It isn't a second travel destination. Festival map replacements
            // are rejected by the travel coordinator before any warp instead.
            if (IsVanillaAlternateBeach(area))
                continue;
            Rectangle areaPixels = Scale(area.Data.PixelArea, Game1.pixelZoom);
            bool areaHit = areaPixels.Contains(mapMouse);

            // Vanilla Valley contains only a single context marker for the whole island.
            // It cannot tell us which island location/tile the user intended.
            if (page.mapRegion.Id == "Valley" && area.Id == "GingerIsland" && areaHit)
            {
                errorKey = "target.ambiguous";
                return false;
            }

            foreach (MapAreaPosition position in area.GetWorldPositions())
            {
                Rectangle pixels = GetHitPixelArea(position);
                bool desertCorrection = IsVanillaDesertPoint(position);
                if (desertCorrection)
                    pixels = areaPixels;
                bool bridgeCorrection = TryGetVanillaQuarryBridgeMapping(position, mapMouse,
                    out Rectangle bridgePixels, out Rectangle bridgeTiles);
                if (bridgeCorrection)
                    pixels = bridgePixels;
                if (!pixels.Contains(mapMouse))
                    continue;

                long specificity = (long)pixels.Width * pixels.Height;
                string mappingId = $"{page.mapRegion.Id}/{area.Id}/{position.Data.Id}";
                List<string> names = GetExplicitLocationNames(position);
                if (desertCorrection)
                    names = new List<string> { "Desert" };
                if (names.Count == 0)
                {
                    blockedOverlayArea = Math.Min(blockedOverlayArea, specificity);
                    blockedOverlayError = "target.ambiguous";
                    continue;
                }

                // Single-point markers discard exterior tile information. An interior
                // marker may still resolve to its actual unique outdoor entrance.
                bool isPointMarker = !desertCorrection && pixels.Width <= Game1.pixelZoom && pixels.Height <= Game1.pixelZoom;

                List<Candidate> mappingCandidates = new();
                foreach (string name in names)
                {
                    GameLocation? location = Game1.getLocationFromName(name);
                    if (location is null || location.IsTemporary)
                        continue;

                    if (SupportedLocations.Contains(name) && location.IsOutdoors)
                    {
                        if (isPointMarker)
                            continue;
                        Rectangle tiles = GetEffectiveTileArea(location, bridgeCorrection ? bridgeTiles : position.Data.TileArea);
                        if (!TryProjectToTile(pixels, tiles, mapMouse, out Point tile))
                            continue;
                        if (!desertCorrection && !position.Matches(name, location.GetLocationContextId(), tile))
                            continue;
                        if (!IsTileUnlocked(location, tile))
                        {
                            foundLocked = true;
                            continue;
                        }
                        string id = desertCorrection ? mappingId + ":full-region-approximation"
                            : bridgeCorrection ? mappingId + ":vanilla-quarry-bridge" : mappingId;
                        mappingCandidates.Add(new Candidate(new TravelTarget(location, tile, GetDisplayName(position, location, tile), id), specificity));
                    }
                    else if (!location.IsOutdoors && !desertCorrection)
                    {
                        // A building icon can resolve to its real, unique outdoor exit.
                        // There is no guess based on the drawing or an indoor fallback warp.
                        List<TravelTarget> exits = GetOutdoorExits(location, mappingId);
                        foreach (TravelTarget exit in exits)
                        {
                            if (IsTileUnlocked(exit.Location, exit.Tile))
                                mappingCandidates.Add(new Candidate(exit, specificity));
                            else
                                foundLocked = true;
                        }
                    }
                }

                if (mappingCandidates.Count == 0)
                {
                    // Do not let a broad Town/Forest rectangle override a smaller,
                    // unresolved icon, particularly a locked or interior-only location.
                    blockedOverlayArea = Math.Min(blockedOverlayArea, specificity);
                    if (names.Any(SupportedLocations.Contains))
                        blockedOverlayError = foundLocked ? "target.locked" : isPointMarker ? "target.imprecise" : "target.unsupported";
                }
                candidates.AddRange(mappingCandidates);
            }
        }

        if (candidates.Count == 0)
        {
            errorKey = foundLocked ? "target.locked" : blockedOverlayArea < long.MaxValue ? blockedOverlayError : "target.unmapped";
            return false;
        }

        long smallestArea = candidates.Min(candidate => candidate.PixelArea);
        if (blockedOverlayArea <= smallestArea)
        {
            errorKey = blockedOverlayError;
            return false;
        }

        // More specific rectangles win over an encompassing location rectangle.
        // Equally specific conflicting destinations are rejected, independent of data order.
        TravelTarget[] best = candidates.Where(candidate => candidate.PixelArea == smallestArea)
            .Select(candidate => candidate.Target)
            .GroupBy(candidate => (candidate.Location.NameOrUniqueName, candidate.Tile))
            .Select(group => group.First())
            .ToArray();
        if (best.Length != 1)
        {
            errorKey = "target.ambiguous";
            return false;
        }

        target = best[0];
        errorKey = "";
        return true;
    }

    /// <summary>Called for every safety-search candidate, so searching cannot bypass a gate.</summary>
    public static bool IsTileUnlocked(GameLocation location, Point tile)
    {
        if (!SupportedLocations.Contains(location.Name) || !location.IsOutdoors || location.IsTemporary)
            return false;
        if (!Game1.isLocationAccessible(location.Name))
            return false;
        if (location.Name == "Woods" && !HasVisited("Woods"))
            return false; // conservative proof that the entrance log has already been passed
        if (location is Beach beach && tile.X >= 58 && !beach.bridgeFixed.Value
            && !NetWorldState.checkAnywhereForWorldStateID("beachBridgeFixed"))
            return false;
        if (location is Mountain && tile.X >= 92
            && !Utility.doesMasterPlayerHaveMailReceivedButNotMailForTomorrow("ccCraftsRoom"))
            return false;
        if (location.Name.StartsWith("Island", StringComparison.Ordinal) && !HasVisited(location.Name))
            return false; // do not infer turtle/resort/shrine access from a visible map name
        return true;
    }

    /// <summary>Pure inverse of the game's rectangle interpolation (division by width/height).</summary>
    public static bool TryProjectToTile(Rectangle pixelArea, Rectangle tileArea, Point mapMouse, out Point tile)
    {
        tile = Point.Zero;
        if (pixelArea.Width <= 0 || pixelArea.Height <= 0 || tileArea.Width <= 0 || tileArea.Height <= 0
            || !pixelArea.Contains(mapMouse))
            return false;

        double xRatio = (double)(mapMouse.X - pixelArea.X) / pixelArea.Width;
        double yRatio = (double)(mapMouse.Y - pixelArea.Y) / pixelArea.Height;
        tile = new Point(
            Math.Clamp(tileArea.X + (int)Math.Floor(xRatio * tileArea.Width), tileArea.Left, tileArea.Right - 1),
            Math.Clamp(tileArea.Y + (int)Math.Floor(yRatio * tileArea.Height), tileArea.Top, tileArea.Bottom - 1));
        return true;
    }

    /// <summary>
    /// The vanilla guild's marker is 3x2 texture pixels, but its actual visible
    /// icon/tooltip spans 9x9. Use that icon for the same unique outdoor exit,
    /// instead of projecting its edges into Mountain's northern cliff.
    /// </summary>
    public static Rectangle GetHitPixelArea(MapAreaPosition position)
    {
        if (position.Region.Id == "Valley" && position.Area.Id == "Beach"
            && position.Data.Id == "ElliottHouse" && position.Data.LocationName == "ElliottHouse"
            && position.Data.MapPixelArea == new Rectangle(211, 140, 2, 3))
        {
            var cabin = position.Area.Data.Tooltips?.FirstOrDefault(item => item.Id == "ElliottCabin");
            if (cabin?.PixelArea is Rectangle hit && hit == new Rectangle(209, 137, 6, 8))
                return Scale(hit, Game1.pixelZoom);
        }
        if (position.Region.Id == "Valley" && position.Area.Id == "Mountain"
            && position.Data.Id == "AdventureGuild" && position.Data.LocationName == "AdventureGuild"
            && position.Data.MapPixelArea == new Rectangle(219, 28, 3, 2))
        {
            var tooltip = position.Area.Data.Tooltips?.FirstOrDefault(item => item.Id == "AdventureGuild");
            if (tooltip?.PixelArea is Rectangle hit && hit == new Rectangle(216, 23, 9, 9))
                return Scale(hit, Game1.pixelZoom);
        }
        return position.GetPixelArea();
    }

    public static bool IsVanillaAlternateBeach(MapArea area)
        => area.Region.Id == "Valley" && area.Id == "BeachNightMarket"
            && area.Data.PixelArea == new Rectangle(186, 137, 57, 31)
            && area.Data.WorldPositions.Any(position => position.LocationName == "BeachNightMarket");

    /// <summary>
    /// Actual 1.6.15 world-map artwork draws the quarry bridge at texture pixels
    /// (232,39)..(241,40); Data/WorldMap only supplies a broad Mountain mapping.
    /// The repaired Maps/Mountain-BridgeFixed is copied at (92,24), and its deck
    /// rows are 26..30. Invert that small drawn bridge continuously into its deck.
    /// Apply only to the exact original mapping, leaving changed data untouched.
    /// </summary>
    public static bool TryGetVanillaQuarryBridgeMapping(MapAreaPosition position, Point mapMouse,
        out Rectangle pixels, out Rectangle tiles)
    {
        pixels = Scale(new Rectangle(232, 39, 10, 2), Game1.pixelZoom);
        tiles = new Rectangle(92, 26, 8, 5);
        return position.Region.Id == "Valley" && position.Area.Id == "Mountain"
            && position.Area.Data.PixelArea == new Rectangle(165, 26, 98, 18)
            && position.Data.Id == "Default" && position.Data.LocationName == "Mountain"
            && position.Data.MapPixelArea.IsEmpty && position.Data.TileArea.IsEmpty
            && pixels.Contains(mapMouse);
    }

    private static bool HasVisited(string name)
        => Game1.player.locationsVisited.Contains(name) || Game1.MasterPlayer.locationsVisited.Contains(name);

    private static Rectangle GetEffectiveTileArea(GameLocation location, Rectangle configured)
    {
        if (location.Map is null || location.Map.Layers.Count == 0)
            return Rectangle.Empty;
        var size = location.Map.Layers[0].LayerSize;
        Rectangle bounds = new(0, 0, size.Width, size.Height);
        return configured.IsEmpty ? bounds : Rectangle.Intersect(configured, bounds);
    }

    private static List<string> GetExplicitLocationNames(MapAreaPosition position)
    {
        if (!string.IsNullOrWhiteSpace(position.Data.LocationName))
        {
            if (position.Data.LocationNames is { Count: > 0 } list && !list.Contains(position.Data.LocationName))
                return new List<string>();
            return new List<string> { position.Data.LocationName };
        }
        return position.Data.LocationNames?.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToList()
            ?? new List<string>();
    }

    private static List<TravelTarget> GetOutdoorExits(GameLocation interior, string mappingId)
    {
        List<TravelTarget> exits = new();
        foreach (Warp warp in interior.warps)
        {
            if (!SupportedLocations.Contains(warp.TargetName))
                continue;
            GameLocation? exterior = Game1.getLocationFromName(warp.TargetName);
            if (exterior is null || !exterior.IsOutdoors || exterior.IsTemporary)
                continue;
            exits.Add(new TravelTarget(exterior, new Point(warp.TargetX, warp.TargetY),
                interior.DisplayName + " → " + exterior.DisplayName, mappingId + ":outdoor-exit"));
        }
        return exits.GroupBy(exit => (exit.Location.NameOrUniqueName, exit.Tile)).Select(group => group.First()).ToList();
    }

    private static bool IsVanillaDesertPoint(MapAreaPosition position)
    {
        // Actual 1.6.15 Data/WorldMap collapses Desert, Sandy's and Skull Cavern to
        // pixel (36,20). Expand only that exact vanilla marker to its region's
        // drawn rectangle. This is an explicit approximate projection, not an
        // invented inverse of the point marker. Changed mod data is not patched.
        return position.Region.Id == "Valley" && position.Area.Id == "Desert"
            && position.Data.Id == "Desert" && position.Data.MapPixelArea == new Rectangle(36, 20, 1, 1)
            && position.Area.Data.PixelArea == new Rectangle(0, 0, 73, 38)
            && position.Data.TileArea.IsEmpty && position.Data.LocationName is null
            && position.Data.LocationNames?.Contains("Desert") == true;
    }

    private static string GetDisplayName(MapAreaPosition position, GameLocation location, Point tile)
    {
        // Do not populate MapAreaPosition's shared scroll-text cache with the hover
        // target: NPC Map Locations and vanilla also use that object for player data.
        string? text = position.Data.ScrollText;
        if (position.Data.ScrollTextZones is not null)
        {
            foreach (var zone in position.Data.ScrollTextZones)
            {
                if (!zone.TileArea.Contains(tile))
                    continue;
                text = zone.ScrollText;
                break;
            }
        }
        text ??= position.Area.Data.ScrollText;
        string name = string.IsNullOrWhiteSpace(text) ? "" : TokenParser.ParseText(text);
        return string.IsNullOrWhiteSpace(name) ? location.DisplayName : name;
    }

    private static Rectangle Scale(Rectangle rectangle, int zoom)
        => new(rectangle.X * zoom, rectangle.Y * zoom, rectangle.Width * zoom, rectangle.Height * zoom);
}
