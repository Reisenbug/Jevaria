using System;
using System.Text.Json;
using Jevaria.Combat;
using Microsoft.Xna.Framework;

Check(new Vector2(0, 0), new Vector2(100, 0), Vector2.Zero, 10, new Vector2(100, 0));
Check(new Vector2(0, 0), new Vector2(100, 0), new Vector2(0, 5), 10,
    new Vector2(100, 57.735027f));
Check(new Vector2(20, 10), new Vector2(120, 10), new Vector2(-5, 0), 10,
    new Vector2(86.66667f, 10));
if (Intercept.TrySolve(Vector2.Zero, new Vector2(100, 0), new Vector2(20, 0), 10, out _))
    throw new Exception("Faster escaping target must be unreachable");
if (Intercept.TrySolve(Vector2.Zero, new Vector2(100, 0), Vector2.Zero, 0, out _))
    throw new Exception("Zero shot speed must be rejected");
string coordinates = JsonSerializer.Serialize(new Vector2(3, 4), new JsonSerializerOptions
{
    IncludeFields = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
});
if (coordinates != "{\"x\":3,\"y\":4}")
    throw new Exception($"State lost coordinates: {coordinates}");
Console.WriteLine("Intercept checks passed");

static void Check(Vector2 origin, Vector2 target, Vector2 velocity, float shotSpeed,
    Vector2 expected)
{
    if (!Intercept.TrySolve(origin, target, velocity, shotSpeed, out Vector2 actual) ||
        Vector2.Distance(actual, expected) > 0.01f)
        throw new Exception($"Expected {expected}, got {actual}");
    float time = Vector2.Distance(origin, actual) / shotSpeed;
    if (Vector2.Distance(actual, target + velocity * time) > 0.01f)
        throw new Exception("Projectile and target do not meet");
}
