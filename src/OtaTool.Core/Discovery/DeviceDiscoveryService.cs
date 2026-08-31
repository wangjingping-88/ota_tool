using System.Collections.Concurrent;
using System.Threading.Channels;
using OtaTool.Core.Mqtt;
using OtaTool.Core.Protocols;

namespace OtaTool.Core.Discovery;

public sealed record ExtenderNodeDiscoveryResult(
    uint ExtenderId,
    IReadOnlyList<GatewayNodeInfo> Nodes,
    int ReportedCount,
    string? Error)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(Error);
}

public sealed record ExtenderStatusDiscoveryResult(
    uint ExtenderId,
    GatewayExtenderStatus? Status,
    string? Error)
{
    public bool IsSuccess => Status is not null && string.IsNullOrWhiteSpace(Error);
}

public sealed record DeviceDiscoveryOptions(
    TimeSpan ResponseTimeout,
    TimeSpan AuthListQuietPeriod,
    int NodeGroupAttempts = 2,
    TimeSpan? ResponseDrainPeriod = null)
{
    public static DeviceDiscoveryOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(500),
        2,
        TimeSpan.FromMilliseconds(500));
}

public sealed class DeviceDiscoveryService
{
    private readonly IMqttTransport _mqtt;
    private readonly DeviceDiscoveryOptions _options;
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _extenderQueryLocks = new();
    private int _sequence = Math.Max(1, Environment.TickCount & 0x3fffffff);

