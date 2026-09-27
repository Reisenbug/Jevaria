using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace Jevaria.Combat;

public enum Direction { Negative = -1, None = 0, Positive = 1 }
public enum Magnitude { None, Small, Medium, Large }

public readonly record struct DodgeIntent(
    Direction Horizontal, Direction Vertical,
    Magnitude HorizontalMagnitude, Magnitude VerticalMagnitude, bool Dash)
{
    public static DodgeIntent Idle => new(Direction.None, Direction.None, Magnitude.None, Magnitude.None, false);

    public bool Valid =>
        (Horizontal == Direction.None) == (HorizontalMagnitude == Magnitude.None) &&
        (Vertical == Direction.None) == (VerticalMagnitude == Magnitude.None);
}

public readonly record struct CombatEntity(
    int Id, string Name, Vector2 Center, Vector2 Velocity,
    Vector2 Size, int Damage);

public readonly record struct BoundaryDistances(
    float Left, float Right, float Up, float Down);

public readonly record struct BossMotion(
    int Id, float BossSpeedCellsPerSecond, float BossFastestInTheLastSecond,
    int Health, int MaxHealth);

public sealed record CombatSnapshot(
    ulong Sequence, ulong Tick, CombatEntity Player, int Health,
    CombatEntity Boss, IReadOnlyList<CombatEntity> Bosses,
    IReadOnlyList<BossMotion> BossMotion,
    IReadOnlyList<CombatEntity> Parts,
    IReadOnlyList<CombatEntity> Projectiles,
    BoundaryDistances SolidDistances, BoundaryDistances WorldDistances,
    bool CanDash, bool CanDoubleJump, bool CanFly, bool HasHook,
    bool HasMount, float ShotSpeed, int ShotProjectileType, string WeaponName,
    DodgeIntent PreviousIntent, long PreviousDurationMs);

public sealed record DodgeDecision(
    DodgeIntent Intent, IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Probabilities,
    long LatencyMs, ulong Sequence);

public readonly record struct ActionResult(DodgeIntent Requested, DodgeIntent Applied, string Reason);

public interface ICombatSensor
{
    void Observe(Player player);
    CombatSnapshot? Capture(Player player, ulong sequence, DodgeIntent previous, long previousDurationMs);
}

public interface IMovementDriver
{
    ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot);
    void Release(Player player);
}

public interface IAttackDriver
{
    bool TryAttack(Player player, CombatSnapshot snapshot, out Vector2 target);
    void Release(Player player);
}

public interface IDodgeBrain
{
    System.Threading.Tasks.Task<DodgeDecision> DecideAsync(CombatSnapshot snapshot,
        System.Threading.CancellationToken cancellationToken);
}
