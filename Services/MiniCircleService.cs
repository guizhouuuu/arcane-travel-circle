using ArcaneTravelCircle.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.GameData.Buildings;
using HarmonyLib;

namespace ArcaneTravelCircle.Services;

/// <summary>Purchases, stores and draws non-blocking outdoor travel-circle licenses.</summary>
public sealed class MiniCircleService
{
    public const int Price = 200_000;
    public const int PurchaseLimit = MiniCircleLedger.PurchaseLimit;
    public const string ObjectId = "guizhouuuu.ArcaneTravelCircle_MiniCircle";
    public const string QualifiedItemId = "(O)" + ObjectId;
    public const string TextureAsset = "Mods/guizhouuuu.ArcaneTravelCircle/MiniCircle";
    public const string TokenKey = "guizhouuuu.ArcaneTravelCircle/MiniId";
    private const string SaveKey = "mini-circles";
    public const string CatalogueId = "guizhouuuu.ArcaneTravelCircle_MiniCatalogue";
    private static MiniCircleService? purchaseHandler;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SafeLandingFinder placementSafety = new(maximumRadius: 0, isTravelAnchor: (_, _) => true);
    private MiniCircleSaveData state = new();
    private Texture2D? texture;
    private bool initialized;
    private bool textureErrorReported;
    private string? notice;
    private double noticeUntil;
    private double nextPurchaseAt;
    private bool purchaseInProgress;

    public MiniCircleService(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    public int PurchasedCount => this.state.Circles.Count;

    public void Initialize()
    {
        if (this.initialized)
            return;
        this.initialized = true;
        purchaseHandler = this;
        var harmony = new Harmony(this.helper.ModRegistry.ModID);
        harmony.Patch(
            AccessTools.Method(typeof(CarpenterMenu), nameof(CarpenterMenu.receiveLeftClick), new[] { typeof(int), typeof(int), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(MiniCircleService), nameof(OnCatalogueClick)));
        harmony.Patch(
            AccessTools.Method(typeof(CarpenterMenu), nameof(CarpenterMenu.performHoverAction), new[] { typeof(int), typeof(int) }),
            postfix: new HarmonyMethod(typeof(MiniCircleService), nameof(OnCatalogueHover)));
        this.helper.Events.Content.AssetRequested += this.OnAssetRequested;
        this.helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        this.helper.Events.GameLoop.Saving += this.OnSaving;
        this.helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            this.state = new MiniCircleSaveData();
            this.texture = null;
            this.notice = null;
            this.textureErrorReported = false;
            this.nextPurchaseAt = 0;
            this.purchaseInProgress = false;
        };
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        this.helper.Events.Display.RenderingStep += this.OnRenderingStep;
        this.helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
    }

    public IReadOnlyList<MiniCircle> GetPlaced()
        => this.state.Circles.Where(circle => circle.IsPlaced).ToArray();

    public bool TryGetAt(GameLocation location, Point tile, out MiniCircle circle)
    {
        circle = this.state.Circles.FirstOrDefault(entry => entry.IsPlaced
            && entry.LocationName == location.NameOrUniqueName && entry.Tile == tile)!;
        return circle is not null;
    }

    /// <summary>Also recognizes invalid copies, so the caller leaves their clicks to this service.</summary>
    public bool IsPlacementItem(Item? item) => item?.QualifiedItemId == QualifiedItemId;

    public static bool HasBuiltMainCircle()
        => Game1.getFarm().buildings.Any(building => building.buildingType.Value == CircleRegistry.BuildingId
            && !building.isUnderConstruction(ignoreUpgrades: false));

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(TextureAsset))
            e.LoadFromModFile<Texture2D>("assets/mini-circle.png", AssetLoadPriority.Exclusive);
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(asset => asset.AsDictionary<string, ObjectData>().Data[ObjectId] = new ObjectData
            {
                Name = "ArcaneMiniCircle",
                DisplayName = this.Text("mini.item.name", "小型传送阵"),
                Description = this.Text("mini.item.description", "持在手中点击相邻空地放置。只能放在农场外的室外。Ctrl+右键拾取后可搬动；每个存档累计限购10个。"),
                Type = "Basic",
                Category = -999,
                Price = 0,
                Edibility = -300,
                Texture = TextureAsset,
                SpriteIndex = 0,
                CanBeGivenAsGift = false,
                CanBeTrashed = false,
                ExcludeFromFishingCollection = true,
                ExcludeFromShippingCollection = true,
                ExcludeFromRandomSale = true,
                ContextTags = new List<string> { "not_placeable", "prevent_loss_on_death" }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Buildings"))
            e.Edit(asset => asset.AsDictionary<string, BuildingData>().Data[CatalogueId] = CreateCatalogueData(
                this.Text("mini.catalogue.name", "小型传送阵（带走放置）"),
                this.Text("mini.catalogue.description", "每个20万金，累计限购10个。农场建成大阵后，点击购买即放入背包；只能放在农场外的室外空地。")));
    }

    public static BuildingData CreateCatalogueData(string name, string description) => new()
    {
        Name = name, NameForGeneralType = name, Description = description,
        Texture = TextureAsset, SourceRect = new Rectangle(0, 0, 16, 16),
        Size = new Point(1, 1), Builder = "Wizard", BuildCost = Price,
        BuildMaterials = new List<BuildingMaterial>(), BuildDays = 0,
        MagicalConstruction = true, CollisionMap = "O", DrawShadow = false,
        HumanDoor = new Point(-1, -1), SortTileOffset = 1f
    };

    // Mouse, keyboard-generated clicks, and controller confirm all route through
    // this native method in 1.6.15. Intercept only our catalogue card's Buy button,
    // before vanilla can fade to the farm or spend building resources.
    private static bool OnCatalogueClick(CarpenterMenu __instance, int x, int y)
    {
        if (__instance.Builder != "Wizard" || __instance.Blueprint?.Id != CatalogueId
            || __instance.onFarm || __instance.freeze || __instance.readOnly
            || __instance.GetChildMenu() is not null || !__instance.okButton.containsPoint(x, y))
            return true;
        if (purchaseHandler is null || !Context.IsWorldReady || Context.IsMultiplayer)
            return false;
        try { purchaseHandler.PurchaseDirectly(Game1.player); }
        catch (Exception ex)
        {
            purchaseHandler.monitor.Log($"Could not complete a direct mini-circle purchase: {ex}", LogLevel.Error);
            purchaseHandler.ShowNotice("mini.purchase-failed", "购买未完成，请查看SMAPI日志。");
        }
        return false;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Context.IsMainPlayer || Context.IsMultiplayer)
        {
            this.state = new MiniCircleSaveData();
            return;
        }
        this.state = this.helper.Data.ReadSaveData<MiniCircleSaveData>(SaveKey) ?? new MiniCircleSaveData();
        this.state.Circles ??= new List<MiniCircle>();
        var valid = this.state.Circles.Where(circle => circle is not null && !string.IsNullOrWhiteSpace(circle.Id)
                && circle.Number is >= 1 and <= PurchaseLimit)
            .GroupBy(circle => circle.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(circle => circle.Number).Take(PurchaseLimit).ToList();
        if (valid.Count != this.state.Circles.Count)
            this.monitor.Log("Ignored invalid/duplicate mini-circle license entries while loading save data.", LogLevel.Warn);
        this.state.Circles = valid;
        this.TryLoadTexture();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer)
            this.helper.Data.WriteSaveData(SaveKey, this.state);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (Context.IsWorldReady && this.texture is null && !this.textureErrorReported && e.IsMultipleOf(60))
            this.TryLoadTexture();
    }

