using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.Config;

namespace Jevaria.Combat;

public sealed class JevariaConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    public string ApiKey = "";

    public const string DefaultGeneralInstruction =
        "Fight the active boss. Avoid dangerous body contact and hostile projectiles, " +
        "considering every threat and incoming projectile. Keep an escape route and room " +
        "to dodge; do not linger near solid or world boundaries. Positions and room are " +
        "in tiles, speeds are in tiles per second, and actions last until the next answer.";

    public const string DefaultInstruction =
        "Attack Spazmatism first, but dodge both eyes. Avoid body contact. " +
        "In phase one, move up and down while increasing distance from Spazmatism. " +
        "Do not run horizontally toward it; stay more than 30 cells away when possible. " +
        "Phase two begins when Spazmatism health_percent is at or below 50. " +
        "Keep away from it throughout phase two. Leave its continuous fire stream immediately. " +
        "It then charges six times, aiming at my position when each charge starts. " +
        "Change both horizontal and vertical direction to dodge each fast charge; " +
        "do not keep running straight while it catches me. " +
        "Leave walls, the floor, the ceiling, and world edges early. " +
        "Never let Spazmatism pin me against a boundary; gain vertical room before crossing past it.";

    public string GeneralInstruction = DefaultGeneralInstruction;
    public string Instruction = DefaultInstruction;
}

