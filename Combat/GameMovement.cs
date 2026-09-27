using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace Jevaria.Combat;

public sealed class GameMovement : IMovementDriver
{
    public static IReadOnlyList<DodgeOption> AvailableActions(Player player,
        BoundaryDistances solid, BoundaryDistances world, bool hasHook, bool hasMount)
    {
        var options = new List<DodgeOption> { new(DodgeIntent.Idle, null) };
        (int X, Magnitude Size)[] horizontal =
        {
            (0, Magnitude.None), (-1, Magnitude.Small), (-1, Magnitude.Medium),
            (1, Magnitude.Small), (1, Magnitude.Medium)
        };
        (int Y, Magnitude Size)[] vertical =
        {
            (0, Magnitude.None), (-1, Magnitude.Small), (-1, Magnitude.Medium),
            (-1, Magnitude.Large), (1, Magnitude.Small), (1, Magnitude.Large)
        };
        var hookTargets = new Dictionary<(int X, int Y, bool NearHorizontal), float?>();
        float worldReserve = (GameSensor.WorldEdgeCells + GameSensor.EscapeReserveCells) * 16f;
        float solidReserve = GameSensor.EscapeReserveCells * 16f;
        foreach (var (x, horizontalSize) in horizontal)
        foreach (var (y, verticalSize) in vertical)
        {
            if (x == 0 && y == 0) continue;
            if (x < 0 && (world.Left <= worldReserve || solid.Left <= solidReserve) ||
                x > 0 && (world.Right <= worldReserve || solid.Right <= solidReserve) ||
                y < 0 && (world.Up <= worldReserve || solid.Up <= solidReserve) ||
                y > 0 && (world.Down <= worldReserve || solid.Down <= solidReserve)) continue;
            bool horizontalHook = horizontalSize == Magnitude.Medium;
            bool verticalHook = y < 0 && verticalSize >= Magnitude.Medium;
            bool upMount = y < 0 && verticalSize == Magnitude.Large;
            bool downMount = y > 0 && verticalSize == Magnitude.Large;
            if ((upMount || downMount) && !hasMount || downMount && horizontalHook) continue;
            float? hookDistance = null;
            if (horizontalHook || verticalHook)
            {
                var direction = (horizontalHook ? x : 0, verticalHook ? y : 0,
                    horizontalHook && !verticalHook);
                if (!hookTargets.TryGetValue(direction, out hookDistance))
                {
                    if (hasHook && TryHookPoint(player, direction.Item1, direction.Item2,
                        direction.Item3, out Vector2 target))
                        hookDistance = Vector2.Distance(player.Center, target) / 16f;
                    hookTargets[direction] = hookDistance;
                }
                if (hookDistance is null && !upMount) continue;
                if (hookDistance is null && horizontalHook) continue;
            }
            options.Add(new DodgeOption(new DodgeIntent(Compose(x, y), horizontalSize,
                verticalSize, false), hookDistance));
        }
        return options;
    }

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
    private bool _hookPulled;
    private bool _autoMountActive;
    private bool _autoMountForDown;
    private bool _mountRiseSeen;
    private ulong _mountStartTick;
    private ulong _upMountSequence;