    private void TryLoadTexture()
    {
        try
        {
            this.texture = this.helper.GameContent.Load<Texture2D>(TextureAsset);
        }
        catch (Exception ex)
        {
            this.textureErrorReported = true;
            this.monitor.Log($"Mini-circle texture could not be loaded: {ex}", LogLevel.Error);
        }
    }

    private bool CanModifyWorld()
        => Context.IsWorldReady && !Context.IsMultiplayer && Context.IsPlayerFree
            && !Game1.eventUp && Game1.currentLocation.currentEvent is null
            && !Game1.fadeToBlack && !Game1.fadeIn && Game1.locationRequest is null
            && !Game1.player.UsingTool && !Game1.player.isRafting && Game1.player.mount is null;

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (this.helper.Input.IsSuppressed(e.Button) || !Context.IsWorldReady || Context.IsMultiplayer)
            return;

        if (!this.CanModifyWorld())
            return;
        if (InputSurface.IsOverHud(Game1.getMousePosition(true)))
            return;

        Point tile = new((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y);
        if (e.Button == SButton.MouseRight
            && (this.helper.Input.IsDown(SButton.LeftControl) || this.helper.Input.IsDown(SButton.RightControl))
            && this.TryGetAt(Game1.currentLocation, tile, out MiniCircle circle))
        {
            this.helper.Input.Suppress(e.Button);
            this.TryPickUp(circle);
            return;
        }

        if (e.Button is not (SButton.MouseLeft or SButton.MouseRight) || !this.IsPlacementItem(Game1.player.CurrentItem))
            return;

        this.helper.Input.Suppress(e.Button);
        this.TryPlace(Game1.player.CurrentItem!, tile);
    }

