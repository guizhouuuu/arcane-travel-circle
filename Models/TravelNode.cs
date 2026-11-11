using Microsoft.Xna.Framework;
using StardewValley;

namespace ArcaneTravelCircle.Models;

/// <summary>A built large circle or a persistent one-tile network endpoint.</summary>
public sealed record TravelNode(string Id, GameLocation Location, Point Tile, string Name, bool IsLarge);
