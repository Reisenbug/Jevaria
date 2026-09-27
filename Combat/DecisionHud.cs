using System;
using System.Collections.Generic;
using System.Text;
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

        var text = new StringBuilder();
        text.Append("Jevaria: ").Append(combat.Status)
            .Append("  dodge ").Append(combat.Enabled && combat.DodgeEnabled ? "on" : "off");
        if (combat.Decision is { } decision)
        {
            text.Append("  #").Append(decision.Sequence)
                .Append("  API ").Append(decision.LatencyMs).Append("ms")
                .Append("  action ").Append(combat.ActionAgeMs).Append("ms");
            text.Append("\nintent ").Append(decision.Intent)
                .Append("\napplied ").Append(combat.LastAction.Applied);
            if (!string.IsNullOrEmpty(combat.LastAction.Reason))
                text.Append("  (").Append(combat.LastAction.Reason).Append(')');
            foreach (var question in decision.Probabilities)
            {
                text.Append('\n').Append(question.Key).Append(':');
                foreach (var choice in question.Value)
                    text.Append(' ').Append(choice.Key).Append(' ')
                        .Append((int)Math.Round(choice.Value * 100f)).Append('%');
            }
        }
        if (combat.Snapshot is { } snapshot)
        {
            text.Append("\nbosses");
            foreach (CombatEntity boss in snapshot.Bosses)
                text.Append(' ').Append(boss.Name).Append('#').Append(boss.Id);
            foreach (BossMotion motion in snapshot.BossMotion)
                text.Append("\n#").Append(motion.Id).Append(" HP ")
                    .Append(motion.Health).Append('/').Append(motion.MaxHealth)
                    .Append(" speed ").Append(motion.BossSpeedCellsPerSecond.ToString("0.0"))
                    .Append('/').Append(motion.BossFastestInTheLastSecond.ToString("0.0"));
            text.Append("\nshot ").Append(snapshot.WeaponName)
                .Append(" projectile#").Append(snapshot.ShotProjectileType)
                .Append(" speed ").Append(snapshot.ShotSpeed.ToString("0.0"));
            text.Append("\nsolid L/R/U/D ")
                .Append((int)snapshot.SolidDistances.Left).Append('/')
                .Append((int)snapshot.SolidDistances.Right).Append('/')
                .Append((int)snapshot.SolidDistances.Up).Append('/')
                .Append((int)snapshot.SolidDistances.Down);
            text.Append("  world ")
                .Append((int)snapshot.WorldDistances.Left).Append('/')
                .Append((int)snapshot.WorldDistances.Right).Append('/')
                .Append((int)snapshot.WorldDistances.Up).Append('/')
                .Append((int)snapshot.WorldDistances.Down);
        }

        Utils.DrawBorderString(Main.spriteBatch, text.ToString(), new Vector2(20f, 120f),
            Color.White, 0.75f);

        if (combat.Enabled && combat.AimTarget is { } target)
        {
            Vector2 screen = target - Main.screenPosition;
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            Main.spriteBatch.Draw(pixel, new Rectangle((int)screen.X - 7, (int)screen.Y, 15, 2), Color.Lime);
            Main.spriteBatch.Draw(pixel, new Rectangle((int)screen.X, (int)screen.Y - 7, 2, 15), Color.Lime);
        }

        if (combat.Enabled && combat.DodgeEnabled && combat.Decision is { } active)
        {
            Vector2 player = Main.LocalPlayer.Center - Main.screenPosition;
            Vector2 direction = new((int)active.Intent.Horizontal, (int)active.Intent.Vertical);
            if (direction != Vector2.Zero)
            {
                direction.Normalize();
                Vector2 end = player + direction * 48f;
                float angle = (float)Math.Atan2(direction.Y, direction.X);
                Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, player, null,
                    Color.Orange, angle, Vector2.Zero, new Vector2(48f, 3f),
                    SpriteEffects.None, 0f);
                Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle((int)end.X - 3, (int)end.Y - 3, 6, 6), Color.Orange);
            }
        }
        return true;
    }
}
