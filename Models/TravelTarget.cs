using Microsoft.Xna.Framework;
using StardewValley;

namespace ArcaneTravelCircle.Models;

/// <summary>A resolved world-map destination before the safety search.</summary>
public sealed record TravelTarget(GameLocation Location, Point Tile, string DisplayName, string MappingId);
