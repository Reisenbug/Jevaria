using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace Jevaria.Combat;

public sealed class CombatPlayer : ModPlayer
{
    private readonly ICombatSensor _sensor = new GameSensor();
    private readonly IMovementDriver _movement = new GameMovement();
    private readonly IAttackDriver _attack = new ProjectileAim();
    private CancellationTokenSource? _cancellation;
    private Task<DodgeDecision>? _request;
    private CombatSnapshot? _snapshot;
    private ulong _sequence;
    private ulong _nextRequestTick;
    private long _activeSince;
    private string _lastActionReason = "";

    public bool Enabled { get; private set; }
    public bool ShowHud { get; set; } = true;
    public string Status { get; private set; } = "off";
    public DodgeDecision? Decision { get; private set; }
    public ActionResult LastAction { get; private set; }
    public Vector2? AimTarget { get; private set; }
    public CombatSnapshot? Snapshot => _snapshot;
    public long ActionAgeMs => _activeSince == 0 ? 0 :
        (long)Stopwatch.GetElapsedTime(_activeSince).TotalMilliseconds;

    public void SetEnabled(bool enabled)
    {
        if (enabled && (Main.netMode != NetmodeID.SinglePlayer || !Jevaria.Brain.Ready))
        {
            Status = Main.netMode != NetmodeID.SinglePlayer
                ? "single player only" : "TypeSafe API key missing";
            return;
        }

        Enabled = enabled;
        Status = enabled ? "waiting for boss" : "off";
        if (enabled) return;
        _cancellation?.Cancel();
        _request = null;
        _snapshot = null;
        Decision = null;
        AimTarget = null;
        _activeSince = 0;
        _movement.Release(Player);
        _attack.Release(Player);
    }

    public override void SetControls()
    {
        if (Player.whoAmI != Main.myPlayer || !Enabled || Main.netMode != NetmodeID.SinglePlayer)
            return;

        if (Player.dead || Main.gameMenu || Main.playerInventory)
        {
            _movement.Release(Player);
            _attack.Release(Player);
            Status = "paused";
            return;
        }

        _sensor.Observe(Player);

        if (_request?.IsCompleted == true) ReceiveDecision();

        if (_request == null && Main.GameUpdateCount >= _nextRequestTick)
        {
            _snapshot = _sensor.Capture(Player, ++_sequence,
                Decision?.Intent ?? DodgeIntent.Idle, ActionAgeMs);
            if (_snapshot == null)
            {
                Decision = null;
                _movement.Release(Player);
                _attack.Release(Player);
                Status = "waiting for boss";
                return;
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _request = Jevaria.Brain.DecideAsync(_snapshot, _cancellation.Token);
            Status = Decision == null ? "first decision pending" : "decision pending";
        }

        if (_request != null && _request.IsCompleted == false &&
            _activeSince != 0 && ActionAgeMs > 1500)
        {
            Decision = null;
            Status = "stale action released";
        }

        if (Decision == null || _snapshot == null)
        {
            _movement.Release(Player);
            _attack.Release(Player);
            return;
        }

        LastAction = _movement.Apply(Player, Decision.Intent, _snapshot);
        if (LastAction.Reason != _lastActionReason)
        {
            _lastActionReason = LastAction.Reason;
            if (_lastActionReason.Length > 0)
                Mod.Logger.Info($"action #{Decision.Sequence}: {_lastActionReason}; applied={LastAction.Applied}");
        }
        if (LastAction.Reason == "grapple")
        {
            _attack.Release(Player);
            return;
        }

        NPC boss = Main.npc[_snapshot.Boss.Id];
        if (!boss.active || !boss.boss)
        {
            Decision = null;
            _movement.Release(Player);
            _attack.Release(Player);
            Status = "boss gone";
            return;
        }

        var liveBoss = _snapshot.Boss with
        {
            Center = boss.Center,
            Velocity = boss.velocity
        };
        if (_attack.TryAttack(Player, _snapshot with { Boss = liveBoss }, out Vector2 target))
        {
            AimTarget = target;
        }
        else
        {
            AimTarget = null;
            _attack.Release(Player);
        }
    }

    private void ReceiveDecision()
    {
        Task<DodgeDecision> completed = _request!;
        _request = null;
        if (completed.IsCanceled) return;
        if (completed.IsFaulted)
        {
            Status = completed.Exception?.GetBaseException().Message ?? "Jev request failed";
            Mod.Logger.Warn($"Jev request failed: {Status}");
            Decision = null;
            _nextRequestTick = Main.GameUpdateCount + 60;
            return;
        }

        DodgeDecision answer = completed.Result;
        if (_snapshot == null || answer.Sequence != _snapshot.Sequence || !answer.Intent.Valid)
        {
            Status = "outdated or invalid answer";
            Decision = null;
            return;
        }
        Decision = answer;
        _activeSince = Stopwatch.GetTimestamp();
        Status = "acting";
        Mod.Logger.Info($"decision #{answer.Sequence}: {answer.Intent}; " +
            $"latency={answer.LatencyMs}ms; probabilities={JsonSerializer.Serialize(answer.Probabilities)}");
    }
}

public sealed class JevariaCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "jevaria";
    public override string Usage => "/jevaria on|off|hud";
    public override string Description => "Control Jevaria boss combat and its decision display";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var combat = Main.LocalPlayer.GetModPlayer<CombatPlayer>();
        if (args.Length != 1) throw new UsageException(Usage);
        switch (args[0].ToLowerInvariant())
        {
            case "on": combat.SetEnabled(true); break;
            case "off": combat.SetEnabled(false); break;
            case "hud": combat.ShowHud = !combat.ShowHud; break;
            default: throw new UsageException(Usage);
        }
        Main.NewText($"[Jevaria] {combat.Status}; HUD {(combat.ShowHud ? "on" : "off")}");
    }
}
