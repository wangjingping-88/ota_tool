using System.Text;
using OtaTool.Core.Models;
using OtaTool.Core.Mqtt;
using OtaTool.Core.Protocols;

namespace OtaTool.Core.Execution;

public sealed record OtaExecutionUpdate(
    Guid TaskId,
    OtaTaskState State,
    string Message,
    DateTimeOffset OccurredAt,
    GatewayOtaStatus? GatewayStatus = null,
    GatewayFinalResult? FinalResult = null);

public interface ITaskSequenceStore
{
    Task<int> NextAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryTaskSequenceStore : ITaskSequenceStore
{
    private int _lastSequence;

    public InMemoryTaskSequenceStore(int initialSequence = 0) => _lastSequence = initialSequence;

    public Task<int> NextAsync(CancellationToken cancellationToken = default)
    {
        var value = Interlocked.Increment(ref _lastSequence);
        if (value <= 0) throw new OverflowException("OTA 任务序号已超出 Int32 范围。");
        return Task.FromResult(value);
    }
}

public sealed record OtaPollingOptions(
    TimeSpan InitialDelay,
    TimeSpan NormalInterval,
    TimeSpan BootVerifyInterval,
    TimeSpan QueryResponseTimeout)
{
    public static OtaPollingOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(8));
}

public sealed class OtaTaskRunner : IAsyncDisposable, IOtaTaskLauncher
{
    private const int StatusTimeoutWarningThreshold = 3;

    private readonly IMqttTransport _mqtt;
    private readonly IOtaProtocolProfile _profile;
    private readonly ITaskSequenceStore _sequenceStore;
    private readonly OtaProtocolOptions _protocolOptions;
    private readonly OtaPollingOptions _pollingOptions;
    private readonly SemaphoreSlim _singleTask = new(1, 1);
    private readonly object _sync = new();
    private ActiveTask? _activeTask;

    public OtaTaskRunner(
        IMqttTransport mqtt,
        IOtaProtocolProfile profile,
        ITaskSequenceStore sequenceStore,
        OtaProtocolOptions? protocolOptions = null,
        OtaPollingOptions? pollingOptions = null)
    {
        _mqtt = mqtt;
        _profile = profile;
        _sequenceStore = sequenceStore;
        _protocolOptions = protocolOptions ?? new OtaProtocolOptions();
        _pollingOptions = pollingOptions ?? OtaPollingOptions.Default;
        _mqtt.MessageReceived += OnMessageReceived;
    }

    public event EventHandler<OtaExecutionUpdate>? Updated;

    public event EventHandler<MqttApplicationMessage>? MessagePublished;

    public bool HasActiveTask
    {
        get
        {
            lock (_sync) return _activeTask is not null;
        }
    }

    public bool IsPollingPaused
    {
        get
        {
            lock (_sync) return _activeTask?.PollingPaused == true;
        }
    }

    public bool PausePolling()
    {
        ActiveTask active;
        lock (_sync)
        {
            if (_activeTask is not { } current || !_profile.SupportsGatewayStatusPolling) return false;
            active = current;
            active.PausePolling();
        }
        Emit(active, OtaTaskState.Running, "已暂停 Gateway OTA 状态轮询。", null, null);
        return true;
    }

    public bool ResumePolling()
    {
        ActiveTask active;
        lock (_sync)
        {
            if (_activeTask is not { } current || !_profile.SupportsGatewayStatusPolling) return false;
            active = current;
            active.ResumePolling();
        }
        Emit(active, OtaTaskState.Running, "已恢复 Gateway OTA 状态轮询。", null, null);
        return true;
    }

