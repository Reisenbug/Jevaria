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
        "Do not run horizontally toward it. " +
        "Spazmatism enters phase two at 40 percent health. In phase two, keep trending " +
        "toward more than 35 cells of separation; if closer, quickly move away. " +
        "Leave its continuous fire stream immediately. " +
        "It then charges six times, aiming at my position when each charge starts. " +
        "Change both horizontal and vertical direction to dodge each fast charge; " +
        "do not keep running straight while it catches me. " +
        "Leave walls, the floor, the ceiling, and world edges early. " +
        "Never let Spazmatism pin me against a boundary; gain vertical room before crossing past it. " +
        "Never run straight toward Spazmatism.";

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
        var options = snapshot.AvailableActions.ToDictionary(option => ActionName(option.Intent));
        var directions = snapshot.AvailableActions.GroupBy(option => option.Intent.Direction)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (twins)
        {
            var routes = directions.Where(group => Components(group.Key).Y != 0 &&
                (snapshot.Leg.Sign == 0 || Components(group.Key).Y == snapshot.Leg.Sign ||
                 snapshot.Leg.CanReverse)).ToDictionary(group => group.Key, group => group.Value);
            if (routes.Count > 0) directions = routes;
        }
        var twinRoutes = new Dictionary<string, DodgeIntent>();
        if (twins)
            foreach (var (routeDirection, candidates) in directions)
            {
                (int x, int y) = Components(routeDirection);
                twinRoutes[routeDirection.ToString()] = new DodgeIntent(routeDirection,
                    x == 0 ? Magnitude.None : Magnitude.Small,
                    y == 0 ? Magnitude.None : Magnitude.Small, false);
                if (candidates.Any(candidate => candidate.Intent.VerticalSize == Magnitude.Large))
                    twinRoutes[$"{routeDirection}Burst"] = new DodgeIntent(routeDirection,
                        x == 0 ? Magnitude.None : Magnitude.Small, Magnitude.Large, false);
            }
        var state = new
        {
            hp_percent = snapshot.Health * 100 / Math.Max(1, snapshot.MaxHealth),
            i_am_losing_health_over_time = snapshot.LosingHealthOverTime,
            my_speed_to_the_right = (int)(snapshot.Player.Velocity.X * 60f / 16f),
            my_speed_upward = (int)(-snapshot.Player.Velocity.Y * 60f / 16f),
            movement = new
            {
                can_double_jump = snapshot.CanDoubleJump,
                can_fly = snapshot.CanFly,
                has_hook = snapshot.HasHook,
                has_slimy_saddle = snapshot.HasMount
            },
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
            vertical_leg = twins ? new
            {
                direction = snapshot.Leg.Sign < 0 ? "up" : snapshot.Leg.Sign > 0 ? "down" : "uncommitted",
                progress_cells = (int)snapshot.Leg.ProgressCells,
                remaining_cells = (int)snapshot.Leg.RemainingCells,
                can_reverse_for_threat = snapshot.Leg.CanReverse
            } : null,
            boss_notes = bossNotes
        };
        var questions = new Dictionary<string, object>
        {
            ["direction"] = Choice(twins
                ? "Choose a broad escape corridor from boss bodies and projectile lanes, with room to keep moving afterward. Favor a substantial vertical sweep over tiny repeated reversals; do not thread narrow gaps or automatically return toward the ceiling. Route descriptions give rough separation trends and projectile exposure, not exact collision predictions. Burst amplifies existing upward speed with Slimy Saddle, while downward Burst mounts immediately. Use the stronger move to clear danger quickly. Spazmatism enters phase two at 40 percent health: keep trending beyond 35 cells away and rapidly increase separation when closer."
                : "Choose the safest movement direction for the next reaction interval. Compare every boss body and projectile, their motion and damage, and room near solid and world boundaries. Frames until contact assumes current velocities remain constant. Preserve an escape route and vary height when a pursuer would catch sustained horizontal running. Follow the boss notes. All listed directions have at least one available action.",
                twins ? twinRoutes.ToDictionary(route => route.Key,
                    route => RouteDescription(route.Value.Direction, snapshot,
                        route.Value.VerticalSize == Magnitude.Large)) :
                    directions.ToDictionary(group => group.Key.ToString(), group => DirectionDescription(group.Key)))
        };
        foreach (var (route, candidates) in twins ? new Dictionary<DodgeDirection, DodgeOption[]>() : directions)
        {
            if (candidates.Length == 1) continue;
            questions[$"tool_{route}"] = Choice(
                $"Assume I move {route} for the next reaction interval. Which available movement tool is needed in that direction? Choose ordinary movement if it avoids the threat. A hook briefly latches, then jumps free after one or two frames. Slimy Saddle gives a strong vertical move but weak horizontal steering and can carry me into a boundary; use it only when ordinary movement or a hook cannot create enough safe separation.",
                candidates.ToDictionary(option => ActionName(option.Intent), ActionDescription));
        }
        string stateJson = JsonSerializer.Serialize(state);
        Terraria.ModLoader.ModContent.GetInstance<Jevaria>().Logger.Info(
            $"jev state #{snapshot.Sequence}: {stateJson}");
        Terraria.ModLoader.ModContent.GetInstance<Jevaria>().Logger.Info(
            $"action candidates #{snapshot.Sequence}: {string.Join(",", options.Keys)}");

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
        string chosenDirection = ChoiceValue(answers, "direction");
        if (twins)
        {
            if (!twinRoutes.TryGetValue(chosenDirection, out DodgeIntent intent))
                throw new FormatException($"unknown route: {chosenDirection}");
            var routeProbabilities = new Dictionary<string, float>();
            foreach (JsonProperty value in answers.GetProperty("direction").GetProperty("probabilities").EnumerateObject())
                routeProbabilities[value.Name] = value.Value.GetSingle();
            return new DodgeDecision(intent,
                new Dictionary<string, IReadOnlyDictionary<string, float>> { ["direction"] = routeProbabilities },
                timer.ElapsedMilliseconds, snapshot.Sequence);
        }
        if (!Enum.TryParse(chosenDirection, out DodgeDirection direction) ||
            !directions.TryGetValue(direction, out DodgeOption[]? actions))
            throw new FormatException($"unknown direction: {chosenDirection}");
        string chosenAction = actions.Length == 1 ? ActionName(actions[0].Intent) :
            ChoiceValue(answers, $"tool_{direction}");
        if (!options.TryGetValue(chosenAction, out DodgeOption chosen))
            throw new FormatException($"unknown action: {chosenAction}");

        var probabilities = new Dictionary<string, IReadOnlyDictionary<string, float>>();
        var values = new Dictionary<string, float>();
        if (actions.Length == 1) values[chosenAction] = 1f;
        else foreach (JsonProperty value in answers.GetProperty($"tool_{direction}")
            .GetProperty("probabilities").EnumerateObject())
            values[value.Name] = value.Value.GetSingle();
        var horizontalProbabilities = new Dictionary<string, float>();
        var verticalProbabilities = new Dictionary<string, float>();
        var toolProbabilities = new Dictionary<string, float>();
        foreach (var (name, probability) in values)
        {
            if (!options.TryGetValue(name, out DodgeOption option)) continue;
            (int x, int y) = Components(option.Intent.Direction);
            string horizontal = x == 0 ? "None" : $"{(x < 0 ? "Left" : "Right")}{option.Intent.HorizontalSize}";
            string vertical = y == 0 ? "None" : $"{(y < 0 ? "Up" : "Down")}{option.Intent.VerticalSize}";
            bool mount = option.Intent.VerticalSize == Magnitude.Large;
            string tool = mount && option.HookDistanceCells is not null ? "HookAndMount" :
                mount ? "Mount" : option.HookDistanceCells is not null ? "Hook" : "Ordinary";
            AddProbability(horizontalProbabilities, horizontal, probability);
            AddProbability(verticalProbabilities, vertical, probability);
            AddProbability(toolProbabilities, tool, probability);
        }
        var directionProbabilities = new Dictionary<string, float>();
        foreach (JsonProperty value in answers.GetProperty("direction").GetProperty("probabilities").EnumerateObject())
            directionProbabilities[value.Name] = value.Value.GetSingle();
        probabilities["direction"] = directionProbabilities;
        probabilities["action"] = values;
        probabilities["horizontal"] = horizontalProbabilities;
        probabilities["vertical"] = verticalProbabilities;
        probabilities["tool"] = toolProbabilities;
        return new DodgeDecision(chosen.Intent, probabilities, timer.ElapsedMilliseconds, snapshot.Sequence);
    }

    private static void AddProbability(Dictionary<string, float> values, string key, float probability)
        => values[key] = values.GetValueOrDefault(key) + probability;

    private static object Choice(string instructions, object criteria)
        => new { type = "choice", instructions, criteria };

    private static string ActionName(DodgeIntent intent)
    {
        if (intent.Direction == DodgeDirection.Stay) return "Stay";
        (int x, int y) = Components(intent.Direction);
        string horizontal = x == 0 ? "" : $"{(x < 0 ? "Left" : "Right")}{intent.HorizontalSize}";
        string vertical = y == 0 ? "" : $"{(y < 0 ? "Up" : "Down")}{intent.VerticalSize}";
        return horizontal + vertical;
    }

    private static string DirectionDescription(DodgeDirection direction)
    {
        if (direction == DodgeDirection.Stay)
            return "Stay only if no approaching threat requires movement and I have room to escape later.";
        (int x, int y) = Components(direction);
        string horizontal = x == 0 ? "" : x < 0 ? "left" : "right";
        string vertical = y == 0 ? "" : y < 0 ? "up" : "down";
        return x == 0 ? $"Move {vertical}." : y == 0 ? $"Move {horizontal}." :
            $"Move {vertical} and {horizontal}.";
    }

    private static string RouteDescription(DodgeDirection direction, CombatSnapshot snapshot, bool burst)
    {
        (int x, int y) = Components(direction);
        float horizontal = x == 0 ? 0f : 12f * x;
        float verticalRoom = y < 0
            ? Room(snapshot.SolidDistances.Up, snapshot.WorldDistances.Up) - GameMovement.UpperReserveCells
            : Room(snapshot.SolidDistances.Down, snapshot.WorldDistances.Down) - GameMovement.LowerReserveCells;
        float travelEstimate = burst ? (y < 0 ? 18f : 22f) : (y < 0 ? 10f : 18f);
        float vertical = y * Math.Max(0f, Math.Min(travelEstimate, verticalRoom));
        Vector2 displacement = new(horizontal * 16f, vertical * 16f);
        string bossClearance = string.Join("; ", snapshot.Bosses
            .Where(boss => Vector2.Distance(boss.Center, snapshot.Player.Center) < 70f * 16f)
            .Select(boss =>
            {
                float current = Vector2.Distance(boss.Center, snapshot.Player.Center);
                float after = Vector2.Distance(boss.Center, snapshot.Player.Center + displacement);
                return $"{boss.Name}: {(after - current > 5f * 16f ? "opens space" :
                    after - current < -5f * 16f ? "closes space" : "little separation change")}";
            }));
        int exposed = 0;
        foreach (CombatEntity projectile in snapshot.Projectiles)
        {
            Vector2 relative = projectile.Center - snapshot.Player.Center;
            Vector2 relativeStep = projectile.Velocity - displacement / 36f;
            float speedSquared = relativeStep.LengthSquared();
            float frame = speedSquared < 0.01f ? 36f :
                Math.Clamp(-Vector2.Dot(relative, relativeStep) / speedSquared, 4f, 36f);
            if ((relative + relativeStep * frame).LengthSquared() < 10f * 16f * 10f * 16f)
                exposed++;
        }
        return $"{DirectionDescription(direction)} Approximate displacement over 0.6 seconds: " +
            $"{horizontal:0} cells right, {vertical:0} cells down. " +
            (burst ? "Mount for faster vertical travel while holding W or S. " :
                "Ordinary vertical travel. ") +
            $"Boss clearance trend: {(bossClearance.Length == 0 ? "no nearby boss" : bossClearance)}. " +
            $"Projectile corridor: {(exposed == 0 ? "clear" : exposed < 3 ? "exposed" : "crowded")}. " +
            (snapshot.Leg.Sign != 0 && y != snapshot.Leg.Sign
                ? "This reverses the current vertical leg. "
                : "This continues the current vertical leg. ") +
            $"Available vertical room before reserve: {Math.Max(0f, verticalRoom):0} cells.";
    }

    private static string ActionDescription(DodgeOption option)
    {
        DodgeIntent intent = option.Intent;
        if (intent.Direction == DodgeDirection.Stay)
            return "Stay only if no approaching threat requires movement and I have room to escape later.";
        (int x, int y) = Components(intent.Direction);
        string horizontal = x == 0 ? "" : $"Move {(x < 0 ? "left" : "right")}. ";
        string hook = option.HookDistanceCells is float distance
            ? $"Grapple toward a surface {distance:0.#} cells away, then jump free one or two frames after latching. " : "";
        string vertical = y switch
        {
            < 0 when intent.VerticalSize == Magnitude.Large =>
                "Jump while holding W and mount Slimy Saddle during the rise. It has poor horizontal steering and may hit a ceiling; dismount at the apex. ",
            < 0 => "Jump or use an extra jump while holding W. ",
            > 0 when intent.VerticalSize == Magnitude.Large =>
                "Mount Slimy Saddle and hold S for a fast descent. It has poor horizontal steering; dismount when the next action begins. ",
            > 0 => "Hold S to descend or pass through a platform. ",
            _ => ""
        };
        return horizontal + hook + vertical;
    }

    private static (int X, int Y) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.Up => (0, -1), DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Right => (1, 0), DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1), DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.Left => (-1, 0), DodgeDirection.UpLeft => (-1, -1),
        _ => (0, 0)
    };

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

    public void Dispose() => _client.Dispose();
}
