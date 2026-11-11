using System.Reflection;
using System.Runtime.Serialization;
using ArcaneTravelCircle.Services;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

internal static class InteractionRegression
{
    public static void Run(Action<bool, string> check)
    {
        var menu = (CarpenterMenu)FormatterServices.GetUninitializedObject(typeof(CarpenterMenu));
        typeof(CarpenterMenu).GetField("Builder")!.SetValue(menu, "Wizard");
        menu.Blueprint = new CarpenterMenu.BlueprintEntry(0, MiniCircleService.CatalogueId,
            MiniCircleService.CreateCatalogueData("Mini", "Take outside"), null);
        menu.okButton = (ClickableTextureComponent)FormatterServices.GetUninitializedObject(typeof(ClickableTextureComponent));
        menu.okButton.bounds = new Rectangle(20, 20, 64, 64);
        menu.okButton.visible = true;
        MethodInfo prefix = typeof(MiniCircleService).GetMethod("OnCatalogueClick", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo original = typeof(CarpenterMenu).GetMethod("receiveLeftClick", new[] { typeof(int), typeof(int), typeof(bool) })!;
        var harmony = new Harmony("guizhouuuu.ArcaneTravelCircle.HeadlessTests");
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        try
        {
            // Without the prefix, this uninitialized native menu would throw in
            // its original close-button handling. The catalogue Buy must bypass
            // that code entirely, even when no live game is available.
            menu.receiveLeftClick(40, 40);
            check(!menu.onFarm && !menu.freeze, "Real Harmony patch intercepts mini Buy before native farm placement");
            check(menu.GetChildMenu() is null, "Mini catalogue click does not create a second shop page");
            MethodInfo hover = typeof(MiniCircleService).GetMethod("OnCatalogueHover", BindingFlags.NonPublic | BindingFlags.Static)!;
            object[] hoverArgs = { menu, 40, 40, "建造" };
            hover.Invoke(null, hoverArgs);
            check((string)hoverArgs[3] == "购买", "Mini card replaces native Build hover text with Purchase");
            check((bool)prefix.Invoke(null, new object[] { menu, 100, 100 })!,
                "Catalogue patch leaves clicks outside Buy to the native menu");
            menu.Blueprint = new CarpenterMenu.BlueprintEntry(0, CircleRegistry.BuildingId,
                CircleRegistry.CreateData("Large", "Farm circle"), null);
            check((bool)prefix.Invoke(null, new object[] { menu, 40, 40 })!,
                "Catalogue patch leaves large-circle construction unchanged");
        }
        finally { harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id); }

        var player = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
        player.CanMove = false;
        player.freezePause = 2000;
        player.temporarilyInvincible = true;
        player.temporaryInvincibilityTimer = -2000;
        Game1.displayFarmer = false;
        var effect = new ScepterTravelEffect();
        void Set(string field, object value) => typeof(ScepterTravelEffect)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(effect, value);
        Set("traveler", player);
        Set("wasVisible", true);
        Set("couldMove", true);
        int failures = 0;
        Set("failed", (Action<Exception>)(_ => failures++));
        // There is no world in this process: Update must abort the effect,
        // release player control/visibility, and report exactly once.
        effect.Update();
        check(!effect.IsActive && Game1.displayFarmer && player.CanMove && player.freezePause == 0
            && !player.temporarilyInvincible && player.temporaryInvincibilityTimer == 0,
            "Interrupted scepter travel restores visibility, movement, freeze and invincibility");
        effect.Update();
        effect.Cancel();
        check(failures == 1, "Repeated ticks/cancel cannot run a stale interrupted travel callback");
    }
}
