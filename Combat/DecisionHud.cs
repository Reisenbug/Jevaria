using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;

namespace Jevaria.Combat;

public sealed class DecisionHud : ModSystem
{
    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (index < 0) index = layers.Count;
        layers.Insert(index, new LegacyGameInterfaceLayer("Jevaria: Decision",
            Draw, InterfaceScaleType.UI));
    }

    private static bool Draw()
    {
        if (Main.gameMenu || Main.LocalPlayer == null) return true;
        CombatPlayer combat = Main.LocalPlayer.GetModPlayer<CombatPlayer>();
        if (!combat.ShowHud) return true;

        string status = $"Jevaria: {combat.Status} | dodge {(combat.Enabled && combat.DodgeEnabled ? "on" : "off")}";
        Utils.DrawBorderString(Main.spriteBatch, status, new Vector2(20f, 120f), Color.White, 0.75f);

        if (combat.Enabled && combat.AimTarget is { } target)
        {
            Vector2 screen = target - Main.screenPosition;
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            Main.spriteBatch.Draw(pixel, new Rectangle((int)screen.X - 7, (int)screen.Y, 15, 2), Color.Lime);
            Main.spriteBatch.Draw(pixel, new Rectangle((int)screen.X, (int)screen.Y - 7, 2, 15), Color.Lime);
        }

        return true;
    }
}
