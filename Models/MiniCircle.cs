using Microsoft.Xna.Framework;

namespace ArcaneTravelCircle.Models;

/// <summary>Save-data record for one paid license; no custom game entity is serialized.</summary>
public sealed class MiniCircle
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }

    public Point Tile => new(this.TileX, this.TileY);

    public bool IsPlaced => !string.IsNullOrWhiteSpace(this.LocationName);
}

public sealed class MiniCircleSaveData
{
    public int SchemaVersion { get; set; } = 1;
    public List<MiniCircle> Circles { get; set; } = new();
}
