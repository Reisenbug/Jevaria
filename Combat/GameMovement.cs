using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace Jevaria.Combat;

public sealed class GameMovement : IMovementDriver
{
    private ulong _hookSequence;
    private ulong _jumpSequence;
    private bool _autoHookActive;
    private bool _hookHeld;
    private bool _jumpHeld;
    private int _jumpTicks;
    private ulong _hookIssuedTick;
    private ulong _hookCooldownUntil;
    private Vector2 _hookTarget;
    private float _previousHookDistance = float.MaxValue;

    public ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot)
    {
        if (!intent.Valid) return new ActionResult(intent, DodgeIntent.Idle, "invalid intent");

        (int horizontal, int vertical) = Components(intent.Direction);
        Magnitude size = intent.Size;
        string reason = "";
        Rectangle box = player.Hitbox;
        BoundaryDistances solid = GameSensor.ScanSolids(box);
        BoundaryDistances world = new(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);

        if (world.Left < 80f || solid.Left < 32f)
            if (horizontal < 0) { horizontal = 0; reason = "left blocked"; }
        if (world.Right < 80f || solid.Right < 32f)
            if (horizontal > 0) { horizontal = 0; reason = "right blocked"; }

        if (world.Up < 64f || solid.Up < 24f)
        {
            if (vertical < 0) { vertical = 0; reason = "upper boundary"; }
        }
        if (world.Down < 64f || solid.Down < 24f)
        {
            if (vertical > 0) { vertical = 0; reason = "lower boundary"; }
        }

        bool moving = horizontal != 0 || vertical != 0;
        if (!moving) size = Magnitude.None;
        bool wantHook = moving && size == Magnitude.Large;
        float hookDistance = Vector2.Distance(player.Center, _hookTarget);
        bool hookFinished = _autoHookActive && player.grapCount > 0 &&
            hookDistance >= _previousHookDistance && Main.GameUpdateCount > _hookIssuedTick + 2;
        if (_autoHookActive && (!wantHook ||
            Main.GameUpdateCount - _hookIssuedTick >= 45 || hookFinished))
        {
            Release(player);
            reason = "grapple released";
        }
        if (_autoHookActive && player.grapCount > 0) _previousHookDistance = hookDistance;
        if (player.grapCount > 0 && !_autoHookActive && reason != "grapple released")
            return new ActionResult(intent, DodgeIntent.Idle, "manual grapple");

        if (wantHook && !_autoHookActive && player.grapCount == 0 &&
            Main.GameUpdateCount >= _hookCooldownUntil && _hookSequence != snapshot.Sequence)
        {
            _hookSequence = snapshot.Sequence;
            if (snapshot.HasHook && TryHookPoint(player, horizontal, vertical, out Vector2 hook))
            {
                _autoHookActive = true;
                _hookIssuedTick = Main.GameUpdateCount;
                _hookTarget = hook;
                _previousHookDistance = float.MaxValue;
                Main.mouseX = (int)(hook.X - Main.screenPosition.X);
                Main.mouseY = (int)(hook.Y - Main.screenPosition.Y);
                player.releaseHook = true;
                player.controlHook = true;
                _hookHeld = true;
                reason = "grapple";
            }
            else
            {
                size = Magnitude.Medium;
                reason = "grapple unavailable";
            }
        }
        else if (_autoHookActive && player.grapCount == 0)
        {
            _hookHeld = !_hookHeld;
            player.controlHook = _hookHeld;
            if (_hookHeld)
            {
                Main.mouseX = (int)(_hookTarget.X - Main.screenPosition.X);
                Main.mouseY = (int)(_hookTarget.Y - Main.screenPosition.Y);
            }
        }
        else player.controlHook = false;
        if (wantHook && !_autoHookActive && size == Magnitude.Large)
        {
            size = Magnitude.Medium;
            reason = "grapple unavailable";
        }

        bool canJump = !(_autoHookActive && player.grapCount > 0);
        if (vertical < 0 && size == Magnitude.Medium && !snapshot.CanDoubleJump)
        {
            size = Magnitude.Small;
            reason = "extra jump unavailable";
        }
        player.controlLeft = horizontal < 0;
        player.controlRight = horizontal > 0;
        player.controlUp = player.grapCount == 0 && vertical == 0 && player.velocity.Y != 0f;
        player.controlDown = player.grapCount == 0 && vertical > 0;
        bool useExtraJump = canJump && vertical < 0 && size == Magnitude.Medium &&
            snapshot.CanDoubleJump && _jumpSequence != snapshot.Sequence;
        player.controlJump = ApplyJump(player, canJump && vertical < 0, useExtraJump, snapshot.Sequence);
        var applied = new DodgeIntent(Compose(horizontal, vertical), moving ? size : Magnitude.None, false);
        return new ActionResult(intent, applied, reason);
    }

    private static (int Horizontal, int Vertical) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.Up => (0, -1),
        DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Right => (1, 0),
        DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1),
        DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.Left => (-1, 0),
        DodgeDirection.UpLeft => (-1, -1),
        _ => (0, 0)
    };

    private static DodgeDirection Compose(int horizontal, int vertical) => (horizontal, vertical) switch
    {
        (0, -1) => DodgeDirection.Up,
        (1, -1) => DodgeDirection.UpRight,
        (1, 0) => DodgeDirection.Right,
        (1, 1) => DodgeDirection.DownRight,
        (0, 1) => DodgeDirection.Down,
        (-1, 1) => DodgeDirection.DownLeft,
        (-1, 0) => DodgeDirection.Left,
        (-1, -1) => DodgeDirection.UpLeft,
        _ => DodgeDirection.Stay
    };

    public void Release(Player player)
    {
        _jumpHeld = false;
        _jumpTicks = 0;
        if (!_autoHookActive) return;
        player.RemoveAllGrapplingHooks();
        _autoHookActive = false;
        _hookHeld = false;
        _hookCooldownUntil = Main.GameUpdateCount + 30;
    }

    private bool ApplyJump(Player player, bool rise, bool hop, ulong sequence)
    {
        if (_jumpHeld)
        {
            if (hop)
            {
                _jumpHeld = false;
                return false;
            }
            _jumpTicks++;
            bool landed = player.velocity.Y == 0f && _jumpTicks > 2;
            if (rise && !landed) return true;
            if (player.velocity.Y < 0f && _jumpTicks < 30 && !landed) return true;
            _jumpHeld = false;
            return false;
        }

        if (!rise && !hop) return false;
        if (hop) _jumpSequence = sequence;
        _jumpHeld = true;
        _jumpTicks = 0;
        return true;
    }

    private static bool TryHookPoint(Player player, int horizontal,
        int vertical, out Vector2 target)
    {
        target = default;
        float range = HookRange(player);
        if (range <= 0f) return false;
        Vector2 step = Vector2.Normalize(new Vector2(horizontal, vertical)) * 4f;
        Vector2 point = player.Center;
        for (float distance = 0f; distance <= range; distance += 4f, point += step)
        {
            int x = (int)(point.X / 16f);
            int y = (int)(point.Y / 16f);
            if (!WorldGen.InWorld(x, y, 2)) break;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated ||
                !Main.tileSolid[tile.TileType] && tile.TileType != TileID.MinecartTrack) continue;
            target = new Vector2(x * 16f + 8f, y * 16f + 8f);
            return true;
        }
        return false;
    }

    private static float HookRange(Player player)
    {
        int type = player.miscEquips[4].shoot;
        if (type <= 0 || !Main.projHook[type])
        {
            type = 0;
            for (int slot = 0; slot < 58 && type == 0; slot++)
            {
                int candidate = player.inventory[slot].shoot;
                if (candidate > 0 && Main.projHook[candidate]) type = candidate;
            }
            if (type == 0) return 0f;
        }
        if (type >= ProjectileID.Count)
            return Terraria.ModLoader.ProjectileLoader.GetProjectile(type)?.GrappleRange() ?? 0f;
        return type switch
        {
            13 or 396 or 865 => 300f,
            32 or 331 or 372 => 400f,
            73 or 74 => 440f,
            165 => 375f,
            256 => 350f,
            315 or 446 or 935 => 500f,
            322 or 332 => 550f,
            >= 646 and <= 649 => 550f,
            652 => 600f,
            >= 486 and <= 489 => 480f,
            >= 230 and <= 235 => 300f + (type - 230) * 30f,
            753 => 420f,
            _ => 2500f
        };
    }
}