public sealed class JevBrain : IDodgeBrain, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    private static string? Key => Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") is { Length: > 0 } key
        ? key : Terraria.ModLoader.ModContent.GetInstance<JevariaConfig>().ApiKey;

    public bool Ready => !string.IsNullOrWhiteSpace(Key);

    public async Task<DodgeDecision> DecideAsync(CombatSnapshot snapshot, CancellationToken cancellationToken)
    {
        string? key = Key;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("TYPESAFE_API_KEY is missing");

        JevariaConfig config = Terraria.ModLoader.ModContent.GetInstance<JevariaConfig>();
        bool twins = snapshot.Bosses.Any(boss => boss.Name is "Spazmatism" or "Retinazer");
        var bossNotes = twins
            ? new Dictionary<string, string> { ["Spazmatism"] = string.IsNullOrWhiteSpace(config.Instruction)
                ? JevariaConfig.DefaultInstruction : config.Instruction }
            : new Dictionary<string, string> { [snapshot.Boss.Name] = string.IsNullOrWhiteSpace(config.GeneralInstruction)
                ? JevariaConfig.DefaultGeneralInstruction : config.GeneralInstruction };
        var state = new
        {
            hp_percent = snapshot.Health * 100 / Math.Max(1, snapshot.MaxHealth),
            i_am_losing_health_over_time = snapshot.LosingHealthOverTime,
            my_speed_to_the_right = (int)(snapshot.Player.Velocity.X * 60f / 16f),
            my_speed_upward = (int)(-snapshot.Player.Velocity.Y * 60f / 16f),
            threats = snapshot.Bosses.Concat(snapshot.Parts)
                .GroupBy(entity => entity.Name)
                .Select(group => group.OrderBy(entity => Vector2.DistanceSquared(entity.Center, snapshot.Player.Center)).First())
                .Where(entity => (Math.Abs(entity.Center.X - snapshot.Player.Center.X) +
                    Math.Abs(entity.Center.Y - snapshot.Player.Center.Y)) / 16f <= 200f)
                .Select(entity => BossThreat(entity, snapshot)).ToArray(),
            incoming_projectiles = snapshot.Projectiles
                .Select(entity => (Entity: entity, Frames: FramesUntilContact(entity, snapshot.Player)))
                .Where(item => item.Frames >= 0 || Vector2.DistanceSquared(item.Entity.Center, snapshot.Player.Center) <= 30f * 30f * 16f * 16f)
                .Where(item => item.Frames == 0 || Vector2.DistanceSquared(item.Entity.Center, snapshot.Player.Center) <= 96f * 96f ||
                    (item.Entity.Center.X < snapshot.Player.Center.X && item.Entity.Velocity.X > 0.1f) ||
                    (item.Entity.Center.X > snapshot.Player.Center.X && item.Entity.Velocity.X < -0.1f) ||
                    (item.Entity.Center.Y < snapshot.Player.Center.Y && item.Entity.Velocity.Y > 0.1f) ||
                    (item.Entity.Center.Y > snapshot.Player.Center.Y && item.Entity.Velocity.Y < -0.1f))
                .OrderBy(item => item.Frames < 0 ? int.MaxValue : item.Frames)
                .Take(12)
                .Select(item => ProjectileThreat(item.Entity, snapshot)).ToArray(),
            room = new
            {
                left = Room(snapshot.SolidDistances.Left, snapshot.WorldDistances.Left),
                right = Room(snapshot.SolidDistances.Right, snapshot.WorldDistances.Right),
                above = Room(snapshot.SolidDistances.Up, snapshot.WorldDistances.Up),
                below = Room(snapshot.SolidDistances.Down, snapshot.WorldDistances.Down)
            },
            boss_notes = bossNotes
        };
        var questions = new Dictionary<string, object>
        {
            ["direction"] = Choice("Terraria boss fight. Which way should I move right now so that nothing hits me? Look at every boss part and shot, where each is heading and how soon it reaches me, and how much room I have. Frames until contact assumes our current velocities stay constant; use enemy health to identify its phase. Keep an escape route: do not run into a wall, floor or ceiling, or toward another threat. Follow the boss notes.", DirectionCriteria(snapshot)),
            ["size"] = Choice("Same moment. Choose the movement tool needed for the direction you chose. These choices do not limit travel distance; movement continues until the next answer.", new Dictionary<string, string>
            {
                ["Small"] = "Run, jump, fly or fall in the chosen direction. No dash or grapple. I can change direction on the next answer. Use this when ordinary movement is enough, even for a long retreat.",
                ["Medium"] = "Trigger an extra jump when moving up or up diagonally; diagonal jumps can also move me sideways. There is no dash. Sideways and downward Medium move exactly like Small, so choose Small for those directions. Use Medium only when an extra upward jump is needed to avoid an imminent hit.",
                ["Large"] = "Fire a grappling hook toward the chosen direction. It needs a reachable surface and may fail or be on cooldown. If there is no surface to hook, I fall back to Medium. Use only when I need the hook to cross a large gap or escape a trap."
            })
        };
        string stateJson = JsonSerializer.Serialize(state);
        Terraria.ModLoader.ModContent.GetInstance<Jevaria>().Logger.Info(
            $"jev state #{snapshot.Sequence}: {stateJson}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "jev-latest", state = stateJson, questions
        }, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }), Encoding.UTF8, "application/json");

        var timer = Stopwatch.StartNew();
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false));
        timer.Stop();

        JsonElement answers = body.RootElement.GetProperty("answers");
        string direction = ChoiceValue(answers, "direction");
        string magnitude = ChoiceValue(answers, "size");

        var intent = new DodgeIntent(
            ParseDirection(direction),
            direction == "Stay" ? Magnitude.None : ParseMagnitude(magnitude), false);

        var probabilities = new Dictionary<string, IReadOnlyDictionary<string, float>>();
        foreach (string name in new[] { "direction", "size" })
        {
            var values = new Dictionary<string, float>();
            foreach (JsonProperty value in answers.GetProperty(name).GetProperty("probabilities").EnumerateObject())
                values[value.Name] = value.Value.GetSingle();
            probabilities[name] = values;
        }
        return new DodgeDecision(intent, probabilities, timer.ElapsedMilliseconds, snapshot.Sequence);
    }

    private static object Choice(string instructions, object criteria)
        => new { type = "choice", instructions, criteria };

    private static Dictionary<string, string> DirectionCriteria(CombatSnapshot snapshot)
    {
        bool left = Room(snapshot.SolidDistances.Left, snapshot.WorldDistances.Left) <= GameSensor.EscapeReserveCells;
        bool right = Room(snapshot.SolidDistances.Right, snapshot.WorldDistances.Right) <= GameSensor.EscapeReserveCells;
        bool up = Room(snapshot.SolidDistances.Up, snapshot.WorldDistances.Up) <= GameSensor.EscapeReserveCells;
        bool down = Room(snapshot.SolidDistances.Down, snapshot.WorldDistances.Down) <= GameSensor.EscapeReserveCells;
        var choices = new Dictionary<string, string>();
        foreach (DodgeDirection direction in Enum.GetValues<DodgeDirection>())
        {
            if (direction == DodgeDirection.Stay) { choices["Stay"] = "Stay only if no threat is approaching and I am not near a boundary."; continue; }
            (int x, int y) = Components(direction);
            string horizontal = x < 0 ? "left" : "right";
            string vertical = y < 0 ? "up" : "down";
            string move = x == 0 ? $"Move straight {vertical}." : y == 0 ?
                $"Move straight {horizontal}." : $"Move {vertical} and to the {horizontal}.";
            bool xStop = x < 0 ? left : x > 0 && right;
            bool yStop = y < 0 ? up : y > 0 && down;
            if (xStop || yStop) continue;
            choices[direction.ToString()] = move;
        }
        return choices;
    }

    private static (int X, int Y) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.Up => (0, -1), DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Right => (1, 0), DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1), DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.Left => (-1, 0), DodgeDirection.UpLeft => (-1, -1),
        _ => (0, 0)
    };

    private static DodgeDirection ParseDirection(string value)
        => Enum.TryParse(value, out DodgeDirection direction) ? direction : DodgeDirection.Stay;

    private static int Room(float solid, float world)
        => (int)Math.Clamp(Math.Min(solid, world - GameSensor.WorldEdgeCells * 16f) / 16f, 0f, 60f);

    private static object BossThreat(CombatEntity entity, CombatSnapshot snapshot)
        => new
        {
            id = entity.Name, name = entity.Name,
            health_percent = snapshot.BossMotion.FirstOrDefault(motion => motion.Id == entity.Id) is { MaxHealth: > 0 } motion
                ? motion.Health * 100 / motion.MaxHealth : 100,
            contact_damage_percent_of_my_hp = entity.Damage * 100 / Math.Max(1, snapshot.Health),
            cells_to_my_right = (int)((entity.Center.X - snapshot.Player.Center.X) / 16f),
            cells_above_me = (int)((snapshot.Player.Center.Y - entity.Center.Y) / 16f),
            speed_to_the_right = (int)(entity.Velocity.X * 60f / 16f),
            speed_upward = (int)(-entity.Velocity.Y * 60f / 16f),
            speed_cells_per_second = (int)((Math.Abs(entity.Velocity.X) + Math.Abs(entity.Velocity.Y)) * 60f / 16f),
            top_speed_in_the_last_second = (int)snapshot.BossMotion.FirstOrDefault(motion => motion.Id == entity.Id).BossFastestInTheLastSecond,
            frames_until_it_hits_me = ContactTime(entity, snapshot.Player)
        };

    private static object ProjectileThreat(CombatEntity entity, CombatSnapshot snapshot) => new
    {
        name = entity.Name,
        damage_percent_of_my_hp = entity.Damage * 100 / Math.Max(1, snapshot.Health),
        cells_to_my_right = (int)((entity.Center.X - snapshot.Player.Center.X) / 16f),
        cells_above_me = (int)((snapshot.Player.Center.Y - entity.Center.Y) / 16f),
        speed_to_the_right = (int)(entity.Velocity.X * 60f / 16f),
        speed_upward = (int)(-entity.Velocity.Y * 60f / 16f),
        frames_until_it_hits_me = ContactTime(entity, snapshot.Player)
    };

    private static object ContactTime(CombatEntity entity, CombatEntity player)
    {
        int frames = FramesUntilContact(entity, player);
        return frames >= 0 ? frames : "not heading at me";
    }

    private static int FramesUntilContact(CombatEntity entity, CombatEntity player)
    {
        float dx = entity.Center.X - player.Center.X;
        float dy = entity.Center.Y - player.Center.Y;
        float gapX = Math.Abs(dx) - (entity.Size.X + player.Size.X) * 0.5f;
        float gapY = Math.Abs(dy) - (entity.Size.Y + player.Size.Y) * 0.5f;
        if (gapX <= 0f && gapY <= 0f) return 0;

        float relativeX = entity.Velocity.X - player.Velocity.X;
        float relativeY = entity.Velocity.Y - player.Velocity.Y;
        float closeX = dx > 0f ? -relativeX : relativeX;
        float closeY = dy > 0f ? -relativeY : relativeY;
        float framesX = gapX <= 0f ? 0f : closeX > 0.1f ? gapX / closeX : -1f;
        float framesY = gapY <= 0f ? 0f : closeY > 0.1f ? gapY / closeY : -1f;
        if (framesX < 0f || framesY < 0f) return -1;
        float frames = Math.Max(framesX, framesY);
        return frames > 600f ? -1 : (int)frames;
    }

    private static string ChoiceValue(JsonElement answers, string name)
        => answers.GetProperty(name).GetProperty("choice").GetString()
           ?? throw new FormatException(name);

    private static Magnitude ParseMagnitude(string value) => value switch
    {
        "Small" => Magnitude.Small,
        "Medium" => Magnitude.Medium,
        "Large" => Magnitude.Large,
        _ => throw new FormatException("magnitude")
    };

    public void Dispose() => _client.Dispose();
}
