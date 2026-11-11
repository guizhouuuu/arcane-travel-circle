using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ArcaneTravelCircle.Services;

/// <summary>The local visual/sound sequence from 1.6.15 Wand.DoFunction,
/// followed by our selected destination instead of Wand's hard-coded farm.</summary>
public sealed class ScepterTravelEffect
{
    public const string TravelSound = "wand";
    public const int DelayMilliseconds = 1000;
    private Farmer? traveler;
    private GameLocation? origin;
    private Action? warp;
    private Action<Exception>? failed;
    private double fadeAt;
    private bool fading;
    private bool wasVisible;
    private bool wasInvincible;
    private int previousInvincibilityTimer;
    private int previousFreezePause;
    private bool couldMove;
    private int generation;

    public bool IsActive => this.traveler is not null;

    public void Begin(Farmer player, Action warp, Action<Exception> failed)
    {
        if (this.IsActive)
            throw new InvalidOperationException("A scepter travel effect is already active.");
        this.traveler = player;
        this.origin = player.currentLocation;
        this.warp = warp;
        this.failed = failed;
        this.wasVisible = Game1.displayFarmer;
        this.wasInvincible = player.temporarilyInvincible;
        this.previousInvincibilityTimer = player.temporaryInvincibilityTimer;
        this.previousFreezePause = player.freezePause;
        this.couldMove = player.CanMove;
        this.fadeAt = Game1.currentGameTime.TotalGameTime.TotalMilliseconds + DelayMilliseconds;
        this.fading = false;
        this.generation++;
        try
        {
            for (int i = 0; i < 12; i++)
                this.origin.temporarySprites.Add(new TemporaryAnimatedSprite(354, Game1.random.Next(25, 75), 6, 1,
                    new Vector2(Game1.random.Next((int)player.Position.X - 256, (int)player.Position.X + 192),
                        Game1.random.Next((int)player.Position.Y - 256, (int)player.Position.Y + 192)),
                    flicker: false, Game1.random.Next(2) == 0));
            player.playNearbySoundAll(TravelSound);
            Game1.displayFarmer = false;
            player.temporarilyInvincible = true;
            player.temporaryInvincibilityTimer = -2000;
            player.Halt();
            player.faceDirection(2);
            player.CanMove = false;
            player.freezePause = 2000;
            Game1.flashAlpha = 1f;
            Point tile = player.TilePoint;
            int index = 0;
            for (int x = tile.X + 8; x >= tile.X - 8; x--)
                this.origin.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(x, tile.Y) * 64f,
                    Color.White, 8, flipped: false, 50f)
                {
                    layerDepth = 1f, delayBeforeAnimationStart = index++ * 25,
                    motion = new Vector2(-0.25f, 0f)
                });
        }
        catch
        {
            this.Cancel();
            throw;
        }
    }

    public void Update()
    {
        if (!this.IsActive)
            return;
        if (!Context.IsWorldReady || !ReferenceEquals(Game1.player, this.traveler)
            || !ReferenceEquals(Game1.currentLocation, this.origin)
            || Game1.eventUp || Game1.currentLocation.currentEvent is not null)
        {
            this.Abort(new InvalidOperationException("Travel was interrupted by a world or event change."));
            return;
        }
        if (this.fading || Game1.currentGameTime.TotalGameTime.TotalMilliseconds < this.fadeAt)
            return;
        if (Game1.fadeToBlack || Game1.fadeIn || Game1.locationRequest is not null)
        {
            this.Abort(new InvalidOperationException("Another scene transition interrupted travel."));
            return;
        }
        this.fading = true;
        int expected = this.generation;
        try { Game1.globalFadeToBlack(() =>
        {
            if (!this.IsActive || expected != this.generation)
                return;
            try
            {
                this.warp!();
                Game1.fadeToBlackAlpha = 0.99f;
                Game1.screenGlow = false;
                this.RestorePlayer();
            }
            catch (Exception ex) { this.Abort(ex); }
        }); }
        catch (Exception ex) { this.Abort(ex); }
    }

    private void Abort(Exception error)
    {
        Action<Exception>? report = this.failed;
        this.Cancel();
        report?.Invoke(error);
    }

    public void Cancel()
    {
        bool ownedFade = this.fading;
        this.RestorePlayer();
        this.generation++;
        if (ownedFade && Context.IsWorldReady && Game1.locationRequest is null)
            Game1.globalFadeToClear();
    }

    private void RestorePlayer()
    {
        if (this.traveler is Farmer player)
        {
            Game1.displayFarmer = this.wasVisible;
            player.temporarilyInvincible = this.wasInvincible;
            player.temporaryInvincibilityTimer = this.previousInvincibilityTimer;
            player.freezePause = this.previousFreezePause;
            player.CanMove = this.couldMove;
        }
        this.traveler = null;
        this.origin = null;
        this.warp = null;
        this.failed = null;
        this.fading = false;
    }

    public static void PlayConstructionSounds(GameLocation location, Point tile)
    {
        Vector2 position = tile.ToVector2();
        location.localSound("axchop", position);
        location.localSound(TravelSound, position);
        for (int i = 0; i < 8; i++)
            DelayedAction.playSoundAfterDelay("dirtyHit", 100 + i * 210, location, position, -1, local: true);
    }
}
