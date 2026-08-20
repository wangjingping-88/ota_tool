using MQTTnet;
using MQTTnet.Server;

namespace OtaTool.Core.Mqtt;

public sealed record EmbeddedMqttBrokerOptions(int Port = 1883, string? UserName = null, string? Password = null);

public sealed class EmbeddedMqttBroker : IAsyncDisposable
{
    private readonly MqttFactory _factory = new();
    private MqttServer? _server;

    public bool IsRunning => _server?.IsStarted == true;

    public async Task StartAsync(EmbeddedMqttBrokerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (IsRunning) return;
        var builder = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(options.Port);
        var server = _factory.CreateMqttServer(builder.Build());
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            server.ValidatingConnectionAsync += args =>
            {
                args.ReasonCode = args.UserName == options.UserName && args.Password == options.Password
                    ? MQTTnet.Protocol.MqttConnectReasonCode.Success
                    : MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
        }
        await server.StartAsync();
        _server = server;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null) return;
        await _server.StopAsync();
        _server.Dispose();
        _server = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
