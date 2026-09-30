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
    GatewayFinalResult? FinalResult = null,
    GatewayOtaRequestAck? RequestAck = null);

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
    TimeSpan QueryResponseTimeout,
    TimeSpan StartAcceptanceWait = default,
    int MaxStartAttempts = 3)
{
    public static OtaPollingOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(5),
        3);

    public TimeSpan EffectiveStartAcceptanceWait =>
        StartAcceptanceWait > TimeSpan.Zero ? StartAcceptanceWait : InitialDelay;

    public int EffectiveMaxStartAttempts => Math.Max(1, MaxStartAttempts);
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
            var active = new ActiveTask(task, sequence, outbound, new CancellationTokenSource());
            lock (_sync)
            {
                _activeTask = active;
            }
            try
            {
                await PublishAsync(new MqttApplicationMessage(outbound.Topic, Encoding.UTF8.GetBytes(outbound.JsonPayload), outbound.QualityOfService), cancellationToken);
            }
            catch
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_activeTask, active)) _activeTask = null;
                }
                active.Cancellation.Cancel();
                active.Cancellation.Dispose();
                throw;
            }
            active.MarkStartPublished();
            Emit(active, OtaTaskState.Running, $"已发送 cmd=5 升级请求，任务序号 {sequence}；正在等待 Gateway 业务受理（1/{_pollingOptions.EffectiveMaxStartAttempts}）。", null, null);
            if (_profile.SupportsGatewayStatusPolling)
            {
                active.PollingTask = PollAsync(active);
            }
            active.TimeoutTask = MonitorTimeoutAsync(active);
            return new OtaTaskResult(OtaTaskState.Running, "升级请求已发送，正在等待 Gateway 业务受理。", DateTimeOffset.Now);
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
            if (update.State is OtaTaskState.Succeeded or OtaTaskState.PartiallySucceeded or OtaTaskState.Failed or OtaTaskState.Cancelled or OtaTaskState.TimedOut)
            {
                completion.TrySetResult(new OtaTaskResult(
                    update.State,
                    update.Message,
                    update.OccurredAt,
                    update.GatewayStatus?.TargetSuccess,
                    update.GatewayStatus?.TargetFailed,
                    update.GatewayStatus?.RetryRound,
                    update.GatewayStatus?.FailedNodes));
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
        lock (_sync) active = _activeTask;
        if (notifyGateway && active?.Task.Mode == OtaMode.IPM)
        {
            // 取消请求不代表设备已停止，提交后的任务仍可能成功。保持同一任务的状态核对。
            active.StartRetryProhibited = true;
            if (!_mqtt.IsConnected)
            {
                Emit(active, OtaTaskState.Running, "取消请求尚未发送：MQTT 未连接；仍保留活动任务，等待重连核对最终状态。", null, null);
                return;
            }
            var sequence = await _sequenceStore.NextAsync(cancellationToken);
            var cancel = OtaMessageCodec.CreateCancelRequest(active.Task, sequence, _protocolOptions);
            await PublishAsync(new MqttApplicationMessage(cancel.Topic, Encoding.UTF8.GetBytes(cancel.JsonPayload), cancel.QualityOfService), cancellationToken);
            active.ResumePolling();
            Emit(active, OtaTaskState.Running, "已请求取消；Flash 写入或已提交升级可能继续，正在核对设备最终状态。", null, null);
            return;
        }
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
            if (!await ConfirmStartAsync(active)) return;
            var backoff = _pollingOptions.NormalInterval;
            while (!active.Cancellation.IsCancellationRequested)
            {
                await active.WaitForPollingResumeAsync(active.Cancellation.Token);
                try
                {
                    var status = await QueryStatusAsync(active);
                    if (!IsCurrent(active)) return;
                    active.ConsecutiveStatusTimeouts = 0;
                    if (status.Result.Equals("TASK_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                    {
                        backoff = _pollingOptions.NormalInterval;
                        active.ConsecutiveTaskNotFound++;
                        if (active.ConsecutiveTaskNotFound >= 3)
                        {
                            Finish(active, OtaTaskState.Failed,
                                "Gateway 已受理过启动请求，但连续三次查询均未找到该任务；可能发生过网关重启或状态快照丢失。为避免重复升级，工具不会重发启动请求。",
                                status,
                                null);
                            return;
                        }
                        Emit(active, OtaTaskState.Running,
                            $"已受理任务暂时无法查询（TASK_NOT_FOUND，{active.ConsecutiveTaskNotFound}/3）；不会重发启动请求，将继续核对状态。", status, null);
                        await Task.Delay(_pollingOptions.NormalInterval, active.Cancellation.Token);
                        continue;
                    }
                    active.ConsecutiveTaskNotFound = 0;
                    if (status.Result.Equals("RESOURCE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        Emit(active, OtaTaskState.Running,
                            $"Gateway 暂时无法读取 OTA 状态（RESOURCE_UNAVAILABLE），将在 {backoff.TotalSeconds:0} 秒后重试。",
                            status,
                            null);
                        await Task.Delay(backoff, active.Cancellation.Token);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                        continue;
                    }
                    if (!status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        Finish(active, OtaTaskState.Failed, $"Gateway 拒绝状态查询：{status.Result}。", status, null);
                        return;
                    }
                    backoff = _pollingOptions.NormalInterval;
                    if (status.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
                        || status.Status.Equals("PARTIAL_SUCCESS", StringComparison.OrdinalIgnoreCase)
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
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Finish(active, OtaTaskState.Failed, $"状态轮询失败：{exception.Message}", null, null);
        }
    }

    private async Task<bool> ConfirmStartAsync(ActiveTask active)
    {
        var queryBackoff = _pollingOptions.NormalInterval;
        while (!active.Cancellation.IsCancellationRequested)
        {
            await active.WaitForPollingResumeAsync(active.Cancellation.Token);
            GatewayOtaRequestAck? ack = null;
            if (_profile.SupportsStartAck && !active.StartRetryProhibited)
            {
                ack = await WaitForAcceptanceAckAsync(active);
            }
            if (ack is not null)
            {
                if (ack.Accepted &&
                    (ack.Result.Equals("ACCEPTED", StringComparison.OrdinalIgnoreCase) ||
                     ack.Result.Equals("DUPLICATE", StringComparison.OrdinalIgnoreCase)))
                {
                    active.SessionId = ack.SessionId;
                    active.AcceptanceConfirmed = true;
                    active.StartRetryProhibited = true;
                    var duplicate = ack.Duplicate ? "，命中幂等记录，未重复创建任务" : string.Empty;
                    var terminalReason = string.IsNullOrWhiteSpace(ack.Reason) ? string.Empty : $"，原因 {ack.Reason}";
                    Emit(active, OtaTaskState.Running,
                        $"Gateway 已受理升级请求{duplicate}；SID {ack.SessionId}，当前状态 {ack.Status}{terminalReason}。将通过 cmd=8/9 继续核对任务事实。",
                        null,
                        null,
                        ack);
                    return true;
                }

                if (ack.Reason.Equals("REQUEST_ID_RESERVED", StringComparison.OrdinalIgnoreCase))
                {
                    active.SessionId = ack.SessionId;
                    active.StartRetryProhibited = true;
                    active.ClearAcceptanceAck();
                    Emit(active, OtaTaskState.Running,
                        $"启动序号已被恢复任务占用（REQUEST_ID_RESERVED，SID {ack.SessionId}）；工具不会重建任务，正在查询已有任务。",
                        null,
                        null,
                        ack);
                }
                else if (ack.Reason.Equals("QUEUE_FAILED", StringComparison.OrdinalIgnoreCase) &&
                         active.StartAttemptCount < _pollingOptions.EffectiveMaxStartAttempts)
                {
                    Emit(active, OtaTaskState.Running,
                        $"Gateway 未受理启动请求：QUEUE_FAILED。将保持任务序号和全部参数不变后重发（下一次 {active.StartAttemptCount + 1}/{_pollingOptions.EffectiveMaxStartAttempts}）。",
                        null,
                        null,
                        ack);
                    await Task.Delay(_pollingOptions.NormalInterval, active.Cancellation.Token);
                    await RepublishStartAsync(active);
                    continue;
                }
                else
                {
                    var reason = string.IsNullOrWhiteSpace(ack.Reason) ? ack.Result : ack.Reason;
                    Finish(active, OtaTaskState.Failed,
                        $"升级请求未受理：{reason}；任务未建立，未进入设备升级阶段。",
                        null,
                        null,
                        ack);
                    return false;
                }
            }

            GatewayOtaStatus status;
            try
            {
                status = await QueryStatusAsync(active);
                if (!IsCurrent(active)) return false;
            }
            catch (TimeoutException)
            {
                active.ConsecutiveStatusTimeouts++;
                Emit(active, OtaTaskState.Running,
                    $"启动尚未确认，cmd=8 查询无响应；当前任务状态未知，将在 {queryBackoff.TotalSeconds:0} 秒后继续查询，不会据此重发启动请求。",
                    null,
                    null);
                await Task.Delay(queryBackoff, active.Cancellation.Token);
                queryBackoff = TimeSpan.FromSeconds(Math.Min(queryBackoff.TotalSeconds * 2, 30));
                continue;
            }

            active.ConsecutiveStatusTimeouts = 0;
            if (status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                active.SessionId = status.SessionId;
                active.AcceptanceConfirmed = true;
                active.StartRetryProhibited = true;
                Emit(active, OtaTaskState.Running,
                    $"cmd=9 已确认任务存在；已绑定 SID {status.SessionId}，继续升级闭环。",
                    status,
                    null);
                return true;
            }

            if (status.Result.Equals("TASK_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                if (active.StartRetryProhibited)
                {
                    Finish(active, OtaTaskState.Failed,
                        "启动序号受保护且查询未找到已有任务；为避免覆盖恢复任务，工具未重发启动请求，请人工确认网关状态。",
                        status,
                        null);
                    return false;
                }
                if (active.StartAttemptCount >= _pollingOptions.EffectiveMaxStartAttempts)
                {
                    Finish(active, OtaTaskState.Failed,
                        $"升级请求未确认 / 任务未建立：累计发送 {active.StartAttemptCount} 次，每次查询均明确返回 TASK_NOT_FOUND。",
                        status,
                        null);
                    return false;
                }
                Emit(active, OtaTaskState.Running,
                    $"Gateway 明确返回 TASK_NOT_FOUND；将保持原任务序号和完整参数不变后重发（下一次 {active.StartAttemptCount + 1}/{_pollingOptions.EffectiveMaxStartAttempts}）。",
                    status,
                    null);
                await RepublishStartAsync(active);
                queryBackoff = _pollingOptions.NormalInterval;
                continue;
            }

            if (status.Result.Equals("RESOURCE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                Emit(active, OtaTaskState.Running,
                    $"Gateway 暂时无法查询启动状态（RESOURCE_UNAVAILABLE），将在 {queryBackoff.TotalSeconds:0} 秒后重试；不会重发启动请求。",
                    status,
                    null);
                await Task.Delay(queryBackoff, active.Cancellation.Token);
                queryBackoff = TimeSpan.FromSeconds(Math.Min(queryBackoff.TotalSeconds * 2, 30));
                continue;
            }

            Finish(active, OtaTaskState.Failed,
                $"升级请求状态核对失败：{status.Result}；任务是否建立尚未确认。",
                status,
                null);
            return false;
        }
        return false;
    }

    private async Task<GatewayOtaRequestAck?> WaitForAcceptanceAckAsync(ActiveTask active)
    {
        Task<GatewayOtaRequestAck> pending;
        TimeSpan remaining;
        lock (_sync)
        {
            if (!ReferenceEquals(_activeTask, active)) return null;
            if (active.LastAcceptanceAck is not null) return active.LastAcceptanceAck;
            pending = active.PendingAcceptanceResponse.Task;
            var deadline = active.LastStartPublishedAt + _pollingOptions.EffectiveStartAcceptanceWait;
            remaining = deadline - DateTimeOffset.Now;
        }
        if (remaining <= TimeSpan.Zero) return null;
        try
        {
            return await pending.WaitAsync(remaining, active.Cancellation.Token);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task RepublishStartAsync(ActiveTask active)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeTask, active)) return;
            active.PrepareStartRetry();
        }
        var request = active.StartRequest;
        await PublishAsync(
            new MqttApplicationMessage(request.Topic, Encoding.UTF8.GetBytes(request.JsonPayload), request.QualityOfService),
            active.Cancellation.Token);
        active.MarkStartPublished();
        Emit(active, OtaTaskState.Running,
            $"已按原主题序号 {active.TaskSequence} 和原始参数重发 cmd=5（{active.StartAttemptCount}/{_pollingOptions.EffectiveMaxStartAttempts}）；正在等待 Gateway 业务受理。",
            null,
            null);
    }

    private async Task<GatewayOtaStatus> QueryStatusAsync(ActiveTask active)
    {
        var querySequence = await _sequenceStore.NextAsync(active.Cancellation.Token);
        var query = OtaMessageCodec.CreateStatusQuery(active.Task.GatewayId, querySequence, active.TaskSequence, active.SessionId, _protocolOptions);
        var response = new TaskCompletionSource<GatewayOtaStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (!ReferenceEquals(_activeTask, active)) throw new OperationCanceledException(active.Cancellation.Token);
            active.TrackStatusQuery(querySequence);
            active.PendingStatusResponse = response;
            active.PendingQuerySequence = querySequence;
        }
        try
        {
            await PublishAsync(
                new MqttApplicationMessage(query.Topic, Encoding.UTF8.GetBytes(query.JsonPayload), QualityOfService: 1),
                active.Cancellation.Token);
            Emit(active, OtaTaskState.Running, $"已发送 cmd=8 状态查询，查询序号 {querySequence}。", null, null);
            return await response.Task.WaitAsync(_pollingOptions.QueryResponseTimeout, active.Cancellation.Token);
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

    private bool IsCurrent(ActiveTask active)
    {
        lock (_sync) return ReferenceEquals(_activeTask, active);
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

        var upstreamPrefix = _protocolOptions.UpstreamTopicFilterTemplate
            .Replace("{gatewayId}", active.Task.GatewayId, StringComparison.Ordinal).TrimEnd('#');
        if (!message.Topic.StartsWith(upstreamPrefix, StringComparison.Ordinal)) return;

        if (OtaMessageCodec.TryParseGatewayOtaRequestAck(json, out var ack) && ack is not null)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_activeTask, active) ||
                    ack.TaskSequence != active.TaskSequence ||
                    !IsMatchingRequestAckTopic(message.Topic, active) ||
                    (active.SessionId != 0 && ack.SessionId != 0 && ack.SessionId != active.SessionId))
                {
                    return;
                }
                active.RecordAcceptanceAck(ack);
            }
            return;
        }

        if (OtaMessageCodec.TryParseGatewayStatus(json, out var status) && status is not null)
        {
            TaskCompletionSource<GatewayOtaStatus>? pendingResponse;
            lock (_sync)
            {
                if (!ReferenceEquals(_activeTask, active)
                    || status.TaskSequence != active.TaskSequence
                    || !active.IsTrackedStatusQuery(status.QuerySequence)
                    || (active.SessionId != 0 &&
                        status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                        status.SessionId != active.SessionId))
                {
                    return;
                }
                active.AcceptStatusQuery(status.QuerySequence);
                if (status.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    active.SessionId = status.SessionId;
                    active.AcceptanceConfirmed = true;
                    active.StartRetryProhibited = true;
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
                "PARTIAL_SUCCESS" => OtaTaskState.PartiallySucceeded,
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
            if (terminalState == OtaTaskState.PartiallySucceeded)
            {
                var failedNodes = status.FailedNodes.Count == 0
                    ? "Gateway 未返回最终失败节点名单"
                    : $"失败节点：{string.Join("；", status.FailedNodes.Select(FormatFailedNode))}";
                Finish(
                    active,
                    terminalState,
                    $"Node OTA 部分成功：成功 {status.TargetSuccess ?? 0}/{status.TargetTotal ?? 0}，失败 {status.TargetFailed ?? status.FailedNodes.Count}；实际重试轮次 {status.RetryRound ?? 0}；{failedNodes}。已暂停循环和后续队列任务。",
                    status,
                    null);
            }
            else if (terminalState == OtaTaskState.Failed ||
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

    private bool IsMatchingRequestAckTopic(string topic, ActiveTask active)
    {
        var filter = _protocolOptions.UpstreamTopicFilterTemplate
            .Replace("{gatewayId}", active.Task.GatewayId, StringComparison.Ordinal);
        var prefix = filter.EndsWith('#') ? filter[..^1] : filter.TrimEnd('/') + "/";
        var expectedTopic = prefix + active.TaskSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return topic.Equals(expectedTopic, StringComparison.Ordinal);
    }

    private static string FormatFailedNode(OtaFailedNode node)
        => $"Extender {node.ExtenderId} (0x{node.ExtenderId:X}) / 类型 {node.NodeType} / Node {node.NodeId} (0x{node.NodeId:X4})";

    private static bool IsMatchingFinalResult(ActiveTask active, GatewayFinalResult final)
    {
        if (final.Sequence != 0 && final.Sequence != active.TaskSequence) return false;
        if (!byte.TryParse(active.Task.OldVersion, out var expectedOldVersion) ||
            !byte.TryParse(active.Task.NewVersion, out var expectedNewVersion) ||
            final.OldVersion != expectedOldVersion ||
            final.NewVersion != expectedNewVersion)
        {
            return false;
        }
        return string.IsNullOrWhiteSpace(final.DeviceType) ||
            final.DeviceType.Equals(OtaMessageCodec.ToProtocolDeviceType(active.Task.DeviceType), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateDispatch(OtaTask task)
    {
        if (string.IsNullOrWhiteSpace(task.GatewayId)) return "必须填写 Gateway ID。";
        var patchUrlError = OtaMessageCodec.ValidateGatewayPatchUrl(task.PatchUrl);
        if (patchUrlError is not null) return patchUrlError;
        if (task.PatchMd5.Length != 32 || !task.PatchMd5.All(Uri.IsHexDigit)) return "Patch MD5 必须为 32 位十六进制字符串。";
        return null;
    }

    private void Emit(
        ActiveTask active,
        OtaTaskState state,
        string message,
        GatewayOtaStatus? status,
        GatewayFinalResult? final,
        GatewayOtaRequestAck? ack = null)
        => Updated?.Invoke(this, new OtaExecutionUpdate(active.Task.Id, state, message, DateTimeOffset.Now, status, final, ack));

    private void Finish(
        ActiveTask active,
        OtaTaskState state,
        string message,
        GatewayOtaStatus? status,
        GatewayFinalResult? final,
        GatewayOtaRequestAck? ack = null)
    {
        if (Complete(active))
        {
            Emit(active, state, message, status, final, ack);
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

    private sealed class ActiveTask(
        OtaTask task,
        int taskSequence,
        OutboundOtaMessage startRequest,
        CancellationTokenSource cancellation)
    {
        public OtaTask Task { get; } = task;
        public int TaskSequence { get; } = taskSequence;
        public OutboundOtaMessage StartRequest { get; } = startRequest;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public uint SessionId { get; set; }
        public bool AcceptanceConfirmed { get; set; }
        public bool StartRetryProhibited { get; set; }
        public int StartAttemptCount { get; private set; } = 1;
        public DateTimeOffset LastStartPublishedAt { get; private set; } = DateTimeOffset.Now;
        public GatewayOtaRequestAck? LastAcceptanceAck { get; private set; }
        public TaskCompletionSource<GatewayOtaRequestAck> PendingAcceptanceResponse { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        public void MarkStartPublished() => LastStartPublishedAt = DateTimeOffset.Now;

        public void RecordAcceptanceAck(GatewayOtaRequestAck ack)
        {
            LastAcceptanceAck = ack;
            PendingAcceptanceResponse.TrySetResult(ack);
        }

        public void ClearAcceptanceAck()
        {
            LastAcceptanceAck = null;
            PendingAcceptanceResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void PrepareStartRetry()
        {
            StartAttemptCount++;
            ClearAcceptanceAck();
        }

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
