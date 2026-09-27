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
    private ulong _hookIssuedTick;
    private ulong _hookCooldownUntil;
    private Vector2 _hookTarget;

    public ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot)
    {
        if (!intent.Valid) return new ActionResult(intent, DodgeIntent.Idle, "invalid intent");

        Direction horizontal = intent.Horizontal;
        Direction vertical = intent.Vertical;
        Magnitude hMagnitude = intent.HorizontalMagnitude;
        Magnitude vMagnitude = intent.VerticalMagnitude;
        string reason = "";

        if (snapshot.WorldDistances.Left < 80f || snapshot.SolidDistances.Left < 32f)
        {
            if (horizontal != Direction.Positive) { horizontal = Direction.Positive; hMagnitude = Magnitude.Small; reason = "left boundary"; }
        }
        else if (snapshot.WorldDistances.Right < 80f || snapshot.SolidDistances.Right < 32f)
        {
            if (horizontal != Direction.Negative) { horizontal = Direction.Negative; hMagnitude = Magnitude.Small; reason = "right boundary"; }
        }

        if (snapshot.WorldDistances.Up < 64f || snapshot.SolidDistances.Up < 24f)
        {
            if (vertical == Direction.Negative) { vertical = Direction.None; vMagnitude = Magnitude.None; reason = "upper boundary"; }
        }
        if (snapshot.WorldDistances.Down < 64f || snapshot.SolidDistances.Down < 24f)
        {
            if (vertical == Direction.Positive) { vertical = Direction.None; vMagnitude = Magnitude.None; reason = "lower boundary"; }
        }

        bool wantHook = (horizontal != Direction.None && hMagnitude == Magnitude.Large) ||
                        (vertical == Direction.Negative && vMagnitude >= Magnitude.Medium);
        if (_autoHookActive && (!wantHook ||
            Main.GameUpdateCount - _hookIssuedTick >= 45 ||
            player.grapCount > 0 && Vector2.Distance(player.Center, _hookTarget) < 48f))
        {
            Release(player);
            reason = "grapple released";
        }
        if (player.grapCount > 0 && !_autoHookActive && reason != "grapple released")
            return new ActionResult(intent, DodgeIntent.Idle, "manual grapple");

        player.controlLeft = horizontal == Direction.Negative;
        player.controlRight = horizontal == Direction.Positive;
        player.controlUp = vertical == Direction.Negative;
        player.controlDown = vertical == Direction.Positive;
        player.controlJump = false;

        if (vertical == Direction.Negative && !(_autoHookActive && player.grapCount > 0))
        {
            player.controlJump = true;
            if (vMagnitude >= Magnitude.Medium && !snapshot.HasHook && !snapshot.CanFly)
            {
                vMagnitude = Magnitude.Small;
                reason = "no vertical ability";
            }
        }
        else if (!(_autoHookActive && player.grapCount > 0) &&
            horizontal != Direction.None && hMagnitude >= Magnitude.Medium)
        {
            if (snapshot.CanDoubleJump)
                player.controlJump = true;
            else if (hMagnitude == Magnitude.Medium)
            {
                hMagnitude = Magnitude.Small;
                reason = "extra jump unavailable";
            }
        }
        if (horizontal != Direction.None && hMagnitude >= Magnitude.Medium &&
            snapshot.CanDoubleJump && _jumpSequence != snapshot.Sequence)
        {
            _jumpSequence = snapshot.Sequence;
            player.releaseJump = true;
        }
        if (vertical == Direction.Positive && vMagnitude == Magnitude.Small)
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
                if (hMagnitude == Magnitude.Large) hMagnitude = snapshot.CanDoubleJump ? Magnitude.Medium : Magnitude.Small;
                if (vMagnitude >= Magnitude.Medium) vMagnitude = Magnitude.Small;
                reason = "grapple unavailable";
            }
        }
        else player.controlHook = false;

        if (vertical != Direction.None && vMagnitude == Magnitude.Large)
        {
            vMagnitude = Magnitude.Medium;
            reason = "mount outside first test";
        }

        bool dash = intent.Dash && horizontal != Direction.None && snapshot.CanDash;
        if (dash && _dashSequence != snapshot.Sequence)
        {
            _dashSequence = snapshot.Sequence;
            player.dashTime = horizontal == Direction.Positive ? 15 : -15;
            if (horizontal == Direction.Positive) player.releaseRight = true;
            else player.releaseLeft = true;
        }
        else if (intent.Dash && !dash) reason = "dash unavailable";

        var applied = new DodgeIntent(horizontal, vertical, hMagnitude, vMagnitude, dash);
        return new ActionResult(intent, applied, reason);
    }

    public void Release(Player player)
    {
        if (!_autoHookActive) return;
        player.RemoveAllGrapplingHooks();
        _autoHookActive = false;
        _hookCooldownUntil = Main.GameUpdateCount + 30;
    }

    private static bool TryHookPoint(Player player, Direction horizontal,
        Direction vertical, out Vector2 target)
    {
        int cx = (int)(player.Center.X / 16f);
        int cy = (int)(player.Center.Y / 16f);
        int dx = (int)horizontal;
        int dy = (int)vertical;
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