    public ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot)
    {
        if (!intent.Valid) return new ActionResult(intent, DodgeIntent.Idle, "invalid intent");

        (int horizontal, int vertical) = Components(intent.Direction);
        Magnitude horizontalSize = intent.HorizontalSize;
        Magnitude verticalSize = intent.VerticalSize;
        string reason = "";
        Rectangle box = player.Hitbox;
        BoundaryDistances solid = GameSensor.ScanSolids(box);
        BoundaryDistances world = new(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);

        float worldReserve = (GameSensor.WorldEdgeCells + GameSensor.EscapeReserveCells) * 16f;
        float solidReserve = GameSensor.EscapeReserveCells * 16f;
        if (world.Left <= worldReserve || solid.Left <= solidReserve)
            if (horizontal < 0) { horizontal = 0; reason = "left blocked"; }
        if (world.Right <= worldReserve || solid.Right <= solidReserve)
            if (horizontal > 0) { horizontal = 0; reason = "right blocked"; }

        if (world.Up <= worldReserve || solid.Up <= solidReserve)
        {
            if (vertical < 0) { vertical = 0; reason = "upper boundary"; }
        }
        if (world.Down <= worldReserve || solid.Down <= solidReserve)
        {
            if (vertical > 0) { vertical = 0; reason = "lower boundary"; }
        }

        bool slimeEquipped = player.miscEquips[3].type == ItemID.SlimySaddle;
        if (verticalSize == Magnitude.Large && !slimeEquipped)
        {
            verticalSize = vertical < 0 ? Magnitude.Medium : Magnitude.Small;
            reason = "slime mount unavailable";
        }
        bool upMount = slimeEquipped && vertical < 0 && verticalSize == Magnitude.Large;
        bool downMount = slimeEquipped && vertical > 0 && verticalSize == Magnitude.Large;
        if (downMount && horizontalSize == Magnitude.Medium)
        {
            horizontalSize = Magnitude.Small;
            reason = "horizontal grapple unavailable while mounted";
        }
        if (_autoMountActive && downMount) _autoMountForDown = true;
        if (_autoMountActive && ((!upMount && !downMount) || upMount && _autoMountForDown ||
            !_autoMountForDown && (_mountRiseSeen && player.velocity.Y >= 0f ||
            !_mountRiseSeen && Main.GameUpdateCount - _mountStartTick > 6)))
        {
            DismountAuto(player);
            reason = "slime dismount";
        }

        if (horizontal == 0) horizontalSize = Magnitude.None;
        if (vertical == 0) verticalSize = Magnitude.None;
        bool horizontalHook = horizontal != 0 && horizontalSize == Magnitude.Medium;
        bool verticalHook = vertical < 0 && verticalSize >= Magnitude.Medium;
        bool wantHook = !downMount && (horizontalHook || verticalHook);
        float hookDistance = Vector2.Distance(player.Center, _hookTarget);
        if (_autoHookActive && player.grapCount > 0 && !_hookPulled &&
            hookDistance < _previousHookDistance - 0.1f)
        {
            _hookPulled = true;
            reason = "grapple pulling";
        }
        bool hookFinished = _autoHookActive && player.grapCount > 0 && _hookPulled &&
            hookDistance >= _previousHookDistance - 0.1f;
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
            if (snapshot.HasHook && TryHookPoint(player, horizontalHook ? horizontal : 0,
                verticalHook ? vertical : 0, horizontalHook && !verticalHook, out Vector2 hook))
            {
                _autoHookActive = true;
                _hookIssuedTick = Main.GameUpdateCount;
                _hookTarget = hook;
                _previousHookDistance = float.MaxValue;
                _hookPulled = false;
                Main.mouseX = (int)(hook.X - Main.screenPosition.X);
                Main.mouseY = (int)(hook.Y - Main.screenPosition.Y);
                player.releaseHook = true;
                player.controlHook = true;
                _hookHeld = true;
                reason = "grapple";
            }
            else
            {
                if (horizontalHook) horizontalSize = Magnitude.Small;
                if (verticalSize >= Magnitude.Medium && !upMount) verticalSize = Magnitude.Small;
                reason = "grapple unavailable";
            }
        }
        else if (_autoHookActive && player.grapCount == 0)
        {
            reason = "grapple";
            _hookHeld = !_hookHeld;
            player.controlHook = _hookHeld;
            if (_hookHeld)
            {
                Main.mouseX = (int)(_hookTarget.X - Main.screenPosition.X);
                Main.mouseY = (int)(_hookTarget.Y - Main.screenPosition.Y);
            }
        }
        else player.controlHook = false;
        if (wantHook && !_autoHookActive &&
            (horizontalSize == Magnitude.Medium || verticalSize >= Magnitude.Medium))
        {
            if (horizontalHook) horizontalSize = Magnitude.Small;
            if (verticalSize >= Magnitude.Medium && !upMount) verticalSize = Magnitude.Small;
            reason = "grapple unavailable";
        }

        bool canJump = !(_autoHookActive && player.grapCount > 0);
        player.controlLeft = horizontal < 0;
        player.controlRight = horizontal > 0;
        player.controlUp = player.grapCount == 0 && vertical < 0;
        player.controlDown = player.grapCount == 0 && vertical > 0;
        bool useExtraJump = canJump && vertical < 0 &&
            snapshot.CanDoubleJump && _jumpSequence != snapshot.Sequence;
        player.controlJump = ApplyJump(player, canJump && vertical < 0, useExtraJump, snapshot.Sequence);
        if (upMount && !_autoMountActive && _upMountSequence != snapshot.Sequence &&
            player.controlJump && (player.velocity.Y <= 0f || hookFinished) &&
            (!_autoHookActive && player.grapCount == 0 || hookFinished))
        {
            if (MountAuto(player, false))
            {
                _upMountSequence = snapshot.Sequence;
                reason = "slime mount jump";
            }
            else
            {
                verticalSize = Magnitude.Small;
                reason = "slime mount unavailable";
            }
        }
        if (downMount && !_autoMountActive)
        {
            if (MountAuto(player, true)) reason = "slime mount descent";
            else
            {
                verticalSize = Magnitude.Small;
                reason = "slime mount unavailable";
            }
        }
        if (_autoMountActive && !_autoMountForDown && player.velocity.Y < -0.1f)
            _mountRiseSeen = true;
        var applied = new DodgeIntent(Compose(horizontal, vertical), horizontalSize, verticalSize, false);
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
        DismountAuto(player);
        if (!_autoHookActive) return;
        player.RemoveAllGrapplingHooks();
        _autoHookActive = false;
        _hookHeld = false;
        _hookPulled = false;
        _hookCooldownUntil = Main.GameUpdateCount + 30;
    }

    private bool MountAuto(Player player, bool down)
    {
        if (player.mount.Active || !player.mount.CanMount(MountID.Slime, player)) return false;
        player.mount.SetMount(MountID.Slime, player);
        if (!player.mount.Active || player.mount.Type != MountID.Slime) return false;
        _autoMountActive = true;
        _autoMountForDown = down;
        _mountRiseSeen = false;
        _mountStartTick = Main.GameUpdateCount;
        return true;
    }

    private void DismountAuto(Player player)
    {
        if (!_autoMountActive) return;
        if (player.mount.Active && player.mount.Type == MountID.Slime)
            player.mount.Dismount(player);
        _autoMountActive = false;
        _autoMountForDown = false;
        _mountRiseSeen = false;
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
        int vertical, bool nearHorizontal, out Vector2 target)
    {
        target = default;
        float range = HookRange(player);
        if (range <= 0f) return false;
        foreach (float tilt in nearHorizontal ? new[] { 0f, -0.35f, 0.35f } : new[] { 0f })
        {
            Vector2 step = Vector2.Normalize(new Vector2(horizontal, nearHorizontal ? tilt : vertical)) * 4f;
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
