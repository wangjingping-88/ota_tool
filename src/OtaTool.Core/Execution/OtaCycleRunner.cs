using OtaTool.Core.Models;

namespace OtaTool.Core.Execution;

public sealed record OtaCycleDefinition(OtaTask ForwardTask, OtaTask ReverseTask, int Rounds);

public sealed record OtaCycleUpdate(int Round, bool IsForward, OtaTaskResult Result, DateTimeOffset OccurredAt);

public interface IOtaTaskLauncher
{
    Task<OtaTaskResult> StartAndWaitAsync(OtaTask task, CancellationToken cancellationToken);
}

public sealed class OtaCycleRunner
{
    public event EventHandler<OtaCycleUpdate>? Updated;

    public async Task<OtaTaskResult> RunAsync(OtaCycleDefinition definition, IOtaTaskLauncher launcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(launcher);
        if (definition.Rounds <= 0) return new OtaTaskResult(OtaTaskState.Failed, "循环轮数必须大于 0。", DateTimeOffset.Now);
        if (!AreOppositeVersions(definition.ForwardTask, definition.ReverseTask)) return new OtaTaskResult(OtaTaskState.Failed, "循环升级的两个方向必须为 V1→V2 和 V2→V1。", DateTimeOffset.Now);

        for (var round = 1; round <= definition.Rounds; round++)
        {
            var forward = await launcher.StartAndWaitAsync(definition.ForwardTask, cancellationToken);
            Updated?.Invoke(this, new OtaCycleUpdate(round, true, forward, DateTimeOffset.Now));
            if (forward.State != OtaTaskState.Succeeded) return forward;

            var reverse = await launcher.StartAndWaitAsync(definition.ReverseTask, cancellationToken);
            Updated?.Invoke(this, new OtaCycleUpdate(round, false, reverse, DateTimeOffset.Now));
            if (reverse.State != OtaTaskState.Succeeded) return reverse;
        }

        return new OtaTaskResult(OtaTaskState.Succeeded, "循环升级完成。", DateTimeOffset.Now);
    }

    private static bool AreOppositeVersions(OtaTask forward, OtaTask reverse)
        => forward.OldVersion.Equals(reverse.NewVersion, StringComparison.OrdinalIgnoreCase)
           && forward.NewVersion.Equals(reverse.OldVersion, StringComparison.OrdinalIgnoreCase);
}
