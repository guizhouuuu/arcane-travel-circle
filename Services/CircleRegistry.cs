using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;

namespace ArcaneTravelCircle.Services;

/// <summary>A vanilla data-driven Building; no custom save-serialized subclass.</summary>
public sealed class CircleRegistry
{
    public const string BuildingId = "guizhouuuu.ArcaneTravelCircle_Circle";
    public const string TextureAsset = "Mods/guizhouuuu.ArcaneTravelCircle/Circle";
    public const int Price = 2_000_000;

    private readonly ITranslationHelper translations;

    public CircleRegistry(ITranslationHelper translations)
    {
        this.translations = translations;
    }

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(TextureAsset))
            e.LoadFromModFile<Texture2D>("assets/circle.png", AssetLoadPriority.Exclusive);
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Buildings"))
            e.Edit(asset => asset.AsDictionary<string, BuildingData>().Data[BuildingId] = this.CreateData());
    }

    public BuildingData CreateData() => CreateData(this.translations.Get("building.name"), this.translations.Get("building.description"));

    public static BuildingData CreateData(string name, string description) => new()
    {
        Name = name,
        NameForGeneralType = name,
        Description = description,
        Texture = TextureAsset,
        SourceRect = new Rectangle(0, 0, 48, 48),
        Size = new Point(3, 3),
        Builder = "Wizard",
        BuildCost = Price,
        BuildDays = 0,
        BuildMaterials = new List<BuildingMaterial>(),
        MagicalConstruction = true,
        CollisionMap = "OOO\nOOO\nOOO",
        DrawShadow = false,
        FadeWhenBehind = false,
        // Keep a flat floor sprite behind the feet of anyone on its top row.
        SortTileOffset = 3f,
        HumanDoor = new Point(-1, -1),
        AnimalDoor = new Rectangle(-1, -1, 0, 0),
        AllowsFlooringUnderneath = true
    };

    public static Building? FindUnderPlayer(Farmer player)
    {
        if (player.currentLocation is not Farm farm)
            return null;

        Point tile = player.TilePoint;
        return farm.buildings.FirstOrDefault(building =>
            building.buildingType.Value == BuildingId
            && !building.isUnderConstruction(ignoreUpgrades: false)
            && new Rectangle(building.tileX.Value, building.tileY.Value,
                building.tilesWide.Value, building.tilesHigh.Value).Contains(tile));
    }
}
