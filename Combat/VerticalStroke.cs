using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace Jevaria.Combat;

public sealed class VerticalStroke
{
    private const float MinimumStrokeCells = 15f;
    private int _sign;
    private float _startY;
    private float _targetY;
    private float _lastY;
    private int _stalledFrames;

    public void Reset()
    {
        _sign = 0;
        _stalledFrames = 0;
    }

    public VerticalLeg State(Player player, CombatSnapshot snapshot)
    {
        if (_sign == 0) return default;
        float remaining = Math.Max(0f, (_targetY - player.Center.Y) * _sign / 16f);
        float progress = Math.Max(0f, (player.Center.Y - _startY) * _sign / 16f);
        return new VerticalLeg(_sign, _targetY, remaining, progress,
            progress >= MinimumStrokeCells || ImmediateThreat(snapshot) ||
            _sign < 0 && player.velocity.Y > 0.5f && !snapshot.CanFly && !snapshot.CanDoubleJump);
    }

    public string Update(Player player, CombatSnapshot snapshot, int requestedSign)
    {
        BoundaryDistances solid = GameSensor.ScanSolids(player.Hitbox);
        Rectangle box = player.Hitbox;
        BoundaryDistances world = new(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);
        if (_sign == 0)
            return requestedSign == 0 ? "" : Begin(player, requestedSign, solid, world, "start");

        bool blocked = GameMovement.Blocked(0, _sign, solid, world, player.velocity);
        bool reached = (player.Center.Y - _targetY) * _sign >= 0f;
        _stalledFrames = Math.Abs(player.velocity.Y) < 0.3f &&
            (player.Center.Y - _lastY) * _sign < 0.5f
            ? _stalledFrames + 1 : 0;
        _lastY = player.Center.Y;
        if (blocked || reached || _stalledFrames >= 12)
            return Begin(player, -_sign, solid, world,
                $"{(blocked ? "boundary" : reached ? "target" : "vertical stall")} after {State(player, snapshot).ProgressCells:0.0} cells");
        if (requestedSign == -_sign && State(player, snapshot).CanReverse)
            return Begin(player, requestedSign, solid, world,
                $"route reversal after {State(player, snapshot).ProgressCells:0.0} cells");
        return "";
    }

    public DodgeIntent Resolve(DodgeIntent chosen, Player player, CombatSnapshot snapshot)
    {
        (int horizontal, _) = Components(chosen.Direction);
        Rectangle box = player.Hitbox;
        BoundaryDistances world = new(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);
        if (horizontal != 0 && GameMovement.Blocked(horizontal, 0,
            GameSensor.ScanSolids(box), world, player.velocity))
            horizontal = 0;
        Magnitude horizontalSize = horizontal == 0 ? Magnitude.None : Magnitude.Small;
        if (_sign == 0) return new DodgeIntent(Compose(horizontal, 0), horizontalSize,
            Magnitude.None, false);
        Magnitude verticalSize = chosen.VerticalSize == Magnitude.Large &&
            Components(chosen.Direction).Y == _sign
            ? Magnitude.Large : Magnitude.Small;
        return new DodgeIntent(Compose(horizontal, _sign), horizontalSize, verticalSize, false);
    }

    private string Begin(Player player, int desiredSign, BoundaryDistances solid,
        BoundaryDistances world, string cause)
    {
        _sign = desiredSign;
        if (GameMovement.Blocked(0, _sign, solid, world, player.velocity)) _sign = -_sign;
        if (GameMovement.Blocked(0, _sign, solid, world, player.velocity))
        {
            _sign = 0;
            return "vertical room unavailable";
        }
        float available = _sign < 0 ? Math.Min(solid.Up, world.Up - GameSensor.WorldEdgeCells * 16f) :
            Math.Min(solid.Down, world.Down - GameSensor.WorldEdgeCells * 16f);
        float reserve = (_sign < 0 ? GameMovement.UpperReserveCells : GameMovement.LowerReserveCells) * 16f;
        float travel = Math.Max(0f, available - reserve);
        _startY = player.Center.Y;
        _lastY = _startY;
        _stalledFrames = 0;
        _targetY = _startY + _sign * travel;
        return $"{cause} {(_sign < 0 ? "up" : "down")} target={_targetY / 16f:0.0} travel={travel / 16f:0.0} cells";
    }

    private static bool ImmediateThreat(CombatSnapshot snapshot)
    {
        foreach (CombatEntity threat in snapshot.Bosses)
            if (ClosingSoon(threat, snapshot.Player)) return true;
        foreach (CombatEntity threat in snapshot.Projectiles)
            if (ClosingSoon(threat, snapshot.Player)) return true;
        return false;
    }

    private static bool ClosingSoon(CombatEntity threat, CombatEntity player)
    {
        Vector2 relativePosition = threat.Center - player.Center;
        Vector2 relativeVelocity = threat.Velocity - player.Velocity;
        if (Vector2.Dot(relativePosition, relativeVelocity) >= 0f) return false;
        float gap = Vector2.Distance(relativePosition, Vector2.Zero) -
            Math.Max(threat.Size.X + player.Size.X, threat.Size.Y + player.Size.Y) * 0.5f;
        return gap < 12f * 16f;
    }

    private static (int X, int Y) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.UpLeft => (-1, -1),
        DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Up => (0, -1),
        DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1),
        DodgeDirection.Left => (-1, 0),
        DodgeDirection.Right => (1, 0),
        _ => (0, 0)
    };

    private static DodgeDirection Compose(int horizontal, int vertical) => (horizontal, vertical) switch
    {
        (-1, -1) => DodgeDirection.UpLeft,
        (0, -1) => DodgeDirection.Up,
        (1, -1) => DodgeDirection.UpRight,
        (-1, 1) => DodgeDirection.DownLeft,
        (0, 1) => DodgeDirection.Down,
        (1, 1) => DodgeDirection.DownRight,
        _ => DodgeDirection.Stay
    };
}