    public DeviceDiscoveryService(IMqttTransport mqtt, DeviceDiscoveryOptions? options = null)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _options = options ?? DeviceDiscoveryOptions.Default;
        if (_options.ResponseTimeout <= TimeSpan.Zero ||
            _options.AuthListQuietPeriod <= TimeSpan.Zero ||
            _options.NodeGroupAttempts < 1 ||
            (_options.ResponseDrainPeriod.HasValue && _options.ResponseDrainPeriod.Value < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public event EventHandler<MqttApplicationMessage>? MessagePublished;

    public async Task<IReadOnlyList<GatewayExtenderInfo>> DiscoverExtendersAsync(
        string gatewayId,
        CancellationToken cancellationToken = default)
    {
        var sequence = NextSequence();
        var pages = Channel.CreateUnbounded<IReadOnlyList<GatewayExtenderInfo>>();
        var extenders = new ConcurrentDictionary<uint, GatewayExtenderInfo>();

        void Handler(object? _, MqttApplicationMessage message)
        {
            // Gateway 的鉴权列表响应没有 query_seq 字段，且部分固件会发布到
            // ucchip/up/sgw/{gatewayId} 或使用独立的上行序号，不能用请求序号过滤。
            if (!MatchesGatewayUpstreamTopic(message.Topic, gatewayId) ||
                !OtaMessageCodec.TryParseGatewayAuthListPage(message.GetPayloadAsUtf8(), out var page))
            {
                return;
            }
            pages.Writer.TryWrite(page);
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateGatewayAuthListQuery(gatewayId, sequence);
            await PublishAsync(
                new MqttApplicationMessage(request.Topic, System.Text.Encoding.UTF8.GetBytes(request.JsonPayload), request.QualityOfService),
                cancellationToken);

            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstTimeout.CancelAfter(_options.ResponseTimeout);
            var first = await pages.Reader.ReadAsync(firstTimeout.Token);
            AddExtenders(extenders, first);

            while (true)
            {
                using var quietTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quietTimeout.CancelAfter(_options.AuthListQuietPeriod);
                try
                {
                    AddExtenders(extenders, await pages.Reader.ReadAsync(quietTimeout.Token));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            return extenders.Values.OrderBy(item => item.ExtenderId).ToArray();
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
            pages.Writer.TryComplete();
        }
    }

    public async Task<GatewayBasicInfo> QueryGatewayBasicInfoAsync(
        string gatewayId,
        CancellationToken cancellationToken = default)
    {
        var sequence = NextSequence();
        var completion = new TaskCompletionSource<GatewayBasicInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, MqttApplicationMessage message)
        {
            if (!MatchesGatewayUpstreamTopic(message.Topic, gatewayId) ||
                !OtaMessageCodec.TryParseGatewayBasicInfo(
                    message.GetPayloadAsUtf8(),
                    out var info) ||
                info is null ||
                info.GatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture) != gatewayId)
            {
                return;
            }
            completion.TrySetResult(info);
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateGatewayBasicInfoQuery(gatewayId, sequence);
            await PublishAsync(
                new MqttApplicationMessage(
                    request.Topic,
                    System.Text.Encoding.UTF8.GetBytes(request.JsonPayload),
                    request.QualityOfService),
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ResponseTimeout);
            try
            {
                return await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("等待 Gateway 基础信息响应超时。");
            }
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
        }
    }

    public async Task<IReadOnlyList<ExtenderNodeDiscoveryResult>> DiscoverNodesAsync(
        string gatewayId,
        IEnumerable<uint> extenderIds,
        CancellationToken cancellationToken = default)
    {
        var tasks = extenderIds
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .Select(async extenderId =>
            {
                try
                {
                    var response = await ExecuteExtenderQueryAsync(
                        extenderId,
                        token => RequestNodeListAsync(gatewayId, extenderId, token),
                        cancellationToken);
                    var nodes = response.Nodes
                        .OrderByDescending(node => node.IsOnline)
                        .ThenBy(node => node.NodeId)
                        .ToArray();
                    return new ExtenderNodeDiscoveryResult(
                        extenderId,
                        nodes,
                        response.TotalCount,
                        null);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ExtenderNodeDiscoveryResult(extenderId, [], 0, exception.Message);
                }
            })
            .ToArray();
        if (tasks.Length == 0)
        {
            return [];
        }
        return await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyList<ExtenderStatusDiscoveryResult>> DiscoverExtenderStatusesAsync(
        string gatewayId,
        IEnumerable<uint> extenderIds,
        CancellationToken cancellationToken = default)
    {
        var tasks = extenderIds
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .Select(async extenderId =>
            {
                try
                {
                    var response = await ExecuteExtenderQueryAsync(
                        extenderId,
                        token => RequestExtenderStatusAsync(gatewayId, extenderId, token),
                        cancellationToken);
                    return new ExtenderStatusDiscoveryResult(extenderId, response, null);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ExtenderStatusDiscoveryResult(extenderId, null, exception.Message);
                }
            })
            .ToArray();
        if (tasks.Length == 0)
        {
            return [];
        }
        return await Task.WhenAll(tasks);
    }

    private async Task<T> ExecuteExtenderQueryAsync<T>(
        uint extenderId,
        Func<CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        var queryLock = _extenderQueryLocks.GetOrAdd(extenderId, static _ => new SemaphoreSlim(1, 1));
        await queryLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= _options.NodeGroupAttempts; attempt++)
            {
                try
                {
                    return await query(cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = exception;
                    if (attempt < _options.NodeGroupAttempts)
                    {
                        var drainPeriod = _options.ResponseDrainPeriod ?? TimeSpan.FromMilliseconds(500);
                        if (drainPeriod > TimeSpan.Zero)
                        {
                            await Task.Delay(drainPeriod, cancellationToken);
                        }
                    }
                }
            }
            throw new InvalidOperationException(
                $"Extender {extenderId} 查询重试后仍失败：{lastError?.Message}",
                lastError);
        }
        finally
        {
            queryLock.Release();
        }
    }

    private async Task<GatewayNodeList> RequestNodeListAsync(
        string gatewayId,
        uint extenderId,
        CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var completion = new TaskCompletionSource<GatewayNodeList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = new Dictionary<byte, GatewayNodeList>();
        var pageSync = new object();

        void Handler(object? _, MqttApplicationMessage message)
        {
            if (!MatchesGatewayUpstreamTopic(message.Topic, gatewayId))
            {
                return;
            }
            if (!OtaMessageCodec.TryParseAsyncNodeListResponse(
                    message.GetPayloadAsUtf8(),
                    extenderId,
                    out var response,
                    out var protocolError))
            {
                if (!string.IsNullOrWhiteSpace(protocolError))
                {
                    completion.TrySetException(new InvalidDataException(protocolError));
                }
                return;
            }
            if (response is null || response.ExtenderId != extenderId) return;
            lock (pageSync)
            {
                if (pages.Values.FirstOrDefault() is { } firstPage &&
                    (response.AsyncAddress != firstPage.AsyncAddress ||
                     response.PageCount != firstPage.PageCount ||
                     response.TotalCount != firstPage.TotalCount))
                {
                    completion.TrySetException(new InvalidDataException("0x0F 多页响应的地址、总页数或设备总数不一致。"));
                    return;
                }
                if (pages.TryGetValue(response.PageIndex, out var existingPage))
                {
                    if (!existingPage.Nodes.SequenceEqual(response.Nodes))
                    {
                        completion.TrySetException(new InvalidDataException($"0x0F 第 {response.PageIndex} 页重复响应内容不一致。"));
                    }
                    return;
                }
                pages.Add(response.PageIndex, response);
                if (pages.Count < response.PageCount) return;

                var orderedNodes = pages
                    .OrderBy(pair => pair.Key)
                    .SelectMany(pair => pair.Value.Nodes)
                    .ToArray();
                if (orderedNodes.Length != response.TotalCount)
                {
                    completion.TrySetException(new InvalidDataException(
                        $"0x0F 分页汇总数量错误：声明 {response.TotalCount} 项，实际收到 {orderedNodes.Length} 项。"));
                    return;
                }
                if (orderedNodes.Select(node => node.NodeId).Distinct().Count() != orderedNodes.Length)
                {
                    completion.TrySetException(new InvalidDataException("0x0F 多页响应包含重复 Node ID。"));
                    return;
                }
                completion.TrySetResult(new GatewayNodeList(
                    response.ExtenderId,
                    response.AsyncAddress,
                    orderedNodes,
                    0,
                    response.PageCount,
                    response.TotalCount));
            }
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateAsyncNodeListQuery(gatewayId, sequence, extenderId);
            await PublishAsync(
                new MqttApplicationMessage(request.Topic, System.Text.Encoding.UTF8.GetBytes(request.JsonPayload), request.QualityOfService),
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ResponseTimeout);
            try
            {
                return await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"等待 Extender {extenderId} 的 0x0F Node 列表响应超时。");
            }
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
        }
    }

    private async Task<GatewayExtenderStatus> RequestExtenderStatusAsync(
        string gatewayId,
        uint extenderId,
        CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var completion = new TaskCompletionSource<GatewayExtenderStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, MqttApplicationMessage message)
        {
            if (!MatchesGatewayUpstreamTopic(message.Topic, gatewayId))
            {
                return;
            }
            if (!OtaMessageCodec.TryParseAsyncStatusResponse(
                    message.GetPayloadAsUtf8(),
                    extenderId,
                    out var response,
                    out var protocolError))
            {
                if (!string.IsNullOrWhiteSpace(protocolError))
                {
                    completion.TrySetException(new InvalidDataException(protocolError));
                }
                return;
            }
            if (response is null) return;
            completion.TrySetResult(response);
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateAsyncStatusQuery(
                gatewayId,
                sequence,
                extenderId);
            await PublishAsync(
                new MqttApplicationMessage(
                    request.Topic,
                    System.Text.Encoding.UTF8.GetBytes(request.JsonPayload),
                    request.QualityOfService),
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ResponseTimeout);
            try
            {
                return await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 Extender {extenderId} 的 0x18 状态响应超时。");
            }
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
        }
    }

    private int NextSequence()
    {
        var value = Interlocked.Increment(ref _sequence);
        if (value > 0) return value;
        Interlocked.Exchange(ref _sequence, 1);
        return 1;
    }

    private async Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        await _mqtt.PublishAsync(message, cancellationToken);
        MessagePublished?.Invoke(this, message);
    }

    private static bool MatchesGatewayUpstreamTopic(string topic, string gatewayId)
    {
        var segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 4 &&
               segments[0] == "ucchip" &&
               segments[1] == "up" &&
               segments[2] == "sgw" &&
               segments[3] == gatewayId;
    }

    private static void AddExtenders(
        ConcurrentDictionary<uint, GatewayExtenderInfo> destination,
        IReadOnlyList<GatewayExtenderInfo> source)
    {
        foreach (var extender in source)
        {
            destination[extender.ExtenderId] = extender;
        }
    }
}