    private void TryPlace(Item item, Point tile)
    {
        if (!HasBuiltMainCircle())
        {
            this.ShowNotice("mini.requires-main", "请先在农场建成大型传送阵，才能放置小型传送阵。");
            return;
        }
        if (item.Stack != 1 || !item.modData.TryGetValue(TokenKey, out string id))
        {
            this.ShowNotice("mini.invalid-token", "这个小阵物品没有有效的购买凭证，或被合并成了异常堆叠。");
            return;
        }
        MiniCircle? circle = this.state.Circles.FirstOrDefault(entry => entry.Id == id);
        if (circle is null || circle.IsPlaced)
        {
            this.ShowNotice("mini.invalid-token", "这个小阵物品没有有效的购买凭证，或其传送阵已经放置。");
            return;
        }

        GameLocation location = Game1.currentLocation;
        if (!IsAllowedPlacementLocation(location))
        {
            this.ShowNotice("mini.outdoor-only", "小型传送阵只能放在农场外的室外。");
            return;
        }
        if (!IsAdjacent(tile, Game1.player.TilePoint))
        {
            this.ShowNotice("mini.too-far", "请靠近要放置传送阵的位置。");
            return;
        }
        if (this.TryGetAt(location, tile, out _) || location.Objects.ContainsKey(tile.ToVector2())
            || !this.placementSafety.TryFind(location, tile, Game1.player, out Point safeTile) || safeTile != tile)
        {
            this.ShowNotice("mini.invalid-placement", "这里不是可安全使用的空地，请换一个位置。");
            return;
        }

        // The record is the entire placed device. No Object, terrain feature,
        // building or map collision is inserted, so NPC routes stay unobstructed.
        if (!MiniCircleLedger.TryMarkPlaced(this.state.Circles, id, item.Stack, location.NameOrUniqueName, tile, out _))
        {
            this.ShowNotice("mini.invalid-token", "这个小阵物品没有有效的购买凭证，或其传送阵已经放置。");
            return;
        }
        Game1.player.reduceActiveItemByOne();
        ScepterTravelEffect.PlayConstructionSounds(location, tile);
        this.ShowNotice("mini.placed", $"已放置小型传送阵 #{circle.Number}。右键使用，Ctrl+右键拾取。", new { number = circle.Number });
    }

    private void TryPickUp(MiniCircle circle)
    {
        if (!IsAdjacent(circle.Tile, Game1.player.TilePoint))
        {
            this.ShowNotice("mini.too-far", "请靠近传送阵。");
            return;
        }
        StardewValley.Object token = CreateToken(circle);
        if (!Game1.player.couldInventoryAcceptThisItem(token) || !Game1.player.addItemToInventoryBool(token))
        {
            this.ShowNotice("mini.inventory-full", "背包已满，请先空出一个格子。");
            return;
        }
        MiniCircleLedger.MarkPickedUp(circle);
        Game1.playSound("coin");
        this.ShowNotice("mini.picked-up", $"已收起小型传送阵 #{circle.Number}，可免费重新放置。", new { number = circle.Number });
    }

    public static bool IsAllowedPlacementLocation(GameLocation location)
        => location.IsOutdoors && location is not Farm && location.Name != "Farm"
            && !location.IsTemporary && !string.IsNullOrWhiteSpace(location.NameOrUniqueName);

    public static bool IsAdjacent(Point tile, Point playerTile)
        => Math.Abs(tile.X - playerTile.X) <= 1 && Math.Abs(tile.Y - playerTile.Y) <= 1;

    private bool TryGetWizardMenu(out CarpenterMenu menu)
    {
        menu = Game1.activeClickableMenu as CarpenterMenu ?? null!;
        return menu is not null && menu.Builder == "Wizard" && !menu.onFarm && !menu.freeze
            && !menu.readOnly && menu.GetChildMenu() is null && Game1.currentLocation.Name == "WizardHouse";
    }

    private static void OnCatalogueHover(CarpenterMenu __instance, int x, int y, ref string ___hoverText)
    {
        if (__instance.Builder == "Wizard" && __instance.Blueprint?.Id == CatalogueId
            && !__instance.onFarm && !__instance.readOnly && __instance.GetChildMenu() is null
            && __instance.okButton.containsPoint(x, y))
            ___hoverText = purchaseHandler?.Text("mini.purchase", "购买") ?? "购买";
    }

    private void PurchaseDirectly(Farmer who)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        if (this.purchaseInProgress || now < this.nextPurchaseAt)
            return;
        this.nextPurchaseAt = now + 300;
        if (!HasBuiltMainCircle())
        {
            this.ShowNotice("mini.requires-main", "请先在农场建成大型传送阵，才能购买小型传送阵。");
            return;
        }
        if (this.PurchasedCount >= PurchaseLimit)
        {
            this.ShowNotice("mini.purchase-limit", "这个存档已累计购买10个小型传送阵，达到上限。");
            return;
        }
        if (who.Money < Price)
        {
            this.ShowNotice("mini.not-enough-money", "金币不足：小型传送阵需要20万金。");
            return;
        }

