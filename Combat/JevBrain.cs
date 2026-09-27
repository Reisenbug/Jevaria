using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terraria.ModLoader.Config;

namespace Jevaria.Combat;

public sealed class JevariaConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    public string ApiKey = "";

    public string Instruction =
        "You control dodging in a Terraria boss fight. Choose movement that avoids imminent " +
        "high-damage contacts and projectiles while preserving room to move. Avoid lingering " +
        "near solid walls or world edges. Use positions and velocities in pixels and pixels " +
        "per game tick. The action lasts until the next answer, usually about 300 ms. " +
        "Small means ordinary movement, medium uses an available movement ability, and large " +
        "may use a grapple or mount. Decide direction and strength for each axis independently. " +
        "Only request a dash when a dash is available and its timing helps avoid a threat.";
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

        string instruction = Terraria.ModLoader.ModContent.GetInstance<JevariaConfig>().Instruction;
        var state = new
        {
            instruction,
            player = snapshot.Player,
            health = snapshot.Health,
            boss = snapshot.Boss,
            parts = snapshot.Parts,
            projectiles = snapshot.Projectiles,
            solid_distances = snapshot.SolidDistances,
            world_distances = snapshot.WorldDistances,
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
            ["horizontal"] = Choice("Which horizontal direction should the player move now?", new
            {
                left = "Move left to avoid danger and keep room for the next action",
                none = "No horizontal movement is useful now",
                right = "Move right to avoid danger and keep room for the next action"
            }),
            ["vertical"] = Choice("Which vertical direction should the player move now?", new
            {
                up = "Move upward to avoid danger and keep room for the next action",
                none = "No vertical movement is useful now",
                down = "Move downward to avoid danger and keep room for the next action"
            }),
            ["horizontal_magnitude"] = Choice("If moving horizontally, how much movement is needed?", new
            {
                small = "Ordinary left or right movement is sufficient",
                medium = "Use an available extra jump while moving sideways",
                large = "Use an available grapple for a larger sideways move"
            }),
            ["vertical_magnitude"] = Choice("If moving vertically, how much movement is needed?", new
            {
                small = "Ordinary jump, flight, or dropping one platform is sufficient",
                medium = "Use an available grapple or sustained downward movement",
                large = "Use an available mount as well as the vertical action"
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
        }, new JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8, "application/json");

        var timer = Stopwatch.StartNew();
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false));
        timer.Stop();

        JsonElement answers = body.RootElement.GetProperty("answers");
        string horizontal = ChoiceValue(answers, "horizontal");
        string vertical = ChoiceValue(answers, "vertical");
        string hMagnitude = ChoiceValue(answers, "horizontal_magnitude");
        string vMagnitude = ChoiceValue(answers, "vertical_magnitude");
        bool dash = answers.GetProperty("dash").GetProperty("noul").GetSingle() >= 0.5f;

        var intent = new DodgeIntent(
            horizontal switch { "left" => Direction.Negative, "right" => Direction.Positive, "none" => Direction.None, _ => throw new FormatException("horizontal") },
            vertical switch { "up" => Direction.Negative, "down" => Direction.Positive, "none" => Direction.None, _ => throw new FormatException("vertical") },
            horizontal == "none" ? Magnitude.None : ParseMagnitude(hMagnitude),
            vertical == "none" ? Magnitude.None : ParseMagnitude(vMagnitude), dash);

        var probabilities = new Dictionary<string, IReadOnlyDictionary<string, float>>();
        foreach (string name in new[] { "horizontal", "vertical", "horizontal_magnitude", "vertical_magnitude" })
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
