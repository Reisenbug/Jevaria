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

    public string GeneralInstruction =
        "Fight the active boss. Avoid dangerous body contact and hostile projectiles, " +
        "considering every boss in `bosses` and the linked `parts`. Keep room to dodge " +
        "and do not linger near solid or world boundaries. If solid_distances.up " +
        "is under 64 pixels, climbing has little room: descend when the route is safe " +
        "instead of repeatedly pushing into the ceiling. platform_distance_below " +
        "at or below 8 pixels means a one-way platform is directly underfoot; moving " +
        "down passes through it. Vary height in both directions when evading; do not " +
        "treat vertical movement as only climbing. Positions are pixels and " +
        "velocities are pixels per game tick. `frames_until_player_contact` estimates " +
        "time to collision if current velocities continue; -1 means no predicted collision. " +
        "Incoming projectiles are ordered by predicted contact time. Each action lasts until the next Jev answer, " +
        "usually about 300 ms. Choose one joint direction for the whole route, then " +
        "choose one movement size for that route. Request a dash only if available and useful now.";

    public string Instruction =
        "The Twins are two independently flying eyes. Focus damage " +
        "on Spazmatism, the green fire eye, while it is alive. Dodge both surviving eyes. " +
        "Avoid body contact first. " +
        "In phase one Spazmatism retreats when approached and follows when the player retreats, " +
        "so chasing it horizontally does not reliably control distance and can run into its " +
        "shots. Within 30 tiles (480 pixels) of Spazmatism is dangerous: its fire can reach " +
        "the player and there may be too little time to react to a charge. Stay farther away " +
        "when possible. Spazmatism keeps pursuing outside its charges, so keep changing height " +
        "by both climbing and descending when there is room; do not stay near the top of the arena or repeatedly " +
        "choose up. When a platform is directly underfoot, a small down move drops below it; " +
        "use this when the space below is safer, then climb again only when useful. " +
        "A charge targets the player's position at its start; change " +
        "horizontal or vertical direction to make it miss, using both axes when needed. " +
        "At half health Spazmatism enters phase two, cycling between a sustained stream of " +
        "fire and six charges. Fire is continuous and repeated exposure hurts every tick; " +
        "move out immediately when it starts hitting. Closer exposure is worse. Keep distance " +
        "from phase-two Spazmatism even between fire attacks. Its charge contact is more " +
        "dangerous than the fire. For a charge, change direction as it approaches, then use " +
        "the slower interval to restore distance and attack. In `boss_motion`, compare " +
        "`boss_speed_cells_per_second` with `boss_fastest_in_the_last_second` for Spazmatism: " +
        "a high current speed near its recent maximum can indicate a charge; a drop can " +
        "indicate that charge has ended. Also consider the absolute speed, because a slow " +
        "recent maximum alone is not a charge. Spazmatism's projectiles can add debuffs, so " +
        "their cost exceeds their listed damage. Retinazer's main danger is collision, not " +
        "its lasers; do not make a dangerous move just to avoid a weak laser. Both bodies " +
        "can collide with the player. `bosses` contains the surviving eyes, while `boss` is " +
        "the attack target. Avoid fleeing from one eye into the other; seek a direction clear " +
        "of both. When caught between them, move away from Spazmatism first. The first test " +
        "has wings, an extra jump, and a grapple, but no mount.";
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
        string instruction = config.GeneralInstruction;
        foreach (CombatEntity boss in snapshot.Bosses)
        {
            if (boss.Name is not ("Spazmatism" or "Retinazer")) continue;
            instruction += " " + config.Instruction;
            break;
        }
        var state = new
        {
            instruction,
            player = snapshot.Player,
            health = snapshot.Health,
            boss = Threat(snapshot.Boss, snapshot),
            bosses = snapshot.Bosses.Select(boss => Threat(boss, snapshot)).ToArray(),
            boss_motion = snapshot.BossMotion,
            parts = snapshot.Parts.Select(part => Threat(part, snapshot)).ToArray(),
            projectiles = snapshot.Projectiles
                .Select(projectile => (Entity: projectile, Frames: FramesUntilContact(projectile, snapshot.Player)))
                .Where(item => item.Frames >= 0 ||
                    Vector2.DistanceSquared(item.Entity.Center, snapshot.Player.Center) <= 96f * 96f)
                .OrderBy(item => item.Frames < 0 ? int.MaxValue : item.Frames)
                .Take(12)
                .Select(item => Threat(item.Entity, snapshot)).ToArray(),
            solid_distances = snapshot.SolidDistances,
            world_distances = snapshot.WorldDistances,
            platform_distance_below = snapshot.PlatformDistanceBelow,
            abilities = new
            {
                snapshot.CanDash, snapshot.CanDoubleJump, snapshot.CanFly,
                snapshot.HasHook, snapshot.HasMount
            },
            previous = snapshot.PreviousIntent,
            previous_duration_ms = snapshot.PreviousDurationMs
        };
        var questions = new Dictionary<string, object>
        {
            ["direction"] = Choice("Choose one direction for the complete dodge route. Consider both eyes, projectiles, and the available room together.", DirectionCriteria(snapshot)),
            ["magnitude"] = Choice("How much movement is needed to evade the current threats?", new Dictionary<string, string>
            {
                ["small"] = "Ordinary movement, wings, or a short platform drop is enough",
                ["medium"] = "Use an extra jump for an upward route; hold down for the full action for a downward route; sideways movement remains ordinary",
                ["large"] = "A grappling hook along the route is useful and a suitable surface is reachable"
            }),
            ["dash"] = new
            {
                type = "noul",
                instructions = "Should the player dash horizontally on this action to dodge a threat?",
                criteria = new
                {
                    @true = "A dash is available, useful now, and safer than normal movement",
                    @false = "A dash is unavailable, unnecessary, or unsafe"
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "jev-latest", state, questions
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
        string magnitude = ChoiceValue(answers, "magnitude");
        bool dash = answers.GetProperty("dash").GetProperty("noul").GetSingle() >= 0.5f;

        var intent = new DodgeIntent(
            ParseDirection(direction),
            direction == "stay" ? Magnitude.None : ParseMagnitude(magnitude), dash);

        var probabilities = new Dictionary<string, IReadOnlyDictionary<string, float>>();
        foreach (string name in new[] { "direction", "magnitude" })
        {
            var values = new Dictionary<string, float>();
            foreach (JsonProperty value in answers.GetProperty(name).GetProperty("probabilities").EnumerateObject())
                values[value.Name] = value.Value.GetSingle();
            probabilities[name] = values;
        }
        probabilities["dash"] = new Dictionary<string, float>
        {
            ["yes"] = answers.GetProperty("dash").GetProperty("noul").GetSingle(),
            ["no"] = 1f - answers.GetProperty("dash").GetProperty("noul").GetSingle()
        };
        return new DodgeDecision(intent, probabilities, timer.ElapsedMilliseconds, snapshot.Sequence);
    }

    private static object Choice(string instructions, object criteria)
        => new { type = "choice", instructions, criteria };

    private static Dictionary<string, string> DirectionCriteria(CombatSnapshot snapshot)
    {
        bool left = snapshot.WorldDistances.Left < 80f || snapshot.SolidDistances.Left < 32f;
        bool right = snapshot.WorldDistances.Right < 80f || snapshot.SolidDistances.Right < 32f;
        bool up = snapshot.WorldDistances.Up < 64f || snapshot.SolidDistances.Up < 24f;
        bool down = snapshot.WorldDistances.Down < 64f || snapshot.SolidDistances.Down < 24f;
        string upText = up ? "up is blocked by a ceiling or world edge" :
            snapshot.SolidDistances.Up < 64f ? "little room remains above" : "upward movement is available";
        string downText = down ? "down is blocked by solid ground or the world edge" :
            snapshot.PlatformDistanceBelow <= 8f ? "down passes through the platform directly underfoot" : "down passes through one-way platforms";
        string leftText = left ? "left is blocked by a wall or world edge" : "leftward movement is available";
        string rightText = right ? "right is blocked by a wall or world edge" : "rightward movement is available";
        return new Dictionary<string, string>
        {
            ["up"] = $"Move up to avoid threats; {upText}.",
            ["up_right"] = $"Move up and right to avoid threats; {upText}, and {rightText}.",
            ["right"] = $"Move right to avoid threats; {rightText}.",
            ["down_right"] = $"Move down and right to avoid threats; {downText}, and {rightText}.",
            ["down"] = $"Move down to avoid threats; {downText}.",
            ["down_left"] = $"Move down and left to avoid threats; {downText}, and {leftText}.",
            ["left"] = $"Move left to avoid threats; {leftText}.",
            ["up_left"] = $"Move up and left to avoid threats; {upText}, and {leftText}.",
            ["stay"] = "Stay where you are only if moving in every direction is more dangerous."
        };
    }

    private static DodgeDirection ParseDirection(string value) => value switch
    {
        "up" => DodgeDirection.Up,
        "up_right" => DodgeDirection.UpRight,
        "right" => DodgeDirection.Right,
        "down_right" => DodgeDirection.DownRight,
        "down" => DodgeDirection.Down,
        "down_left" => DodgeDirection.DownLeft,
        "left" => DodgeDirection.Left,
        "up_left" => DodgeDirection.UpLeft,
        "stay" => DodgeDirection.Stay,
        _ => throw new FormatException("direction")
    };

    private static object Threat(CombatEntity entity, CombatSnapshot snapshot)
        => new
        {
            entity.Id,
            entity.Name,
            entity.Center,
            entity.Velocity,
            entity.Size,
            entity.Damage,
            cells_right_of_player = (entity.Center.X - snapshot.Player.Center.X) / 16f,
            cells_above_player = (snapshot.Player.Center.Y - entity.Center.Y) / 16f,
            speed_to_the_right_cells_per_second = entity.Velocity.X * 60f / 16f,
            speed_upward_cells_per_second = -entity.Velocity.Y * 60f / 16f,
            damage_percent_of_current_health = entity.Damage * 100f / Math.Max(1, snapshot.Health),
            frames_until_player_contact = FramesUntilContact(entity, snapshot.Player)
        };

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
        "small" => Magnitude.Small,
        "medium" => Magnitude.Medium,
        "large" => Magnitude.Large,
        _ => throw new FormatException("magnitude")
    };

    public void Dispose() => _client.Dispose();
}
