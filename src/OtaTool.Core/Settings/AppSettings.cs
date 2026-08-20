using System.Text.Json;
using OtaTool.Core.Models;

namespace OtaTool.Core.Settings;

public sealed class AppSettings
{
    public string MqttHost { get; init; } = "117.172.29.2";

    public int MqttPort { get; init; } = 36106;

    public string HttpRootDirectory { get; init; } = string.Empty;

    public int HttpPort { get; init; } = 8080;

    public bool HttpUsesLocalServer { get; init; } = true;

    public string PublicHttpBaseUrl { get; init; } = string.Empty;

    public bool MqttUseTls { get; init; }

    public bool MqttAcceptAnyServerCertificate { get; init; }

    public string MqttUserName { get; init; } = string.Empty;

    public bool MqttClientUsesLocalBroker { get; init; } = true;

    public int LocalBrokerPort { get; init; } = 1883;

    public string LocalBrokerUserName { get; init; } = string.Empty;

    public string SftpHost { get; init; } = "117.172.29.2";

    public int SftpPort { get; init; } = 36112;

    public string SftpUserName { get; init; } = "root";

    public string SftpPrivateKeyPath { get; init; } = string.Empty;

    public string SftpRemoteDirectory { get; init; } = "/opt/www/static/download/";

    public string SftpPublicBaseUrl { get; init; } = string.Empty;

    public string SftpHostKeySha256 { get; init; } = string.Empty;

    public string LogAnalyzerExecutablePath { get; init; } = string.Empty;

    public string LogDirectory { get; init; } = string.Empty;

    public string SelectedTaskType { get; init; } = "Gateway 升级";

    public string OldVersion { get; init; } = "V1.2.3";

    public string NewVersion { get; init; } = "V1.3.0";

    public string ForwardPatchName { get; init; } = "a-to-b";

    public string ReversePatchName { get; init; } = "b-to-a";

    public bool IsSpecifiedTarget { get; init; } = true;

    public string TargetIdList { get; init; } = string.Empty;

    public int NodeType { get; init; } = 5;

    public IReadOnlyList<NodeTypeDefinitionSettings> CustomNodeTypes { get; init; } = [];

    public string NodeTargetsText { get; init; } = string.Empty;

    public string GatewayId { get; init; } = string.Empty;

    public int CycleRounds { get; init; } = 1;

    public long NodePatchLimit { get; init; } = 0xD000;

    public long AsyncPatchLimit { get; init; } = 0x2F000;

    public long SyncPatchLimit { get; init; } = PatchCapacityPolicy.SyncPatchLimit;

    public long GatewayPatchLimit { get; init; } = PatchCapacityPolicy.GatewayPatchLimit;

    public int DiscoveryFreshnessMinutes { get; init; } = 30;

    public int MinimumNodeRssi { get; init; } = -100;

    public string SelectedUpgradePatchPath { get; init; } = string.Empty;

    public string SelectedReverseUpgradePatchPath { get; init; } = string.Empty;

    public IReadOnlyList<DiscoveredExtenderSettings> DiscoveredExtenders { get; init; } = [];

    public IReadOnlyList<DiscoveredNodeGroupSettings> DiscoveredNodeGroups { get; init; } = [];

    public DateTimeOffset? NodeDiscoveryCompletedAt { get; init; }
}

public sealed record DiscoveredExtenderSettings(
    uint ExtenderId,
    string Detail,
    byte DeviceType,
    byte SoftwareVersion,
    bool IsSelected);

public sealed record NodeTypeDefinitionSettings(int Value, string Name);

public sealed record DiscoveredNodeSettings(ushort NodeId, byte NodeType, byte SoftwareVersion, sbyte Rssi, bool IsSelected);

public sealed record DiscoveredNodeGroupSettings(
    uint ExtenderId,
    IReadOnlyList<DiscoveredNodeSettings> Nodes,
    string Error);

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
            ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_settingsPath}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
        }

        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