    public async Task<OtaTaskResult> StartAsync(OtaTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await _singleTask.WaitAsync(cancellationToken);
        try
        {
            if (_activeTask is not null)
            {
                return new OtaTaskResult(OtaTaskState.Failed, "当前已有活动 OTA 任务。", DateTimeOffset.Now);
            }
            if (!_mqtt.IsConnected)
            {
                return new OtaTaskResult(OtaTaskState.Failed, "MQTT 尚未连接。", DateTimeOffset.Now);
            }
            var profileValidation = OtaTaskValidator.Validate(task, _profile);
            if (!profileValidation.IsValid)
            {
                return new OtaTaskResult(OtaTaskState.Failed, profileValidation.Message, DateTimeOffset.Now);
            }
            var dispatchValidation = ValidateDispatch(task);
            if (dispatchValidation is not null)
            {
                return new OtaTaskResult(OtaTaskState.Failed, dispatchValidation, DateTimeOffset.Now);
            }

            var sequence = await _sequenceStore.NextAsync(cancellationToken);
            var subscribeTopic = _protocolOptions.UpstreamTopicFilterTemplate.Replace("{gatewayId}", task.GatewayId, StringComparison.Ordinal);
            await _mqtt.SubscribeAsync(subscribeTopic, qualityOfService: 1, cancellationToken);
            var outbound = OtaMessageCodec.CreateUpgradeRequest(task, sequence, _protocolOptions);
            var active = new ActiveTask(task, sequence, new CancellationTokenSource());
            lock (_sync)
            {
                _activeTask = active;
            }
            await PublishAsync(new MqttApplicationMessage(outbound.Topic, Encoding.UTF8.GetBytes(outbound.JsonPayload), outbound.QualityOfService), cancellationToken);
            Emit(active, OtaTaskState.Running, $"已发送 cmd=5 升级请求，任务序号 {sequence}。", null, null);
            if (_profile.SupportsGatewayStatusPolling)
            {
                active.PollingTask = PollAsync(active);
            }
            active.TimeoutTask = MonitorTimeoutAsync(active);
            return new OtaTaskResult(OtaTaskState.Running, "升级请求已发送。", DateTimeOffset.Now);
        }
        catch (Exception exception)
        {
            return new OtaTaskResult(OtaTaskState.Failed, exception.Message, DateTimeOffset.Now);
        }
        finally
        {
            _singleTask.Release();
        }
    }

