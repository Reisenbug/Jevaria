using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace Jevaria.Combat;

public sealed class GameMovement : IMovementDriver
{
    private ulong _hookSequence;
    private ulong _dashSequence;
    private ulong _jumpSequence;
    private ulong _dropSequence;
    private float _dropStartY;
    private int _dropTicks;
    private bool _autoHookActive;
    private bool _jumpHeld;
    private int _jumpTicks;
    private ulong _hookIssuedTick;
    private ulong _hookCooldownUntil;
    private Vector2 _hookTarget;

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
        {
            if (horizontal < 0) { horizontal = 1; size = Magnitude.Small; reason = "left boundary"; }
        }
        else if (world.Right < 80f || solid.Right < 32f)
        {
            if (horizontal > 0) { horizontal = -1; size = Magnitude.Small; reason = "right boundary"; }
        }

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
        if (_autoHookActive && (!wantHook ||
            Main.GameUpdateCount - _hookIssuedTick >= 45 ||
            player.grapCount > 0 && Vector2.Distance(player.Center, _hookTarget) < 48f))
        {
            Release(player);
            reason = "grapple released";
        }
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
                Main.mouseX = (int)(hook.X - Main.screenPosition.X);
                Main.mouseY = (int)(hook.Y - Main.screenPosition.Y);
                player.releaseHook = true;
                player.controlHook = true;
                reason = "grapple";
            }
            else
            {
                size = vertical < 0 && snapshot.CanDoubleJump ? Magnitude.Medium : Magnitude.Small;
                reason = "grapple unavailable";
            }
        }
        else player.controlHook = false;
        if (wantHook && !_autoHookActive && size == Magnitude.Large)
        {
            size = vertical < 0 && snapshot.CanDoubleJump ? Magnitude.Medium : Magnitude.Small;
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
        player.controlUp = vertical < 0;
        player.controlDown = vertical > 0;
        bool useExtraJump = canJump && vertical < 0 && size == Magnitude.Medium &&
            snapshot.CanDoubleJump && _jumpSequence != snapshot.Sequence;
        player.controlJump = ApplyJump(player, canJump && vertical < 0, useExtraJump, snapshot.Sequence);
        if (vertical > 0 && size == Magnitude.Small)
        {
            if (_dropSequence != snapshot.Sequence)
            {
                _dropSequence = snapshot.Sequence;
                _dropStartY = player.Bottom.Y;
                _dropTicks = 0;
            }
            _dropTicks++;
            if (player.Bottom.Y >= _dropStartY + 16f || _dropTicks > 20)
                player.controlDown = false;
        }

        bool dash = intent.Dash && horizontal != 0 && snapshot.CanDash;
        if (dash && _dashSequence != snapshot.Sequence)
        {
            _dashSequence = snapshot.Sequence;
            player.dashTime = horizontal > 0 ? 15 : -15;
            if (horizontal > 0) player.releaseRight = true;
            else player.releaseLeft = true;
        }
        else if (intent.Dash && !dash) reason = "dash unavailable";

        var applied = new DodgeIntent(Compose(horizontal, vertical), moving ? size : Magnitude.None, dash);
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
        int cx = (int)(player.Center.X / 16f);
        int cy = (int)(player.Center.Y / 16f);
        int dx = horizontal;
        int dy = vertical;
        if (dx == 0 && dy == 0) dy = -1;

        for (int step = 4; step <= 18; step++)
        {
            int x = cx + dx * step;
            int y = cy + dy * step;
            for (int side = -3; side <= 3; side++)
            {
                int sx = x + (dy != 0 ? side : 0);
                int sy = y + (dx != 0 ? side : 0);
                if (!WorldGen.InWorld(sx, sy, 2) || !WorldGen.SolidTile(sx, sy)) continue;
                target = new Vector2(sx * 16f + 8f, sy * 16f + 8f);
                return true;
            }
        }
        target = default;
        return false;
    }
}
