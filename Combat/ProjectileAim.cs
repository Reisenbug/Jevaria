using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace Jevaria.Combat;

public sealed class ProjectileAim : IAttackDriver
{
    public bool TryAttack(Player player, CombatSnapshot snapshot, out Vector2 target)
    {
        target = snapshot.Boss.Center;
        if (player.HeldItem.shoot == ProjectileID.None || snapshot.ShotSpeed <= 0f) return false;
        Vector2 origin = player.MountedCenter;
        if (player.HeldItem.type == ItemID.DartPistol)
        {
            int facing = snapshot.Boss.Center.X >= origin.X ? 1 : -1;
            origin += new Vector2(-4f * facing, -2f * player.gravDir);
        }
        if (!Intercept.TrySolve(origin, snapshot.Boss.Center,
                snapshot.Boss.Velocity, snapshot.ShotSpeed, out target)) return false;
        Main.mouseX = (int)(target.X - Main.screenPosition.X);
        Main.mouseY = (int)(target.Y - Main.screenPosition.Y);
        player.controlUseItem = true;
        return true;
    }

    public void Release(Player player) => player.controlUseItem = false;
}
