using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace Jevaria.Combat;

public sealed class ProjectileAim : IAimDriver
{
    public bool TryAim(Player player, CombatSnapshot snapshot, out Vector2 target)
    {
        target = snapshot.Boss.Center;
        if (player.HeldItem.shoot == ProjectileID.None || snapshot.ShotSpeed <= 0f) return false;
        return Intercept.TrySolve(player.MountedCenter, snapshot.Boss.Center,
            snapshot.Boss.Velocity, snapshot.ShotSpeed, out target);
    }
}
