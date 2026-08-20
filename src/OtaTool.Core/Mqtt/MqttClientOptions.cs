namespace OtaTool.Core.Mqtt;

public sealed record MqttClientOptions(
    string Host,
    int Port,
    string ClientId,
    bool UseTls = false,
    string? UserName = null,
    string? Password = null,
    bool AcceptAnyServerCertificate = false,
    ushort KeepAliveSeconds = 30)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        if (Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port));
        }
        if (KeepAliveSeconds == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveSeconds));
        }
    }
}

public sealed record MqttApplicationMessage(string Topic, ReadOnlyMemory<byte> Payload, byte QualityOfService = 0, bool Retain = false)
{
    public string GetPayloadAsUtf8() => System.Text.Encoding.UTF8.GetString(Payload.Span);
}

public interface IMqttTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<MqttApplicationMessage>? MessageReceived;

    Task ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SubscribeAsync(string topicFilter, byte qualityOfService = 1, CancellationToken cancellationToken = default);

    Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default);
}
