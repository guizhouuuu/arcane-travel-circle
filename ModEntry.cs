using ArcaneTravelCircle.Models;
using ArcaneTravelCircle.Services;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ArcaneTravelCircle;

public sealed class ModEntry : Mod
{
    private CircleRegistry circles = null!;
    private readonly MapTargetResolver targets = new();
    private SafeLandingFinder landing = null!;
    private SafeLandingFinder networkLanding = null!;
    private MiniCircleService minis = null!;
    private TravelNetwork network = null!;
    private NetworkMapOverlay overlay = null!;
    private readonly ScepterTravelEffect travelEffect = new();
    private TravelNode? activeOrigin;
    private IReadOnlyList<TravelNode> landingAnchors = Array.Empty<TravelNode>();
    private MapPage? armedMap;
    private bool warpPending;
    private double warpStartedAt;
    private double lastNoticeAt = -10;
    private string? previousNotice;
    private string? hoverText;
    private bool mapErrorReported;
    private string? noticeText;
    private double noticeUntil;

    public override void Entry(IModHelper helper)
    {
        this.circles = new CircleRegistry(helper.Translation);
        this.minis = new MiniCircleService(helper, this.Monitor);
        this.network = new TravelNetwork(this.minis, helper.Translation);
        this.overlay = new NetworkMapOverlay(helper);
        this.landing = new SafeLandingFinder(isAllowed: MapTargetResolver.IsTileUnlocked, isTravelAnchor: this.IsTravelAnchor);
        this.networkLanding = new SafeLandingFinder(isAllowed: NetworkTileAllowed, isTravelAnchor: this.IsTravelAnchor);
        helper.Events.Content.AssetRequested += this.circles.OnAssetRequested;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Input.MouseWheelScrolled += (_, e) =>
        {
            if (this.TryGetArmedMap(out MapPage page)
                && this.overlay.IsOverSidebar(page, this.network.GetNodes(), Game1.getMousePosition(true)))
                this.overlay.ScrollSidebar(e.Delta);
        };
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.ClearAllState();
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            this.ClearAllState();
            this.overlay.PreloadTexture();
        };
        helper.Events.Display.MenuChanged += (_, e) =>
        {
            this.ClearMapPresentation();
            if (e.NewMenu is not GameMenu && e.NewMenu is not MapPage)
                this.activeOrigin = null;
        };
        helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
        helper.Events.Player.Warped += this.OnWarped;
        // Our existing-circle interaction gets first refusal. The placement
        // service checks suppression and owns Ctrl+right-click pickup.
        this.minis.Initialize();
        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            string npcMap = helper.ModRegistry.Get("Bouhm.NPCMapLocations")?.Manifest.Version.ToString() ?? "not installed";
            this.Monitor.Log($"Arcane Travel Circle ready. NPC Map Locations: {npcMap}. Scoped Wizard catalogue purchase/hover patches; no map replacements.", LogLevel.Info);
        };
    }

    private bool CanPlayerInteract()
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || this.warpPending
            || Game1.eventUp || Game1.currentLocation.currentEvent is not null
            || Game1.fadeToBlack || Game1.fadeIn || Game1.locationRequest is not null
            || Game1.nextFarmerWarpOffsetX != 0 || Game1.nextFarmerWarpOffsetY != 0)
            return false;

        Farmer player = Game1.player;
        return !player.UsingTool && !player.isRafting && player.mount is null
            && !player.swimming.Value && !player.IsSitting();
    }

    private bool CanActivate() => this.CanPlayerInteract() && this.activeOrigin is not null
        && this.network.IsAvailableOrigin(this.activeOrigin, Game1.player);

    private bool TryGetArmedMap(out MapPage page)
    {
        if (this.CanPlayerInteract() && MapViewAdapter.TryGetActiveMap(out page))
        {
            // Retain the original stand-on-circle shortcut, while right-click
            // also supports opening from an adjacent tile.
            if (this.activeOrigin is null)
                this.network.TryGetAt(Game1.currentLocation, Game1.player.TilePoint, out this.activeOrigin);
            if (!this.CanActivate())
            {
                this.ClearMapState();
                return false;
            }
            if (!ReferenceEquals(this.armedMap, page))
            {
                this.armedMap = page;
                this.hoverText = null;
            }
            return true;
        }

        this.ClearMapState();
        page = null!;
        return false;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.travelEffect.Update();
        if (this.warpPending && Context.IsWorldReady
            && Game1.currentGameTime.TotalGameTime.TotalSeconds - this.warpStartedAt > 15
            && Game1.locationRequest is null && !Game1.fadeToBlack && !Game1.fadeIn)
        {
            this.warpPending = false;
            this.travelEffect.Cancel();
            this.Monitor.Log("The pending warp ended without the expected Warped event; the travel lock was cleared.", LogLevel.Warn);
        }

        if (!this.TryGetArmedMap(out MapPage map))
            return;

        // Resolve at 10 Hz for a hover label. Safety/obstacle search only runs
        // after a click, so an open map never scans collision every frame.
        if (!e.IsMultipleOf(6))
            return;

        try
        {
            TravelNode? node = this.overlay.HitTest(map, this.network.GetNodes(), Game1.getMousePosition(true));
            if (node is not null)
                this.hoverText = this.Helper.Translation.Get("map.destination", new { name = node.Name });
            else if (this.activeOrigin!.IsLarge && this.targets.TryResolve(map, Game1.getMousePosition(true), out TravelTarget? target, out _))
            {
                this.hoverText = this.Helper.Translation.Get("map.destination", new { name = target!.DisplayName });
            }
            else
            {
                this.hoverText = null;
            }
        }
        catch (Exception ex)
        {
            this.ClearMapState();
            if (!this.mapErrorReported)
            {
                this.mapErrorReported = true;
                this.Monitor.Log($"Unable to resolve the world-map hover: {ex}", LogLevel.Error);
            }
        }
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == SButton.MouseRight && Game1.activeClickableMenu is null && this.CanPlayerInteract()
            && !this.Helper.Input.IsSuppressed(e.Button)
            && !InputSurface.IsOverHud(Game1.getMousePosition(true))
            && !this.Helper.Input.IsDown(SButton.LeftControl) && !this.Helper.Input.IsDown(SButton.RightControl)
            && !this.minis.IsPlacementItem(Game1.player.CurrentItem))
        {
            Point clicked = new((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y);
            if (this.network.TryGetAt(Game1.currentLocation, clicked, out TravelNode? origin)
                && TravelNetwork.IsWithinReach(TravelNetwork.GetFootprint(origin!), Game1.player.TilePoint))
            {
                this.Helper.Input.Suppress(e.Button);
                this.ClearMapState();
                this.activeOrigin = origin;
                Game1.activeClickableMenu = new GameMenu(GameMenu.mapTab);
                return;
            }
        }

        if (e.Button != SButton.MouseLeft || !this.TryGetArmedMap(out MapPage map))
            return;

        Point mouse = Game1.getMousePosition(true);
        if (!MapViewAdapter.IsInsideCanvas(map, mouse))
            return;

        // Suppress only the canvas click. Vanilla otherwise switches tabs on a
        // blank map click. Close buttons and tabs are outside this rectangle.
        this.Helper.Input.Suppress(e.Button);

        try
        {
            IReadOnlyList<TravelNode> nodes = this.network.GetNodes();
            TravelNode? selectedNode = this.overlay.HitTest(map, nodes, mouse);
            if (selectedNode is null && this.overlay.IsOverSidebar(map, nodes, mouse))
                return;
            TravelTarget? target;
            if (selectedNode is not null)
            {
                if (selectedNode.Id == this.activeOrigin!.Id)
                {
                    this.ShowNotice("network.current");
                    return;
                }
                target = new TravelTarget(selectedNode.Location, selectedNode.Tile, selectedNode.Name, "network:" + selectedNode.Id);
            }
            else if (!this.activeOrigin!.IsLarge)
            {
                this.ShowNotice("network.select-marker");
                return;
            }
            else if (!this.targets.TryResolve(map, mouse, out target, out string errorKey))
            {
                this.ShowNotice(errorKey);
                return;
            }

            if (HasActiveMapReplacement(target!.Location))
            {
                this.ShowNotice("target.festival");
                return;
            }

            this.landingAnchors = nodes;
            SafeLandingFinder finder = selectedNode is null ? this.landing : this.networkLanding;
            if (!finder.TryFind(target.Location, target.Tile, Game1.player, out Point safeTile))
            {
                this.Monitor.Log($"No safe landing: {target.Location.NameOrUniqueName} ({target.Tile.X}, {target.Tile.Y}), mapping {target.MappingId}, days played {Game1.stats.DaysPlayed}.", LogLevel.Debug);
                this.ShowNotice("travel.no-safe-tile");
                return;
            }

            // A passive festival can substitute another map inside warpFarmer.
            // Do not validate one map and then silently arrive on another.
            LocationRequest request = Game1.getLocationRequest(target.Location.NameOrUniqueName);
            if (request.Location != target.Location || !this.CanActivate()
                || !(selectedNode is null ? MapTargetResolver.IsTileUnlocked(target.Location, safeTile)
                    : NetworkTileAllowed(target.Location, safeTile)))
            {
                this.ShowNotice("travel.state-changed");
                return;
            }

            this.warpPending = true;
            this.warpStartedAt = Game1.currentGameTime.TotalGameTime.TotalSeconds;
            TravelNode origin = this.activeOrigin!;
            GameLocation source = Game1.currentLocation;
            int facing = Game1.player.FacingDirection;
            this.ClearMapState();
            Game1.exitActiveMenu();
            this.travelEffect.Begin(Game1.player, () =>
            {
                if (!Context.IsWorldReady || !ReferenceEquals(source, Game1.currentLocation)
                    || Game1.eventUp || Game1.currentLocation.currentEvent is not null
                    || !this.network.IsAvailableOrigin(origin, Game1.player)
                    || HasActiveMapReplacement(target.Location))
                    throw new InvalidOperationException("Travel conditions changed during the scepter effect.");
                this.landingAnchors = this.network.GetNodes();
                if (selectedNode is not null && !this.landingAnchors.Any(node => node.Id == selectedNode.Id
                    && ReferenceEquals(node.Location, selectedNode.Location) && node.Tile == selectedNode.Tile))
                    throw new InvalidOperationException("The selected network circle was moved or removed.");
                LocationRequest finalRequest = Game1.getLocationRequest(target.Location.NameOrUniqueName);
                if (finalRequest.Location != target.Location
                    || !finder.TryFind(target.Location, target.Tile, Game1.player, out Point finalTile))
                    throw new InvalidOperationException("The chosen destination no longer has a safe landing.");
                // SMAPI's Warped event doesn't fire when both nodes are in the
                // same location; the native request callback covers that too.
                finalRequest.OnWarp += () => this.warpPending = false;
                Game1.warpFarmer(finalRequest, finalTile.X, finalTile.Y, facing);
                this.Monitor.Log($"Travel requested: {target.Location.NameOrUniqueName} ({finalTile.X}, {finalTile.Y}), mapping {target.MappingId}.", LogLevel.Debug);
            }, ex =>
            {
                this.warpPending = false;
                this.Monitor.Log($"Travel effect could not complete: {ex}", LogLevel.Warn);
                if (Context.IsWorldReady)
                    this.ShowNotice("travel.failed");
            });
        }
        catch (Exception ex)
        {
            this.warpPending = false;
            this.travelEffect.Cancel();
            this.ClearMapPresentation();
            this.Monitor.Log($"Travel failed before completion: {ex}", LogLevel.Error);
            this.ShowNotice("travel.failed");
        }
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!this.TryGetArmedMap(out MapPage map))
            return;

        // A small caption above the map preserves NPC Map Locations' original
        // marker layer and hover tooltip; no custom MapPage is substituted.
        bool showNotice = Game1.currentGameTime.TotalGameTime.TotalSeconds < this.noticeUntil;
        string label = showNotice && this.noticeText is not null
            ? this.noticeText
            : this.hoverText ?? this.Helper.Translation.Get(this.activeOrigin!.IsLarge ? "map.hint" : "map.network-hint");
        Vector2 size = Game1.smallFont.MeasureString(label);
        float x = Math.Clamp(map.mapBounds.X + 12f, 8f, Math.Max(8f, Game1.uiViewport.Width - size.X - 8));
        float y = Math.Max(8, map.mapBounds.Y - 32);
        Utility.drawTextWithShadow(e.SpriteBatch, label, Game1.smallFont, new Vector2(x, y),
            showNotice ? Color.LightSalmon : Color.LightGoldenrodYellow);
        this.overlay.Draw(map, this.network.GetNodes(), Game1.getMousePosition(true), e.SpriteBatch);
        Game1.activeClickableMenu?.drawMouse(e.SpriteBatch);
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer)
            return;
        this.ClearMapState();
        this.travelEffect.Cancel();
        this.warpPending = false;
    }

    private void ShowNotice(string key)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        if (key == this.previousNotice && now - this.lastNoticeAt < 1.2)
            return;
        this.previousNotice = key;
        this.lastNoticeAt = now;
        this.noticeText = this.Helper.Translation.Get(key);
        this.noticeUntil = now + 3;
        Game1.addHUDMessage(new HUDMessage(this.noticeText, HUDMessage.error_type));
    }

    private void ClearMapState()
    {
        this.ClearMapPresentation();
        this.activeOrigin = null;
        this.landingAnchors = Array.Empty<TravelNode>();
    }

    private void ClearMapPresentation()
    {
        this.armedMap = null;
        this.hoverText = null;
        this.noticeText = null;
        this.noticeUntil = 0;
    }

    private bool IsTravelAnchor(GameLocation location, Point tile)
        => this.landingAnchors.Any(node => ReferenceEquals(node.Location, location)
            && TravelNetwork.GetFootprint(node).Contains(tile));

    private static bool NetworkTileAllowed(GameLocation location, Point tile)
        => location.IsOutdoors && !location.IsTemporary && Game1.isLocationAccessible(location.Name);

    private void ClearAllState()
    {
        this.travelEffect.Cancel();
        this.ClearMapState();
        this.warpPending = false;
        this.previousNotice = null;
        this.lastNoticeAt = -10;
        this.mapErrorReported = false;
    }

    private static bool HasActiveMapReplacement(GameLocation destination)
    {
        if (Game1.weatherIcon == 1 && Game1.whereIsTodaysFest == destination.Name)
            return true;
        foreach (string festivalId in Game1.netWorldState.Value.ActivePassiveFestivals)
        {
            if (Utility.TryGetPassiveFestivalData(festivalId, out var data)
                && Game1.dayOfMonth >= data.StartDay && Game1.dayOfMonth <= data.EndDay
                && data.Season == Game1.season && data.MapReplacements is not null
                && data.MapReplacements.TryGetValue(destination.Name, out string? replacement)
                && replacement != destination.Name)
                return true;
        }
        return false;
    }
}
