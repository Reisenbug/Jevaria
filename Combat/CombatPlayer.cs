using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
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
    private DodgeIntent _lastCompletedIntent = DodgeIntent.Idle;
    private long _lastCompletedDurationMs;

    public bool Enabled { get; private set; } = true;
    public bool DodgeEnabled { get; private set; } = true;
    public bool ShowHud { get; set; } = true;
    public string Status { get; private set; } = "waiting for boss";
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
        if (enabled) DodgeEnabled = true;
        Status = enabled ? "waiting for boss" : "off";
        Mod.Logger.Info($"combat {(enabled ? "enabled" : "disabled")}: tick={Main.GameUpdateCount}; health={Player.statLife}");
        if (enabled) return;
        _cancellation?.Cancel();
        _request = null;
        _snapshot = null;
        Decision = null;
        AimTarget = null;
        _activeSince = 0;
        _lastCompletedIntent = DodgeIntent.Idle;
        _lastCompletedDurationMs = 0;
        _movement.Release(Player);
        _attack.Release(Player);
        _attack.RestoreWeapon(Player);
    }

    public void SetDodgeEnabled(bool enabled)
    {
        DodgeEnabled = enabled;
        _cancellation?.Cancel();
        _request = null;
        Decision = null;
        _activeSince = 0;
        _lastCompletedIntent = DodgeIntent.Idle;
        _lastCompletedDurationMs = 0;
        _nextRequestTick = Main.GameUpdateCount;
        _lastActionReason = "";
        _movement.Release(Player);
        Status = enabled ? "waiting for boss" : "dodge off";
        Mod.Logger.Info($"dodge {(enabled ? "enabled" : "disabled")}: tick={Main.GameUpdateCount}");
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!Jevaria.DodgeToggle.JustPressed) return;
        if (!Enabled) SetEnabled(true);
        else SetDodgeEnabled(!DodgeEnabled);
        Main.NewText($"[Jevaria] dodge {(Enabled && DodgeEnabled ? "on" : "off")}");
    }

    public override void SetControls()
    {
        if (Player.whoAmI != Main.myPlayer || !Enabled || Main.netMode != NetmodeID.SinglePlayer)
            return;

        if (!Jevaria.Brain.Ready)
        {
            _movement.Release(Player);
            _attack.Release(Player);
            Status = "TypeSafe API key missing";
            return;
        }

        if (Player.dead || Main.gameMenu || Main.playerInventory)
        {
            _movement.Release(Player);
            _attack.Release(Player);
            Status = "paused";
            return;
        }

        bool manualMovementInput = Player.controlLeft || Player.controlRight || Player.controlUp ||
            Player.controlDown || Player.controlJump || Player.controlHook || Player.controlMount;

        bool bossPresent = _sensor.Observe(Player);
        if (bossPresent)
        {
            int? slot = _attack.SelectFirstWeapon(Player);
            if (slot is { } selected)
                Mod.Logger.Info($"weapon selected: slot={selected + 1}; item={Player.HeldItem.Name}");
        }
        else _attack.RestoreWeapon(Player);

        if (DodgeEnabled && _request?.IsCompleted == true) ReceiveDecision();

        if (DodgeEnabled && _request == null && Main.GameUpdateCount >= _nextRequestTick)
        {
            _snapshot = _sensor.Capture(Player, ++_sequence,
                _lastCompletedIntent, _lastCompletedDurationMs);
            if (_snapshot == null)
            {
                if (Decision != null)
                    Mod.Logger.Info($"combat ended: tick={Main.GameUpdateCount}; health={Player.statLife}");
                Decision = null;
                _lastCompletedIntent = DodgeIntent.Idle;
                _lastCompletedDurationMs = 0;
                _movement.Release(Player);
                _attack.Release(Player);
                Status = "waiting for boss";
                return;
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            Mod.Logger.Info($"state #{_snapshot.Sequence}: {JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions { IncludeFields = true })}");
            _request = Jevaria.Brain.DecideAsync(_snapshot, _cancellation.Token);
            Status = Decision == null ? "first decision pending" : "decision pending";
        }

        if (DodgeEnabled && _request != null && _request.IsCompleted == false &&
            _activeSince != 0 && ActionAgeMs > 1500)
        {
            Decision = null;
            Status = "stale action released";
        }

        if (!DodgeEnabled && Main.GameUpdateCount >= _nextRequestTick)
        {
            _snapshot = _sensor.Capture(Player, ++_sequence, DodgeIntent.Idle, 0);
            _nextRequestTick = Main.GameUpdateCount + 15;
            Status = _snapshot == null ? "waiting for boss" : "dodge off";
        }

        if (_snapshot == null)
        {
            _movement.Release(Player);
            _attack.Release(Player);
            AimTarget = null;
            return;
        }

        if (DodgeEnabled && Decision != null && !manualMovementInput)
        {
            LastAction = _movement.Apply(Player, Decision!.Intent, _snapshot);
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
        }
        else
        {
            _movement.Release(Player);
            LastAction = new ActionResult(DodgeIntent.Idle, DodgeIntent.Idle, "");
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
        if (manualMovementInput)
        {
            AimTarget = null;
            return;
        }
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
            Mod.Logger.Warn($"Jev request #{_snapshot?.Sequence} failed: {Status}");
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
        long previousAgeMs = ActionAgeMs;
        _lastCompletedIntent = Decision is not null && LastAction.Applied.Valid
            ? LastAction.Applied : DodgeIntent.Idle;
        _lastCompletedDurationMs = Decision is null ? 0 : previousAgeMs;
        Decision = answer;
        _activeSince = Stopwatch.GetTimestamp();
        Status = "acting";
        Mod.Logger.Info($"decision #{answer.Sequence}: {answer.Intent}; " +
            $"latency={answer.LatencyMs}ms; previous_action={previousAgeMs}ms; probabilities={JsonSerializer.Serialize(answer.Probabilities)}");
        string direction = answer.Intent.Direction switch
        {
            DodgeDirection.Up => "↑",
            DodgeDirection.UpRight => "↗",
            DodgeDirection.Right => "→",
            DodgeDirection.DownRight => "↘",
            DodgeDirection.Down => "↓",
            DodgeDirection.DownLeft => "↙",
            DodgeDirection.Left => "←",
            DodgeDirection.UpLeft => "↖",
            _ => "·"
        };
        Main.NewText($"[Jev #{answer.Sequence}] {direction} {answer.Intent.Size}" +
            (answer.Intent.Dash ? " dash" : "") + $" {answer.LatencyMs}ms", Color.Orange);
    }

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Enabled && _snapshot != null)
            Mod.Logger.Info($"hurt: tick={Main.GameUpdateCount}; decision=#{Decision?.Sequence}; damage={info.Damage}; health={Player.statLife}; position={Player.Center}");
    }

    public override void OnHitNPCWithProj(Projectile proj, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Enabled && _snapshot != null && target.boss)
            Mod.Logger.Info($"hit: tick={Main.GameUpdateCount}; decision=#{Decision?.Sequence}; target={target.FullName}; damage={damageDone}; target_health={target.life}; projectile={proj.Name}");
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
