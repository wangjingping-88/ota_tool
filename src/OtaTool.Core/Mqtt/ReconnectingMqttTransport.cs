namespace OtaTool.Core.Mqtt;

public sealed class ReconnectingMqttTransport : IMqttTransport
{
    private readonly Func<IMqttTransport> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, byte> _subscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private IMqttTransport? _current;
    private MqttClientOptions? _options;
    private Task? _reconnectTask;

    public ReconnectingMqttTransport(Func<IMqttTransport> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public bool IsConnected => _current?.IsConnected == true;

    public event EventHandler<MqttApplicationMessage>? MessageReceived;

    public event EventHandler<string>? ConnectionStateChanged;

    public async Task ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        await EnsureConnectedAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _options = null;
        _subscriptions.Clear();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is not null)
            {
                Unhook(_current);
                await _current.DisconnectAsync(cancellationToken);
                await _current.DisposeAsync();
                _current = null;
            }
            ConnectionStateChanged?.Invoke(this, "MQTT 已断开。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubscribeAsync(string topicFilter, byte qualityOfService = 1, CancellationToken cancellationToken = default)
    {
        _subscriptions[topicFilter] = qualityOfService;
        await EnsureConnectedAsync(cancellationToken);
        await _current!.SubscribeAsync(topicFilter, qualityOfService, cancellationToken);
    }

    public async Task UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        _subscriptions.Remove(topicFilter);
        if (_current?.IsConnected == true)
        {
            await _current.UnsubscribeAsync(topicFilter, cancellationToken);
        }
    }

    public async Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _current!.PublishAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await DisconnectAsync();
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        var options = _options ?? throw new InvalidOperationException("尚未配置 MQTT 连接参数。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) return;
            if (_current is not null)
            {
                Unhook(_current);
                await _current.DisposeAsync();
            }
            var transport = _factory();
            Hook(transport);
            await transport.ConnectAsync(options, cancellationToken);
            _current = transport;
            foreach (var (filter, qos) in _subscriptions)
            {
                await transport.SubscribeAsync(filter, qos, cancellationToken);
            }
            ConnectionStateChanged?.Invoke(this, "MQTT 已连接。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Hook(IMqttTransport transport)
    {
        transport.MessageReceived += ForwardMessage;
        if (transport is Mqtt311Client client) client.ConnectionFaulted += OnConnectionFaulted;
    }

    private void Unhook(IMqttTransport transport)
    {
        transport.MessageReceived -= ForwardMessage;
        if (transport is Mqtt311Client client) client.ConnectionFaulted -= OnConnectionFaulted;
    }

    private void ForwardMessage(object? sender, MqttApplicationMessage message) => MessageReceived?.Invoke(this, message);

    private void OnConnectionFaulted(object? sender, Exception exception)
    {
        ConnectionStateChanged?.Invoke(this, $"MQTT 连接中断：{exception.Message}，正在重连。");
        if (_options is not null && (_reconnectTask is null || _reconnectTask.IsCompleted))
        {
            _reconnectTask = ReconnectAsync();
        }
    }

    private async Task ReconnectAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_shutdown.IsCancellationRequested && _options is not null && !IsConnected)
        {
            try
            {
                await Task.Delay(delay, _shutdown.Token);
                await EnsureConnectedAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                ConnectionStateChanged?.Invoke(this, $"MQTT 重连失败：{exception.Message}");
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
