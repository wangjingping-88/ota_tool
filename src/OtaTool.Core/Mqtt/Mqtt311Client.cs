using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace OtaTool.Core.Mqtt;

/// <summary>只实现 OTA 工具需要的 MQTT 3.1.1 CONNECT、PUBLISH、SUBSCRIBE、UNSUBSCRIBE、PING 与 QoS 0/1。</summary>
public sealed class Mqtt311Client : IMqttTransport
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private int _packetIdentifier;

    public bool IsConnected { get; private set; }

    public event EventHandler<MqttApplicationMessage>? MessageReceived;

    public event EventHandler<Exception>? ConnectionFaulted;

    public async Task ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(options.Host, options.Port, cancellationToken);
            Stream stream = tcpClient.GetStream();
            if (options.UseTls)
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) =>
                    options.AcceptAnyServerCertificate || errors == SslPolicyErrors.None);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, cancellationToken);
                stream = sslStream;
            }

            await SendPacketAsync(stream, BuildConnectPacket(options), cancellationToken);
            var connack = await ReadPacketAsync(stream, cancellationToken);
            if (connack.PacketType != 2 || connack.Body.Length != 2 || connack.Body[1] != 0)
            {
                throw new InvalidOperationException("MQTT Broker 拒绝连接。请检查账号、密码和协议版本。");
            }

            _tcpClient = tcpClient;
            _stream = stream;
            _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            IsConnected = true;
            _receiveTask = ReceiveLoopAsync(_connectionCancellation.Token);
            _keepAliveTask = KeepAliveLoopAsync(options.KeepAliveSeconds, _connectionCancellation.Token);
        }
        catch
        {
            await DisposeConnectionAsync();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_stream is not null && IsConnected)
            {
                try { await SendPacketAsync(_stream, new byte[] { 0xE0, 0x00 }, cancellationToken); } catch { }
            }
            await DisposeConnectionAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task SubscribeAsync(string topicFilter, byte qualityOfService = 1, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ValidateTopic(topicFilter, allowWildcards: true);
        if (qualityOfService > 1) throw new ArgumentOutOfRangeException(nameof(qualityOfService));
        var identifier = NextPacketIdentifier();
        var body = new ArrayBufferWriter<byte>();
        WriteUInt16(body, identifier);
        WriteUtf8(body, topicFilter);
        body.Write(new byte[] { qualityOfService });
        await SendPacketAsync(_stream!, BuildPacket(0x82, body.WrittenSpan), cancellationToken);
    }

    public async Task UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ValidateTopic(topicFilter, allowWildcards: true);
        var identifier = NextPacketIdentifier();
        var body = new ArrayBufferWriter<byte>();
        WriteUInt16(body, identifier);
        WriteUtf8(body, topicFilter);
        await SendPacketAsync(_stream!, BuildPacket(0xA2, body.WrittenSpan), cancellationToken);
    }

    public async Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ValidateTopic(message.Topic, allowWildcards: false);
        if (message.QualityOfService > 1) throw new ArgumentOutOfRangeException(nameof(message));
        var body = new ArrayBufferWriter<byte>();
        WriteUtf8(body, message.Topic);
        if (message.QualityOfService == 1)
        {
            WriteUInt16(body, NextPacketIdentifier());
        }
        body.Write(message.Payload.Span);
        var flags = (byte)(0x30 | (message.Retain ? 0x01 : 0) | (message.QualityOfService << 1));
        await SendPacketAsync(_stream!, BuildPacket(flags, body.WrittenSpan), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _sendLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                var packet = await ReadPacketAsync(_stream, cancellationToken);
                switch (packet.PacketType)
                {
                    case 3:
                        await HandlePublishAsync(packet, cancellationToken);
                        break;
                    case 6:
                        if (packet.Body.Length == 2) await SendPacketAsync(_stream, new byte[] { 0x70, 0x02, packet.Body[0], packet.Body[1] }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            IsConnected = false;
            ConnectionFaulted?.Invoke(this, exception);
        }
    }

    private async Task HandlePublishAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        var body = packet.Body;
        if (body.Length < 2) return;
        var topicLength = (body[0] << 8) | body[1];
        if (topicLength + 2 > body.Length) return;
        var topic = Encoding.UTF8.GetString(body, 2, topicLength);
        var qos = (byte)((packet.Header >> 1) & 0x03);
        var offset = 2 + topicLength;
        ushort packetIdentifier = 0;
        if (qos > 0)
        {
            if (offset + 2 > body.Length) return;
            packetIdentifier = (ushort)((body[offset] << 8) | body[offset + 1]);
            offset += 2;
        }
        MessageReceived?.Invoke(this, new MqttApplicationMessage(topic, body[offset..], qos, (packet.Header & 0x01) != 0));
        if (qos == 1 && _stream is not null)
        {
            await SendPacketAsync(_stream, new byte[] { 0x40, 0x02, (byte)(packetIdentifier >> 8), (byte)packetIdentifier }, cancellationToken);
        }
    }

    private async Task KeepAliveLoopAsync(ushort keepAliveSeconds, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, keepAliveSeconds / 2)), cancellationToken);
                if (_stream is not null && IsConnected)
                {
                    await SendPacketAsync(_stream, new byte[] { 0xC0, 0x00 }, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            IsConnected = false;
            ConnectionFaulted?.Invoke(this, exception);
        }
    }

    private async Task SendPacketAsync(Stream stream, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(packet, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] BuildConnectPacket(MqttClientOptions options)
    {
        var body = new ArrayBufferWriter<byte>();
        WriteUtf8(body, "MQTT");
        body.Write(new byte[] { 0x04 });
        var flags = (byte)0x02;
        if (!string.IsNullOrEmpty(options.UserName)) flags |= 0x80;
        if (!string.IsNullOrEmpty(options.Password)) flags |= 0x40;
        body.Write(new byte[] { flags });
        WriteUInt16(body, options.KeepAliveSeconds);
        WriteUtf8(body, options.ClientId);
        if (!string.IsNullOrEmpty(options.UserName)) WriteUtf8(body, options.UserName);
        if (!string.IsNullOrEmpty(options.Password)) WriteUtf8(body, options.Password);
        return BuildPacket(0x10, body.WrittenSpan);
    }

    private static byte[] BuildPacket(byte header, ReadOnlySpan<byte> body)
    {
        var writer = new ArrayBufferWriter<byte>(body.Length + 5);
        writer.Write(new byte[] { header });
        WriteRemainingLength(writer, body.Length);
        writer.Write(body);
        return writer.WrittenSpan.ToArray();
    }

    private static async Task<MqttPacket> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = new byte[1];
        await ReadExactlyAsync(stream, first, cancellationToken);
        var remainingLength = await ReadRemainingLengthAsync(stream, cancellationToken);
        var body = new byte[remainingLength];
        if (remainingLength > 0) await ReadExactlyAsync(stream, body, cancellationToken);
        return new MqttPacket(first[0], body);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var consumed = 0;
        while (consumed < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[consumed..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("MQTT 连接已关闭。");
            consumed += read;
        }
    }

    private static async Task<int> ReadRemainingLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var multiplier = 1;
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var buffer = new byte[1];
            await ReadExactlyAsync(stream, buffer, cancellationToken);
            value += (buffer[0] & 0x7F) * multiplier;
            if ((buffer[0] & 0x80) == 0) return value;
            multiplier *= 128;
        }
        throw new InvalidDataException("MQTT Remaining Length 非法。");
    }

    private static void WriteRemainingLength(IBufferWriter<byte> writer, int value)
    {
        if (value < 0 || value > 268435455) throw new ArgumentOutOfRangeException(nameof(value));
        do
        {
            var encoded = value % 128;
            value /= 128;
            if (value > 0) encoded |= 0x80;
            writer.Write([(byte)encoded]);
        } while (value > 0);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        WriteUInt16(writer, (ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value) => writer.Write([(byte)(value >> 8), (byte)value]);

    private static void ValidateTopic(string topic, bool allowWildcards)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (!allowWildcards && (topic.Contains('#') || topic.Contains('+'))) throw new ArgumentException("发布 Topic 不能包含通配符。", nameof(topic));
    }

    private ushort NextPacketIdentifier()
    {
        var value = Interlocked.Increment(ref _packetIdentifier);
        return (ushort)((value - 1) % ushort.MaxValue + 1);
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _stream is null) throw new InvalidOperationException("MQTT 尚未连接。");
    }

    private async Task DisposeConnectionAsync()
    {
        IsConnected = false;
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        if (_stream is not null) await _stream.DisposeAsync();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    private readonly record struct MqttPacket(byte Header, byte[] Body)
    {
        public byte PacketType => (byte)(Header >> 4);
    }
}
