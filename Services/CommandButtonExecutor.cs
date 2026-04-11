using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Executes CommandButton command sequences via RCON.
/// Manages continuous health-check timers keyed by button ID.
/// </summary>
public class CommandButtonExecutor
{
    private readonly RconService _rcon;
    private readonly LoggingService _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTimers = new();

    public CommandButtonExecutor(RconService rcon, LoggingService logger)
    {
        _rcon = rcon;
        _logger = logger;
    }

    /// <summary>
    /// Executes all commands in the button sequentially.
    /// For Summon buttons, nickname/username are substituted.
    /// For Buff buttons with RunContinuously, starts a repeating timer.
    /// </summary>
    public async Task ExecuteAsync(CommandButton button, string nickname = "", string username = "")
    {
        if (button.ButtonType == CommandButtonType.Buff && button.RunContinuously)
        {
            ToggleContinuous(button, nickname, username);
            return;
        }

        await RunCommandSequenceAsync(button, nickname, username);
    }

    /// <summary>
    /// Runs the command list once, sequentially, with placeholder substitution.
    /// </summary>
    public async Task RunCommandSequenceAsync(CommandButton button, string nickname = "", string username = "")
    {
        if (!_rcon.IsConnected)
        {
            _logger.LogWarning($"RCON not connected. Cannot execute button '{button.Name}'.");
            return;
        }

        _logger.LogInfo($"[Button] Executing '{button.Name}' ({button.Commands.Count} commands)", LogCategory.System);

        foreach (var template in button.Commands)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var cmd = template;
            if (button.UseNickname && button.ButtonType == CommandButtonType.Summon)
            {
                cmd = Core.EventProcessor.BuildCommand(template, nickname, username);
            }

            await _rcon.SendCommand(cmd);
        }
    }

    /// <summary>
    /// Toggles a continuous health-check timer for the given button.
    /// If already running, stops it. Otherwise starts it.
    /// </summary>
    public void ToggleContinuous(CommandButton button, string nickname = "", string username = "")
    {
        if (_runningTimers.TryRemove(button.Id, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
            _logger.LogInfo($"[Button] Stopped continuous '{button.Name}'", LogCategory.System);
            return;
        }

        var cts = new CancellationTokenSource();
        _runningTimers[button.Id] = cts;
        _logger.LogInfo($"[Button] Started continuous '{button.Name}' (every {button.IntervalSeconds}s)", LogCategory.System);

        _ = RunContinuousLoop(button, nickname, username, cts.Token);
    }

    public bool IsRunning(string buttonId) => _runningTimers.ContainsKey(buttonId);

    public void StopAll()
    {
        foreach (var kvp in _runningTimers)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _runningTimers.Clear();
    }

    private async Task RunContinuousLoop(CommandButton button, string nickname, string username, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RunCommandSequenceAsync(button, nickname, username);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, button.IntervalSeconds)), token);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError($"[Button] Continuous loop error for '{button.Name}': {ex.Message}");
        }
        finally
        {
            _runningTimers.TryRemove(button.Id, out _);
        }
    }
}
