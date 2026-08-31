using OtaTool.Core.Models;

namespace OtaTool.Core.Execution;

public sealed record OtaTestPlanPreparationResult(
    bool IsSuccess,
    string Message,
    OtaTestPlanPreparedItem? PreparedItem = null)
{
    public static OtaTestPlanPreparationResult Success(OtaTestPlanPreparedItem item, string message = "预检通过。")
        => new(true, message, item);

    public static OtaTestPlanPreparationResult Failure(string message)
        => new(false, message);
}

public sealed record OtaTestPlanOperationResult(
    OtaTaskState State,
    string Message,
    IReadOnlyList<Guid>? ChildReportIds = null)
{
    public bool IsSuccess => State == OtaTaskState.Succeeded;
}

public sealed record OtaTestPlanItemUpdate(
    Guid PlanId,
    Guid ItemId,
    int Index,
    int Total,
    OtaTestPlanItemState State,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record OtaTestPlanRunResult(
    OtaTestPlanState State,
    string Message,
    int Succeeded,
    int Failed,
    int Skipped,
    DateTimeOffset OccurredAt);

public interface IOtaTestPlanItemExecutor
{
    Task<string?> ValidatePlanAsync(OtaTestPlanTemplate plan, CancellationToken cancellationToken);

    Task<OtaTestPlanPreparationResult> PreflightAsync(
        OtaTestPlanItemTemplate item,
        bool justInTime,
        CancellationToken cancellationToken);

    Task<OtaTestPlanOperationResult> ExecuteAsync(
        OtaTestPlanPreparedItem item,
        CancellationToken cancellationToken);

    Task<OtaTestPlanOperationResult> VerifyAsync(
        OtaTestPlanPreparedItem item,
        CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed class OtaTestPlanRunner
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _singleRun = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private IOtaTestPlanItemExecutor? _activeExecutor;

    public OtaTestPlanRunner(Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        => _delayAsync = delayAsync ?? Task.Delay;

    public event EventHandler<OtaTestPlanItemUpdate>? Updated;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _activeCancellation is not null;
        }
    }

    public async Task<IReadOnlyList<OtaTestPlanItemUpdate>> PreflightAsync(
        OtaTestPlanTemplate plan,
        IOtaTestPlanItemExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executor);
        var items = ValidateAndOrder(plan);
        var planError = await executor.ValidatePlanAsync(plan, cancellationToken);
        if (planError is not null)
        {
            var failedUpdates = items.Select((item, index) => CreateUpdate(
                plan, item, index, items.Count, OtaTestPlanItemState.Failed, planError)).ToArray();
            foreach (var update in failedUpdates) Updated?.Invoke(this, update);
            return failedUpdates;
        }

        var updates = new List<OtaTestPlanItemUpdate>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            Emit(plan, item, index, items.Count, OtaTestPlanItemState.Preflighting, "正在执行计划预检…");
            OtaTestPlanPreparationResult result;
            try
            {
                result = await executor.PreflightAsync(item, justInTime: false, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = OtaTestPlanPreparationResult.Failure(exception.Message);
            }
            var update = CreateUpdate(
                plan,
                item,
                index,
                items.Count,
                result.IsSuccess ? OtaTestPlanItemState.Ready : OtaTestPlanItemState.Failed,
                result.Message);
            updates.Add(update);
            Updated?.Invoke(this, update);
        }
        return updates;
    }

    public async Task<OtaTestPlanRunResult> RunAsync(
        OtaTestPlanTemplate plan,
        IOtaTestPlanItemExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executor);
        await _singleRun.WaitAsync(cancellationToken);
        IReadOnlyList<OtaTestPlanItemTemplate> items = [];
        var terminalItems = new HashSet<Guid>();
        var currentIndex = -1;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        try
        {
            lock (_sync)
            {
                if (_activeCancellation is not null)
                {
                    return new(OtaTestPlanState.Failed, "已有测试计划正在执行。", 0, 1, 0, DateTimeOffset.Now);
                }
                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeExecutor = executor;
            }

            var token = _activeCancellation.Token;
            items = ValidateAndOrder(plan);
            var preflight = await PreflightAsync(plan, executor, token);
            if (preflight.Any(update => update.State == OtaTestPlanItemState.Failed))
            {
                return new(OtaTestPlanState.Failed, "计划预检失败，未启动任何升级任务。", 0, preflight.Count(update => update.State == OtaTestPlanItemState.Failed), 0, DateTimeOffset.Now);
            }

            for (var index = 0; index < items.Count; index++)
            {
                currentIndex = index;
                token.ThrowIfCancellationRequested();
                var item = items[index];
                Emit(plan, item, index, items.Count, OtaTestPlanItemState.Preflighting, "正在执行任务开始前实时校验…");
                var prepared = await executor.PreflightAsync(item, justInTime: true, token);
                if (!prepared.IsSuccess || prepared.PreparedItem is null)
                {
                    failed++;
                    Emit(plan, item, index, items.Count, OtaTestPlanItemState.Failed, prepared.Message);
                    terminalItems.Add(item.Id);
                    if (!plan.ContinueOnFailure)
                    {
                        skipped += SkipRemaining(plan, items, index + 1, prepared.Message);
                        return new(OtaTestPlanState.Failed, prepared.Message, succeeded, failed, skipped, DateTimeOffset.Now);
                    }
                    await WaitBetweenItemsAsync(plan, index, items.Count, token);
                    continue;
                }

                Emit(plan, item, index, items.Count, OtaTestPlanItemState.Running, "升级任务正在执行…");
                var execution = await executor.ExecuteAsync(prepared.PreparedItem, token);
                if (!execution.IsSuccess)
                {
                    failed++;
                    var itemState = execution.State switch
                    {
                        OtaTaskState.Cancelled => OtaTestPlanItemState.Cancelled,
                        OtaTaskState.TimedOut => OtaTestPlanItemState.TimedOut,
                        _ => OtaTestPlanItemState.Failed,
                    };
                    Emit(plan, item, index, items.Count, itemState, execution.Message);
                    terminalItems.Add(item.Id);
                    if (!plan.ContinueOnFailure || itemState == OtaTestPlanItemState.Cancelled)
                    {
                        skipped += SkipRemaining(plan, items, index + 1, execution.Message);
                        return new(
                            itemState == OtaTestPlanItemState.Cancelled ? OtaTestPlanState.Cancelled : OtaTestPlanState.Failed,
                            execution.Message,
                            succeeded,
                            failed,
                            skipped,
                            DateTimeOffset.Now);
                    }
                    await WaitBetweenItemsAsync(plan, index, items.Count, token);
                    continue;
                }

                Emit(plan, item, index, items.Count, OtaTestPlanItemState.Verifying, "Gateway 已完成，正在复查目标版本…");
                var verification = await executor.VerifyAsync(prepared.PreparedItem, token);
                if (verification.IsSuccess)
                {
                    succeeded++;
                    Emit(plan, item, index, items.Count, OtaTestPlanItemState.Succeeded, verification.Message);
                    terminalItems.Add(item.Id);
                }
                else
                {
                    failed++;
                    var itemState = verification.State == OtaTaskState.TimedOut
                        ? OtaTestPlanItemState.TimedOut
                        : OtaTestPlanItemState.Failed;
                    Emit(plan, item, index, items.Count, itemState, verification.Message);
                    terminalItems.Add(item.Id);
                    if (!plan.ContinueOnFailure)
                    {
                        skipped += SkipRemaining(plan, items, index + 1, verification.Message);
                        return new(OtaTestPlanState.Failed, verification.Message, succeeded, failed, skipped, DateTimeOffset.Now);
                    }
                }
                await WaitBetweenItemsAsync(plan, index, items.Count, token);
            }

            var planState = failed == 0 ? OtaTestPlanState.Succeeded : OtaTestPlanState.Failed;
            var message = failed == 0
                ? $"测试计划完成，共 {succeeded} 项全部通过。"
                : $"测试计划执行结束：成功 {succeeded}，失败 {failed}。";
            return new(planState, message, succeeded, failed, skipped, DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            if (items.Count > 0)
            {
                var cancelledIndex = Math.Clamp(currentIndex < 0 ? 0 : currentIndex, 0, items.Count - 1);
                if (!terminalItems.Contains(items[cancelledIndex].Id))
                {
                    Emit(plan, items[cancelledIndex], cancelledIndex, items.Count, OtaTestPlanItemState.Cancelled, "测试计划已取消，当前任务停止。 ");
                    terminalItems.Add(items[cancelledIndex].Id);
                }
                skipped += SkipRemaining(plan, items, cancelledIndex + 1, "用户取消测试计划");
            }
            return new(OtaTestPlanState.Cancelled, "测试计划已取消。", succeeded, failed, skipped, DateTimeOffset.Now);
        }
        finally
        {
            lock (_sync)
            {
                _activeCancellation?.Dispose();
                _activeCancellation = null;
                _activeExecutor = null;
            }
            _singleRun.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? active;
        IOtaTestPlanItemExecutor? executor;
        lock (_sync)
        {
            active = _activeCancellation;
            executor = _activeExecutor;
            active?.Cancel();
        }
        if (executor is not null)
        {
            await executor.CancelAsync(cancellationToken);
        }
    }

    private static IReadOnlyList<OtaTestPlanItemTemplate> ValidateAndOrder(OtaTestPlanTemplate plan)
    {
        if (plan.Items.Count == 0) throw new InvalidOperationException("测试计划至少需要一个任务。");
        if (plan.InterItemDelaySeconds is < 0 or > 86400) throw new InvalidOperationException("任务间隔必须是 0～86400 秒。");
        if (string.IsNullOrWhiteSpace(plan.GatewayId)) throw new InvalidOperationException("测试计划缺少 Gateway ID。");
        if (plan.Items.Any(item => item.Mode != plan.Mode || !string.Equals(item.GatewayId, plan.GatewayId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("测试计划中的所有任务必须属于同一协议模式和 Gateway。");
        }
        if (plan.Items.Select(item => item.Id).Distinct().Count() != plan.Items.Count)
        {
            throw new InvalidOperationException("测试计划中存在重复的任务 ID。");
        }
        if (plan.Items.Any(item => item.Order <= 0) || plan.Items.Select(item => item.Order).Distinct().Count() != plan.Items.Count)
        {
            throw new InvalidOperationException("测试计划任务顺序必须是唯一正整数。");
        }
        return plan.Items.OrderBy(item => item.Order).ThenBy(item => item.Id).ToArray();
    }

    private int SkipRemaining(
        OtaTestPlanTemplate plan,
        IReadOnlyList<OtaTestPlanItemTemplate> items,
        int startIndex,
        string reason)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            Emit(plan, items[index], index, items.Count, OtaTestPlanItemState.Skipped, $"前序任务未通过：{reason}");
        }
        return Math.Max(0, items.Count - startIndex);
    }

    private async Task WaitBetweenItemsAsync(OtaTestPlanTemplate plan, int index, int total, CancellationToken cancellationToken)
    {
        if (index >= total - 1 || plan.InterItemDelaySeconds <= 0) return;
        await _delayAsync(TimeSpan.FromSeconds(plan.InterItemDelaySeconds), cancellationToken);
    }

    private void Emit(
        OtaTestPlanTemplate plan,
        OtaTestPlanItemTemplate item,
        int index,
        int total,
        OtaTestPlanItemState state,
        string message)
        => Updated?.Invoke(this, CreateUpdate(plan, item, index, total, state, message));

    private static OtaTestPlanItemUpdate CreateUpdate(
        OtaTestPlanTemplate plan,
        OtaTestPlanItemTemplate item,
        int index,
        int total,
        OtaTestPlanItemState state,
        string message)
        => new(plan.Id, item.Id, index + 1, total, state, message, DateTimeOffset.Now);
}