        this.purchaseInProgress = true;
        MiniCircle? entry = null;
        StardewValley.Object? token = null;
        bool charged = false;
        bool delivered = false;
        try
        {
            if (!MiniCircleLedger.TryRecordPurchase(this.state.Circles, true, Guid.NewGuid().ToString("N"), out entry))
                return;
            token = CreateToken(entry!);
            if (!who.couldInventoryAcceptThisItem(token))
            {
                this.ShowNotice("mini.inventory-full", "背包已满，请先空出一个格子。");
                return;
            }
            // The native construction method is suppressed. This is the one
            // and only charge; no ShopMenu or native ConsumeResources runs.
            who.Money -= Price;
            charged = true;
            delivered = who.addItemToInventoryBool(token);
            if (!delivered)
            {
                this.ShowNotice("mini.inventory-full", "背包已满，购买款项已退还。");
                return;
            }
            Game1.playSound("purchaseClick");
            this.ShowNotice("mini.purchased", $"已购买小型传送阵 #{entry!.Number}。已购 {this.PurchasedCount}/{PurchaseLimit}。",
                new { number = entry.Number, count = this.PurchasedCount, limit = PurchaseLimit });
        }
        catch (Exception ex)
        {
            // A mod hook can throw after inserting the item. Retain its license
            // and charge if the exact token is already in the inventory.
            delivered |= token is not null && who.Items.Any(item => ReferenceEquals(item, token));
            this.monitor.Log($"Direct mini-circle purchase failed (delivered: {delivered}): {ex}", LogLevel.Error);
            if (!delivered)
                this.ShowNotice("mini.purchase-failed", "购买未完成，未保留扣款。请查看SMAPI日志。");
        }
        finally
        {
            if (!delivered)
            {
                if (entry is not null) this.state.Circles.Remove(entry);
                if (charged) who.addUnearnedMoney(Price);
            }
            this.purchaseInProgress = false;
        }
    }

    public static StardewValley.Object CreateToken(MiniCircle circle)
    {
        var token = (StardewValley.Object)ItemRegistry.Create(QualifiedItemId);
        token.Stack = 1;
        token.modData[TokenKey] = circle.Id;
        // Vanilla Item.canStackWith compares Name, not modData. Distinct internal
        // names are therefore essential to preserve each purchased license ID.
        token.Name = "ArcaneMiniCircle_" + circle.Id;
        token.displayNameFormat = "%DISPLAY_NAME #" + circle.Number;
        token.CanBeSetDown = false;
        token.questItem.Value = true;
        return token;
    }

    private void OnRenderingStep(object? sender, RenderingStepEventArgs e)
    {
        if (e.Step != RenderSteps.World_Sorted || !Context.IsWorldReady || this.texture is null)
            return;

        // This hook runs in the open FrontToBack world batch. Depth near zero
        // places the floor sprite behind NPCs and farmers, unlike RenderedWorld.
        foreach (MiniCircle circle in this.state.Circles)
        {
            if (!circle.IsPlaced || circle.LocationName != Game1.currentLocation.NameOrUniqueName)
                continue;
            Vector2 local = Game1.GlobalToLocal(Game1.viewport, circle.Tile.ToVector2() * Game1.tileSize);
            e.SpriteBatch.Draw(this.texture, local, new Rectangle(0, 0, 16, 16), Color.White,
                0f, Vector2.Zero, Game1.pixelZoom, SpriteEffects.None, 0.000001f);
        }
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || !this.TryGetWizardMenu(out _))
            return;

        if (this.notice is not null && Game1.currentGameTime.TotalGameTime.TotalSeconds < this.noticeUntil)
        {
            string wrapped = Game1.parseText(this.notice, Game1.smallFont, Math.Max(200, Game1.uiViewport.Width - 48));
            Utility.drawTextWithShadow(e.SpriteBatch, wrapped, Game1.smallFont,
                new Vector2(20, 20), Color.LightGoldenrodYellow);
        }
        Game1.activeClickableMenu.drawMouse(e.SpriteBatch);
    }

    private string Text(string key, string fallback, object? tokens = null)
    {
        Translation text = this.helper.Translation.Get(key, tokens);
        return text.HasValue() ? text.ToString() : fallback;
    }

    private void ShowNotice(string key, string fallback, object? tokens = null)
    {
        this.notice = this.Text(key, fallback, tokens);
        this.noticeUntil = Game1.currentGameTime.TotalGameTime.TotalSeconds + 5;
        Game1.addHUDMessage(new HUDMessage(this.notice));
    }
}