    public async Task<OtaTaskResult> StartAndWaitAsync(OtaTask task, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<OtaTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(object? _, OtaExecutionUpdate update)
        {
            if (update.TaskId != task.Id) return;
            if (update.State is OtaTaskState.Succeeded or OtaTaskState.Failed or OtaTaskState.Cancelled or OtaTaskState.TimedOut)
            {
                completion.TrySetResult(new OtaTaskResult(update.State, update.Message, update.OccurredAt));
            }
        }
        Updated += OnUpdated;
        try
        {
            var start = await StartAsync(task, cancellationToken);
            if (start.State != OtaTaskState.Running) return start;
            return await completion.Task.WaitAsync(task.Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await CancelAsync(CancellationToken.None);
            return new OtaTaskResult(OtaTaskState.TimedOut, "等待最终升级结果超时。", DateTimeOffset.Now);
        }
        finally
        {
            Updated -= OnUpdated;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
        => await CancelCoreAsync(notifyGateway: false, cancellationToken);

    public async Task CancelAndNotifyGatewayAsync(CancellationToken cancellationToken = default)
        => await CancelCoreAsync(notifyGateway: true, cancellationToken);

    private async Task CancelCoreAsync(bool notifyGateway, CancellationToken cancellationToken)
    {
        ActiveTask? active;
        lock (_sync)
        {
            active = _activeTask;
            _activeTask = null;
        }
        if (active is null) return;
        active.Cancellation.Cancel();
        Emit(
            active,
            OtaTaskState.Cancelled,
            notifyGateway
                ? "工具已停止当前任务，正在通知 Gateway 取消升级。"
                : "任务已由工具取消。",
            null,
            null);

        string? gatewayCancelResult = null;
        if (notifyGateway && _mqtt.IsConnected)
        {
            try
            {
                var sequence = await _sequenceStore.NextAsync(cancellationToken);
                var cancel = OtaMessageCodec.CreateCancelRequest(
                    active.Task,
                    sequence,
                    _protocolOptions);
                await PublishAsync(
                    new MqttApplicationMessage(
                        cancel.Topic,
                        Encoding.UTF8.GetBytes(cancel.JsonPayload),
                        cancel.QualityOfService),
                    cancellationToken);
                gatewayCancelResult = $"已发送 cmd=5 active=0 取消请求，任务序号 {sequence}。";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                gatewayCancelResult = $"本地任务已取消，但 Gateway 取消请求发送失败：{exception.Message}";
            }
        }
        else if (notifyGateway)
        {
            gatewayCancelResult = "本地任务已取消；MQTT 未连接，未向 Gateway 发送取消请求。";
        }
        if (active.PollingTask is not null)
        {
            try { await active.PollingTask.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        }
        if (gatewayCancelResult is not null)
        {
            Emit(active, OtaTaskState.Cancelled, gatewayCancelResult, null, null);
        }
        active.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _mqtt.MessageReceived -= OnMessageReceived;
        await CancelAsync();
        _singleTask.Dispose();
    }

    private async Task PollAsync(ActiveTask active)
    {
        try
        {
            await Task.Delay(_pollingOptions.InitialDelay, active.Cancellation.Token);
            var backoff = _pollingOptions.NormalInterval;
            while (!active.Cancellation.IsCancellationRequested)
            {
                await active.WaitForPollingResumeAsync(active.Cancellation.Token);
                var querySequence = await _sequenceStore.NextAsync(active.Cancellation.Token);
                var query = OtaMessageCodec.CreateStatusQuery(active.Task.GatewayId, querySequence, active.TaskSequence, active.SessionId, _protocolOptions);
                var response = new TaskCompletionSource<GatewayOtaStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync)
                {
                    if (!ReferenceEquals(_activeTask, active)) return;
                    active.TrackStatusQuery(querySequence);
                    active.PendingStatusResponse = response;
                    active.PendingQuerySequence = querySequence;
                }
                await PublishAsync(new MqttApplicationMessage(query.Topic, Encoding.UTF8.GetBytes(query.JsonPayload), QualityOfService: 1), active.Cancellation.Token);
                Emit(active, OtaTaskState.Running, $"已发送 cmd=8 状态查询，查询序号 {querySequence}。", null, null);
                try
                {
                    var status = await response.Task.WaitAsync(_pollingOptions.QueryResponseTimeout, active.Cancellation.Token);
                    backoff = _pollingOptions.NormalInterval;
                    active.ConsecutiveStatusTimeouts = 0;
                    if (status.Result.Equals("TASK_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                    {
                        active.ConsecutiveTaskNotFound++;
                        if (active.ConsecutiveTaskNotFound >= 3)
                        {
                            Finish(active, OtaTaskState.Failed, "Gateway 连续三次未找到对应 OTA 任务，已停止轮询。", status, null);
                            return;
                        }
                        Emit(active, OtaTaskState.Running,
                            $"Gateway 暂未找到任务（{active.ConsecutiveTaskNotFound}/3），将继续轮询。", status, null);
                        await Task.Delay(_pollingOptions.NormalInterval, active.Cancellation.Token);
                        continue;
                    }
                    active.ConsecutiveTaskNotFound = 0;
                    if (!status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        Finish(active, OtaTaskState.Failed, $"Gateway 拒绝状态查询：{status.Result}。", status, null);
                        return;
                    }
                    if (status.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
                        || status.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                        || status.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    var interval = status.Stage.Equals("BOOT_VERIFY", StringComparison.OrdinalIgnoreCase)
                        ? _pollingOptions.BootVerifyInterval
                        : _pollingOptions.NormalInterval;
                    await Task.Delay(interval, active.Cancellation.Token);
                }
                catch (TimeoutException)
                {
                    active.ConsecutiveStatusTimeouts++;
                    if (active.ConsecutiveStatusTimeouts == StatusTimeoutWarningThreshold)
                    {
                        Emit(active, OtaTaskState.Running,
                            $"cmd=8 状态查询连续 {StatusTimeoutWarningThreshold} 次无响应，当前升级状态暂不可用；下游任务可能仍在运行，工具将降低频率继续查询。",
                            null,
                            null);
                    }
                    else
                    {
                        Emit(active, OtaTaskState.Running,
                            active.ConsecutiveStatusTimeouts < StatusTimeoutWarningThreshold
                                ? $"cmd=8 状态查询响应超时（{active.ConsecutiveStatusTimeouts}/{StatusTimeoutWarningThreshold}），将在 {backoff.TotalSeconds:0} 秒后重试。"
                                : $"cmd=8 状态查询仍无响应（已连续 {active.ConsecutiveStatusTimeouts} 次），将在 {backoff.TotalSeconds:0} 秒后继续查询。",
                            null,
                            null);
                    }
                    await Task.Delay(backoff, active.Cancellation.Token);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(active.PendingStatusResponse, response))
                        {
                            active.PendingStatusResponse = null;
                            active.PendingQuerySequence = 0;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Finish(active, OtaTaskState.Failed, $"状态轮询失败：{exception.Message}", null, null);
        }
    }

    private async Task MonitorTimeoutAsync(ActiveTask active)
    {
        try
        {
            await Task.Delay(active.Task.Timeout, active.Cancellation.Token);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeTask, active)) return;
            }
            Finish(active, OtaTaskState.TimedOut, "等待 Gateway 最终升级结果超时。", null, null);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested) { }
    }

    private void OnMessageReceived(object? sender, MqttApplicationMessage message)
    {
        var json = message.GetPayloadAsUtf8();
        ActiveTask? active;
        lock (_sync) active = _activeTask;
        if (active is null) return;

        if (OtaMessageCodec.TryParseGatewayStatus(json, out var status) && status is not null)
        {
            TaskCompletionSource<GatewayOtaStatus>? pendingResponse;
            lock (_sync)
            {
                if (!ReferenceEquals(_activeTask, active)
                    || status.TaskSequence != active.TaskSequence
                    || !active.IsTrackedStatusQuery(status.QuerySequence)
                    || (active.SessionId != 0 && status.SessionId != active.SessionId))
                {
                    return;
                }
                active.AcceptStatusQuery(status.QuerySequence);
                if (status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    active.SessionId = status.SessionId;
                    active.ConsecutiveStatusTimeouts = 0;
                }
                active.LastStage = status.Stage;
                pendingResponse = active.PendingStatusResponse;
            }
            pendingResponse?.TrySetResult(status);
            if (!status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var terminalState = status.Status.ToUpperInvariant() switch
            {
                "SUCCESS" => OtaTaskState.Succeeded,
                "FAILED" => OtaTaskState.Failed,
                "CANCELLED" => OtaTaskState.Cancelled,
                _ => OtaTaskState.Running,
            };
            var failedSubtask = status.Subtasks.FirstOrDefault(subtask =>
                subtask.Result.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
            var failedStage = status.Stages.FirstOrDefault(stage =>
                stage.State.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
            var hasFailureFact = failedSubtask is not null || failedStage is not null;
            var hasUnfinishedSubtask = status.Subtasks.Any(subtask => !IsTerminalSubtaskResult(subtask.Result));
            if (terminalState == OtaTaskState.Failed ||
                (terminalState != OtaTaskState.Cancelled &&
                 hasFailureFact &&
                 (terminalState == OtaTaskState.Succeeded || !hasUnfinishedSubtask)))
            {
                var failureDetail = failedSubtask is not null
                    ? $"Extender {failedSubtask.ExtenderId} 子任务失败：{failedSubtask.Stage} / {failedSubtask.Reason}"
                    : failedStage is not null
                        ? $"Gateway 阶段失败：{failedStage.Stage} / {failedStage.Reason}"
                        : $"Gateway 状态：{status.Status} / {status.Stage}";
                _ = FinishFailedAndCancelGatewayAsync(active, failureDetail, status, null);
            }
            else if (hasFailureFact)
            {
                var failedCount = status.Subtasks.Count(subtask =>
                    subtask.Result.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
                var unfinishedCount = status.Subtasks.Count(subtask => !IsTerminalSubtaskResult(subtask.Result));
                Emit(
                    active,
                    OtaTaskState.Running,
                    $"已有 {failedCount} 个 Extender 子任务失败，仍有 {unfinishedCount} 个子任务未结束；将继续轮询并等待全部 Extender 完成。",
                    status,
                    null);
            }
            else if (terminalState is OtaTaskState.Succeeded or OtaTaskState.Cancelled)
            {
                Finish(active, terminalState, $"Gateway 状态：{status.Status} / {status.Stage}", status, null);
            }
            else
            {
                Emit(active, terminalState, $"Gateway 状态：{status.Status} / {status.Stage}", status, null);
            }
            return;
        }

        if (OtaMessageCodec.TryParseGatewayFinalResult(json, out var final) && final is not null && IsMatchingFinalResult(active, final))
        {
            if (_profile.SupportsGatewayStatusPolling)
            {
                if (final.IsSuccess)
                {
                    Emit(active, OtaTaskState.Running, "已收到 Gateway 最终结果上报，正在使用 cmd=8 确认最终事实。", null, final);
                }
                else
                {
                    _ = FinishFailedAndCancelGatewayAsync(
                        active,
                        $"Gateway 最终结果上报失败：{final.Prompt}",
                        null,
                        final);
                }
            }
            else
            {
                var terminalState = final.IsSuccess ? OtaTaskState.Succeeded : OtaTaskState.Failed;
                Finish(active, terminalState, final.IsSuccess ? "Gateway 最终结果上报：升级完成。" : $"Gateway 最终结果上报：{final.Prompt}", null, final);
            }
        }
    }

    private static bool IsTerminalSubtaskResult(string result)
        => result.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
           result.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
           result.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchingFinalResult(ActiveTask active, GatewayFinalResult final)
    {
        if (final.Sequence != 0 && final.Sequence != active.TaskSequence) return false;
        return string.IsNullOrWhiteSpace(final.DeviceType)
            || final.DeviceType.Equals(OtaMessageCodec.ToProtocolDeviceType(active.Task.DeviceType), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateDispatch(OtaTask task)
    {
        if (string.IsNullOrWhiteSpace(task.GatewayId)) return "必须填写 Gateway ID。";
        if (!Uri.TryCreate(task.PatchUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Patch URL 必须是 HTTP 或 HTTPS 地址。";
        if (task.PatchMd5.Length != 32 || !task.PatchMd5.All(Uri.IsHexDigit)) return "Patch MD5 必须为 32 位十六进制字符串。";
        return null;
    }

    private void Emit(ActiveTask active, OtaTaskState state, string message, GatewayOtaStatus? status, GatewayFinalResult? final)
        => Updated?.Invoke(this, new OtaExecutionUpdate(active.Task.Id, state, message, DateTimeOffset.Now, status, final));

    private void Finish(ActiveTask active, OtaTaskState state, string message, GatewayOtaStatus? status, GatewayFinalResult? final)
    {
        if (Complete(active))
        {
            Emit(active, state, message, status, final);
        }
    }

    private async Task FinishFailedAndCancelGatewayAsync(
        ActiveTask active,
        string failureMessage,
        GatewayOtaStatus? status,
        GatewayFinalResult? final)
    {
        if (!Complete(active)) return;

        var cancelResult = "MQTT 未连接，无法向 Gateway 发送取消请求。";
        if (_mqtt.IsConnected)
        {
            try
            {
                var sequence = await _sequenceStore.NextAsync(CancellationToken.None);
                var cancel = OtaMessageCodec.CreateCancelRequest(active.Task, sequence, _protocolOptions);
                await PublishAsync(
                    new MqttApplicationMessage(
                        cancel.Topic,
                        Encoding.UTF8.GetBytes(cancel.JsonPayload),
                        cancel.QualityOfService),
                    CancellationToken.None);
                cancelResult = $"已发送 cmd=5 active=0 取消请求，任务序号 {sequence}。";
            }
            catch (Exception exception)
            {
                cancelResult = $"Gateway 取消请求发送失败：{exception.Message}";
            }
        }

        Emit(
            active,
            OtaTaskState.Failed,
            $"{failureMessage}；已停止状态轮询。{cancelResult}",
            status,
            final);
    }

    private async Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        await _mqtt.PublishAsync(message, cancellationToken);
        MessagePublished?.Invoke(this, message);
    }

    private bool Complete(ActiveTask active)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeTask, active)) return false;
            _activeTask = null;
        }
        active.Cancellation.Cancel();
        if (active.PollingTask is null)
        {
            active.Cancellation.Dispose();
        }
        else
        {
            _ = active.PollingTask.ContinueWith(
                _ => active.Cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return true;
    }

    private sealed class ActiveTask(OtaTask task, int taskSequence, CancellationTokenSource cancellation)
    {
        public OtaTask Task { get; } = task;
        public int TaskSequence { get; } = taskSequence;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public uint SessionId { get; set; }
        public string LastStage { get; set; } = string.Empty;
        public TaskCompletionSource<GatewayOtaStatus>? PendingStatusResponse { get; set; }
        public int PendingQuerySequence { get; set; }
        public int ConsecutiveTaskNotFound { get; set; }
        public int ConsecutiveStatusTimeouts { get; set; }
        public Task? PollingTask { get; set; }
        public Task? TimeoutTask { get; set; }
        private Queue<int> TrackedStatusQueries { get; } = new();
        private HashSet<int> TrackedStatusQuerySet { get; } = [];
        private int LastAcceptedStatusQuery { get; set; }
        private TaskCompletionSource<bool>? ResumeSignal { get; set; }
        public bool PollingPaused { get; private set; }

        public void TrackStatusQuery(int querySequence)
        {
            TrackedStatusQueries.Enqueue(querySequence);
            TrackedStatusQuerySet.Add(querySequence);
            while (TrackedStatusQueries.Count > 16)
            {
                TrackedStatusQuerySet.Remove(TrackedStatusQueries.Dequeue());
            }
        }

        public bool IsTrackedStatusQuery(int querySequence)
            => querySequence > LastAcceptedStatusQuery && TrackedStatusQuerySet.Contains(querySequence);

        public void AcceptStatusQuery(int querySequence)
        {
            LastAcceptedStatusQuery = querySequence;
            while (TrackedStatusQueries.TryPeek(out var trackedSequence) && trackedSequence <= querySequence)
            {
                TrackedStatusQuerySet.Remove(TrackedStatusQueries.Dequeue());
            }
        }

        public void PausePolling()
        {
            if (PollingPaused) return;
            PollingPaused = true;
            ResumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ResumePolling()
        {
            if (!PollingPaused) return;
            PollingPaused = false;
            ResumeSignal?.TrySetResult(true);
            ResumeSignal = null;
        }

        public Task WaitForPollingResumeAsync(CancellationToken cancellationToken)
        {
            if (!PollingPaused || ResumeSignal is null) return System.Threading.Tasks.Task.CompletedTask;
            return ResumeSignal.Task.WaitAsync(cancellationToken);
        }
    }
}
