using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace Jevaria.Combat;

public sealed class GameSensor : ICombatSensor
{
    public CombatSnapshot? Capture(Player player, ulong sequence, DodgeIntent previous, long previousDurationMs)
    {
        NPC? boss = null;
        float closest = float.MaxValue;
        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || !npc.boss || npc.friendly) continue;
            float distance = Vector2.DistanceSquared(player.Center, npc.Center);
            if (distance >= closest) continue;
            closest = distance;
            boss = npc;
        }

        if (boss is null) return null;

        var parts = new List<CombatEntity>();
        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || npc.friendly || npc.whoAmI == boss.whoAmI) continue;
            if (npc.realLife == boss.whoAmI)
                parts.Add(Entity(npc.whoAmI, npc.FullName, npc.Center, npc.velocity,
                    npc.width, npc.height, npc.damage));
        }

        var projectiles = new List<CombatEntity>();
        foreach (Projectile projectile in Main.projectile)
        {
            if (!projectile.active || !projectile.hostile || projectile.damage <= 0) continue;
            if (Vector2.DistanceSquared(player.Center, projectile.Center) > 1600f * 1600f) continue;
            projectiles.Add(Entity(projectile.whoAmI, projectile.Name, projectile.Center,
                projectile.velocity * (projectile.extraUpdates + 1), projectile.width,
                projectile.height, projectile.damage));
        }

        Rectangle box = player.Hitbox;
        var world = new BoundaryDistances(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);
        bool hasMount = player.miscEquips[3].mountType >= MountID.Rudolph;

        return new CombatSnapshot(sequence, Main.GameUpdateCount,
            Entity(player.whoAmI, player.name, player.Center, player.velocity,
                player.width, player.height, 0), player.statLife,
            Entity(boss.whoAmI, boss.FullName, boss.Center, boss.velocity,
                boss.width, boss.height, boss.damage), parts, projectiles,
            ScanSolids(box), world, player.dashType > 0 && player.dashDelay == 0,
            player.AnyExtraJumpUsable(), player.wingTime > 0f,
            player.miscEquips[4].shoot != ProjectileID.None, hasMount,
            player.HeldItem.shootSpeed, player.HeldItem.Name, previous, previousDurationMs);
    }

    private static CombatEntity Entity(int id, string name, Vector2 center,
        Vector2 velocity, int width, int height, int damage)
        => new(id, name, center, velocity, new Vector2(width, height), damage);

    private static BoundaryDistances ScanSolids(Rectangle box)
    {
        float left = 1600f, right = 1600f, up = 1600f, down = 1600f;
        int x0 = Math.Max(0, box.Left / 16);
        int x1 = Math.Min(Main.maxTilesX - 1, (box.Right - 1) / 16);
        int y0 = Math.Max(0, box.Top / 16);
        int y1 = Math.Min(Main.maxTilesY - 1, (box.Bottom - 1) / 16);

        for (int step = 1; step <= 100; step++)
        {
            int lx = x0 - step, rx = x1 + step;
            if (left == 1600f && lx >= 0 && SolidColumn(lx, y0, y1))
                left = box.Left - (lx + 1) * 16f;
            if (right == 1600f && rx < Main.maxTilesX && SolidColumn(rx, y0, y1))
                right = rx * 16f - box.Right;
            int uy = y0 - step, dy = y1 + step;
            if (up == 1600f && uy >= 0 && SolidRow(uy, x0, x1))
                up = box.Top - (uy + 1) * 16f;
            if (down == 1600f && dy < Main.maxTilesY && SolidRow(dy, x0, x1))
                down = dy * 16f - box.Bottom;
            if (left < 1600f && right < 1600f && up < 1600f && down < 1600f) break;
        }
        return new BoundaryDistances(left, right, up, down);
    }

    private static bool SolidColumn(int x, int first, int last)
    {
        for (int y = first; y <= last; y++) if (Solid(x, y)) return true;
        return false;
    }

    private static bool SolidRow(int y, int first, int last)
    {
        for (int x = first; x <= last; x++) if (Solid(x, y)) return true;
        return false;
    }

    private static bool Solid(int x, int y)
    {
        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType]
            && !Main.tileSolidTop[tile.TileType] && tile.Slope == 0 && !tile.IsHalfBlock;
    }
}
