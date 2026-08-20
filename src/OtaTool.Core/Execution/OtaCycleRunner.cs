using OtaTool.Core.Models;

namespace OtaTool.Core.Execution;

public enum OtaCycleIntervalMode
{
    Fixed,
    Random,
}

public sealed record OtaCycleIntervalOptions(
    OtaCycleIntervalMode Mode,
    int FixedSeconds = 0,
    int RandomMinimumSeconds = 0,
    int RandomMaximumSeconds = 0)
{
    public string? Validate()
    {
        if (FixedSeconds < 0 || RandomMinimumSeconds < 0 || RandomMaximumSeconds < 0)
        {
            return "循环升级间隔不能小于 0 秒。";
        }
        if (Mode == OtaCycleIntervalMode.Random && RandomMinimumSeconds > RandomMaximumSeconds)
        {
            return "随机间隔的最小秒数不能大于最大秒数。";
        }
        return null;
    }

    public int NextDelaySeconds(Random random) => Mode == OtaCycleIntervalMode.Fixed
        ? FixedSeconds
        : RandomMinimumSeconds == RandomMaximumSeconds
            ? RandomMinimumSeconds
            : random.Next(RandomMinimumSeconds, RandomMaximumSeconds + 1);
}

public sealed record OtaCycleDefinition(
    OtaTask ForwardTask,
    OtaTask ReverseTask,
    int Rounds,
    OtaCycleIntervalOptions? Interval = null);

public sealed record OtaCycleUpdate(int Round, bool IsForward, OtaTaskResult Result, DateTimeOffset OccurredAt);

public sealed record OtaCycleStepUpdate(int Round, bool IsForward, DateTimeOffset OccurredAt);

public sealed record OtaCycleWaitUpdate(int NextRound, bool NextIsForward, int DelaySeconds, DateTimeOffset OccurredAt);

public interface IOtaTaskLauncher
{
    Task<OtaTaskResult> StartAndWaitAsync(OtaTask task, CancellationToken cancellationToken);
}

public sealed class OtaCycleRunner
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Random _random;

    public OtaCycleRunner(
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Random? random = null)
    {
        _delayAsync = delayAsync ?? Task.Delay;
        _random = random ?? Random.Shared;
    }

    public event EventHandler<OtaCycleUpdate>? Updated;

    public event EventHandler<OtaCycleStepUpdate>? StepStarting;

    public event EventHandler<OtaCycleWaitUpdate>? Waiting;

    public async Task<OtaTaskResult> RunAsync(OtaCycleDefinition definition, IOtaTaskLauncher launcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(launcher);
        if (definition.Rounds <= 0) return new OtaTaskResult(OtaTaskState.Failed, "循环轮数必须大于 0。", DateTimeOffset.Now);
        if (!AreOppositeVersions(definition.ForwardTask, definition.ReverseTask)) return new OtaTaskResult(OtaTaskState.Failed, "循环升级的两个方向必须为 V1→V2 和 V2→V1。", DateTimeOffset.Now);
        var intervalError = definition.Interval?.Validate();
        if (intervalError is not null) return new OtaTaskResult(OtaTaskState.Failed, intervalError, DateTimeOffset.Now);

        for (var round = 1; round <= definition.Rounds; round++)
        {
            StepStarting?.Invoke(this, new OtaCycleStepUpdate(round, true, DateTimeOffset.Now));
            var forward = await launcher.StartAndWaitAsync(definition.ForwardTask, cancellationToken);
            Updated?.Invoke(this, new OtaCycleUpdate(round, true, forward, DateTimeOffset.Now));
            if (forward.State != OtaTaskState.Succeeded) return forward;

            await WaitBeforeNextStepAsync(definition.Interval, round, nextIsForward: false, cancellationToken);

            StepStarting?.Invoke(this, new OtaCycleStepUpdate(round, false, DateTimeOffset.Now));
            var reverse = await launcher.StartAndWaitAsync(definition.ReverseTask, cancellationToken);
            Updated?.Invoke(this, new OtaCycleUpdate(round, false, reverse, DateTimeOffset.Now));
            if (reverse.State != OtaTaskState.Succeeded) return reverse;

            if (round < definition.Rounds)
            {
                await WaitBeforeNextStepAsync(definition.Interval, round + 1, nextIsForward: true, cancellationToken);
            }
        }

        return new OtaTaskResult(OtaTaskState.Succeeded, "循环升级完成。", DateTimeOffset.Now);
    }

    private static bool AreOppositeVersions(OtaTask forward, OtaTask reverse)
        => forward.OldVersion.Equals(reverse.NewVersion, StringComparison.OrdinalIgnoreCase)
           && forward.NewVersion.Equals(reverse.OldVersion, StringComparison.OrdinalIgnoreCase);

    private async Task WaitBeforeNextStepAsync(
        OtaCycleIntervalOptions? options,
        int nextRound,
        bool nextIsForward,
        CancellationToken cancellationToken)
    {
        if (options is null) return;
        var delaySeconds = options.NextDelaySeconds(_random);
        if (delaySeconds <= 0) return;
        Waiting?.Invoke(this, new OtaCycleWaitUpdate(nextRound, nextIsForward, delaySeconds, DateTimeOffset.Now));
        await _delayAsync(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
    }
}
