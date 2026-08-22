using System.Collections.Concurrent;
using System.Threading.Channels;
using OtaTool.Core.Mqtt;
using OtaTool.Core.Protocols;

namespace OtaTool.Core.Discovery;

public sealed record ExtenderNodeDiscoveryResult(
    uint ExtenderId,
    IReadOnlyList<GatewayNodeInfo> Nodes,
    string? Error)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(Error);
}

public sealed record ExtenderAsyncVersionDiscoveryResult(
    uint ExtenderId,
    byte? SoftwareVersion,
    string? Error)
{
    public bool IsSuccess => SoftwareVersion is >= 1 and <= 254 && string.IsNullOrWhiteSpace(Error);
}

public sealed record DeviceDiscoveryOptions(
    TimeSpan ResponseTimeout,
    TimeSpan AuthListQuietPeriod,
    int NodeGroupAttempts = 2)
{
    public static DeviceDiscoveryOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(500));
}

public sealed class DeviceDiscoveryService
{
    private readonly IMqttTransport _mqtt;
    private readonly DeviceDiscoveryOptions _options;
    private int _sequence = Math.Max(1, Environment.TickCount & 0x3fffffff);

    public DeviceDiscoveryService(IMqttTransport mqtt, DeviceDiscoveryOptions? options = null)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _options = options ?? DeviceDiscoveryOptions.Default;
        if (_options.ResponseTimeout <= TimeSpan.Zero ||
            _options.AuthListQuietPeriod <= TimeSpan.Zero ||
            _options.NodeGroupAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

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
            await _mqtt.PublishAsync(
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
            await _mqtt.PublishAsync(
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
                    return new ExtenderNodeDiscoveryResult(
                        extenderId,
                        await QueryNodeGroupWithRetryAsync(gatewayId, extenderId, cancellationToken),
                        null);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ExtenderNodeDiscoveryResult(extenderId, [], exception.Message);
                }
            })
            .ToArray();
        if (tasks.Length == 0)
        {
            return [];
        }
        return await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyList<ExtenderAsyncVersionDiscoveryResult>> DiscoverAsyncVersionsAsync(
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
                    var response = await RequestAsyncVersionAsync(
                        gatewayId,
                        extenderId,
                        cancellationToken);
                    if (!response.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExtenderAsyncVersionDiscoveryResult(
                            extenderId,
                            null,
                            $"Gateway 返回 {response.Result}/{response.Reason}。");
                    }
                    return new ExtenderAsyncVersionDiscoveryResult(
                        extenderId,
                        response.SoftwareVersion,
                        null);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ExtenderAsyncVersionDiscoveryResult(extenderId, null, exception.Message);
                }
            })
            .ToArray();
        if (tasks.Length == 0)
        {
            return [];
        }
        return await Task.WhenAll(tasks);
    }

    private async Task<IReadOnlyList<GatewayNodeInfo>> QueryNodeGroupWithRetryAsync(
        string gatewayId,
        uint extenderId,
        CancellationToken cancellationToken)
    {
        Exception? firstError = null;
        for (var attempt = 0; attempt < _options.NodeGroupAttempts; attempt++)
        {
            try
            {
                return await QueryNodeGroupOnceAsync(gatewayId, extenderId, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                firstError ??= exception;
            }
        }
        throw new InvalidOperationException(
            $"Extender {extenderId} 的 Node 分页查询重试后仍失败：{firstError?.Message}");
    }

    private async Task<IReadOnlyList<GatewayNodeInfo>> QueryNodeGroupOnceAsync(
        string gatewayId,
        uint extenderId,
        CancellationToken cancellationToken)
    {
        var pages = new Dictionary<int, GatewayNodeListPage>();
        var first = await RequestNodePageAsync(gatewayId, extenderId, 0, cancellationToken);
        ValidatePage(first, extenderId, 0, null);
        pages[0] = first;
        for (var pageIndex = 1; pageIndex < first.PageCount; pageIndex++)
        {
            var page = await RequestNodePageAsync(gatewayId, extenderId, pageIndex, cancellationToken);
            ValidatePage(page, extenderId, pageIndex, first);
            pages[pageIndex] = page;
        }

        var nodesById = new Dictionary<ushort, GatewayNodeInfo>();
        foreach (var node in pages.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.Nodes))
        {
            if (nodesById.TryGetValue(node.NodeId, out var existing) && existing.NodeType != node.NodeType)
            {
                throw new InvalidDataException($"Node {node.NodeId} 在分页中出现冲突类型。");
            }
            nodesById[node.NodeId] = node;
        }
        var nodes = nodesById.Values.OrderBy(node => node.NodeId).ToArray();
        if (nodes.Length != first.TotalCount)
        {
            throw new InvalidDataException(
                $"分页总数不一致，声明 {first.TotalCount}，实际 {nodes.Length}。");
        }

        // 部分固件会把注册表中的空槽或失效记录一并放进 Node 列表，
        // 这类记录的版本和 RSSI 都为 0，不能作为真实在线 Node 提供给升级任务。
        return nodes
            .Where(node => node.SoftwareVersion != 0 || node.Rssi != 0)
            .ToArray();
    }

    private async Task<GatewayNodeListPage> RequestNodePageAsync(
        string gatewayId,
        uint extenderId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var completion = new TaskCompletionSource<GatewayNodeListPage>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, MqttApplicationMessage message)
        {
            if (!MatchesUpstreamTopic(message.Topic, gatewayId, sequence) ||
                !OtaMessageCodec.TryParseNodeListPage(message.GetPayloadAsUtf8(), out var page) ||
                page is null || page.QuerySequence != sequence)
            {
                return;
            }
            completion.TrySetResult(page);
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateNodeListQuery(gatewayId, sequence, extenderId, pageIndex);
            await _mqtt.PublishAsync(
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
                throw new TimeoutException($"等待 Extender {extenderId} 的第 {pageIndex + 1} 页 Node 列表响应超时。");
            }
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
        }
    }

    private async Task<GatewayAsyncVersion> RequestAsyncVersionAsync(
        string gatewayId,
        uint extenderId,
        CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var completion = new TaskCompletionSource<GatewayAsyncVersion>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, MqttApplicationMessage message)
        {
            if (!MatchesUpstreamTopic(message.Topic, gatewayId, sequence) ||
                !OtaMessageCodec.TryParseAsyncVersionResponse(
                    message.GetPayloadAsUtf8(),
                    out var response) ||
                response is null ||
                response.QuerySequence != sequence ||
                response.ExtenderId != extenderId)
            {
                return;
            }
            completion.TrySetResult(response);
        }

        _mqtt.MessageReceived += Handler;
        try
        {
            var request = OtaMessageCodec.CreateAsyncVersionQuery(
                gatewayId,
                sequence,
                extenderId);
            await _mqtt.PublishAsync(
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
                    $"等待 Extender {extenderId} 的异步板版本响应超时。");
            }
        }
        finally
        {
            _mqtt.MessageReceived -= Handler;
        }
    }

    private static void ValidatePage(
        GatewayNodeListPage page,
        uint extenderId,
        int pageIndex,
        GatewayNodeListPage? first)
    {
        if (!page.Result.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Gateway 返回 {page.Result}/{page.Reason}。");
        }
        if (page.ExtenderId != extenderId || page.PageIndex != pageIndex ||
            page.PageCount is < 1 or > 5 || page.TotalCount is < 0 or > 256 ||
            page.Nodes.Count > 56 || page.Nodes.Any(node => node.NodeId == 0))
        {
            throw new InvalidDataException("Node 分页字段非法。");
        }
        var expectedPageCount = Math.Max(1, (page.TotalCount + 55) / 56);
        var expectedItemCount = Math.Min(56, Math.Max(0, page.TotalCount - pageIndex * 56));
        if (page.PageCount != expectedPageCount || page.Nodes.Count != expectedItemCount)
        {
            throw new InvalidDataException("Node 分页数量与元数据不一致。");
        }
        if (first is not null &&
            (page.PageCount != first.PageCount || page.TotalCount != first.TotalCount))
        {
            throw new InvalidDataException("Node 分页元数据不一致。");
        }
    }

    private int NextSequence()
    {
        var value = Interlocked.Increment(ref _sequence);
        if (value > 0) return value;
        Interlocked.Exchange(ref _sequence, 1);
        return 1;
    }

    private static bool MatchesUpstreamTopic(string topic, string gatewayId, int sequence)
    {
        var segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 5 &&
               segments[0] == "ucchip" &&
               segments[1] == "up" &&
               segments[2] == "sgw" &&
               segments[3] == gatewayId &&
               segments[^1] == sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
