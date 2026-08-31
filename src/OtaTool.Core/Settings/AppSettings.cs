using System.Text.Json;
using OtaTool.Core.Models;

namespace OtaTool.Core.Settings;

public sealed class AppSettings
{
    public string ActiveMode { get; init; } = "EcoLink";

    public IReadOnlyDictionary<string, ModeWorkspaceSettings> ModeWorkspaces { get; init; }
        = new Dictionary<string, ModeWorkspaceSettings>(StringComparer.OrdinalIgnoreCase);

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

    public string SelectedTaskType { get; init; } = "网关升级";

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

    public IReadOnlyList<string> GatewayIdHistory { get; init; } = [];

    public int CycleRounds { get; init; } = 1;

    public string CycleIntervalMode { get; init; } = "固定间隔";

    public int CycleFixedIntervalSeconds { get; init; }

    public int CycleRandomMinimumSeconds { get; init; }

    public int CycleRandomMaximumSeconds { get; init; }

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

    public IReadOnlyList<OtaTestPlanTemplate> TestPlanTemplates { get; init; } = [];

    public Guid? SelectedTestPlanId { get; init; }
}

/// <summary>
/// 按协议模式保存的桌面工作区。密码等敏感值仍由 Windows Credential Manager 分模式保存。
/// </summary>
public sealed class ModeWorkspaceSettings
{
    public string SelectedPageName { get; set; } = "MQTT 配置";
    public string MqttHost { get; set; } = "117.172.29.2";
    public int MqttPort { get; set; } = 36106;
    public string HttpRootDirectory { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 8080;
    public bool HttpUsesLocalServer { get; set; } = true;
    public string PublicHttpBaseUrl { get; set; } = string.Empty;
    public bool MqttUseTls { get; set; }
    public bool MqttAcceptAnyServerCertificate { get; set; }
    public string MqttUserName { get; set; } = string.Empty;
    public bool MqttClientUsesLocalBroker { get; set; } = true;
    public int LocalBrokerPort { get; set; } = 1883;
    public string LocalBrokerUserName { get; set; } = string.Empty;
    public string SftpHost { get; set; } = "117.172.29.2";
    public int SftpPort { get; set; } = 36112;
    public string SftpUserName { get; set; } = "root";
    public string SftpPrivateKeyPath { get; set; } = string.Empty;
    public string SftpRemoteDirectory { get; set; } = "/opt/www/static/download/";
    public string SftpPublicBaseUrl { get; set; } = string.Empty;
    public string SftpHostKeySha256 { get; set; } = string.Empty;
    public string LogAnalyzerExecutablePath { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public string SelectedTaskType { get; set; } = "网关升级";
    public string OldVersion { get; set; } = "V1.2.3";
    public string NewVersion { get; set; } = "V1.3.0";
    public string ForwardPatchName { get; set; } = "a-to-b";
    public string ReversePatchName { get; set; } = "b-to-a";
    public bool IsSpecifiedTarget { get; set; } = true;
    public string TargetIdList { get; set; } = string.Empty;
    public int NodeType { get; set; } = 5;
    public IReadOnlyList<NodeTypeDefinitionSettings> CustomNodeTypes { get; set; } = [];
    public string NodeTargetsText { get; set; } = string.Empty;
    public string GatewayId { get; set; } = string.Empty;
    public IReadOnlyList<string> GatewayIdHistory { get; set; } = [];
    public int CycleRounds { get; set; } = 1;
    public string CycleIntervalMode { get; set; } = "固定间隔";
    public int CycleFixedIntervalSeconds { get; set; }
    public int CycleRandomMinimumSeconds { get; set; }
    public int CycleRandomMaximumSeconds { get; set; }
    public long NodePatchLimit { get; set; } = 0xD000;
    public long AsyncPatchLimit { get; set; } = 0x2F000;
    public long SyncPatchLimit { get; set; } = PatchCapacityPolicy.SyncPatchLimit;
    public long GatewayPatchLimit { get; set; } = PatchCapacityPolicy.GatewayPatchLimit;
    public int DiscoveryFreshnessMinutes { get; set; } = 30;
    public int MinimumNodeRssi { get; set; } = -100;
    public string SelectedUpgradePatchPath { get; set; } = string.Empty;
    public string SelectedReverseUpgradePatchPath { get; set; } = string.Empty;
    public IReadOnlyList<DiscoveredExtenderSettings> DiscoveredExtenders { get; set; } = [];
    public IReadOnlyList<DiscoveredNodeGroupSettings> DiscoveredNodeGroups { get; set; } = [];
    public DateTimeOffset? NodeDiscoveryCompletedAt { get; set; }
    public bool ShowArchivedReports { get; set; }
    public IReadOnlyList<OtaTestPlanTemplate> TestPlanTemplates { get; set; } = [];
    public Guid? SelectedTestPlanId { get; set; }

    public ModeWorkspaceSettings Copy() => (ModeWorkspaceSettings)MemberwiseClone();

    public static ModeWorkspaceSettings FromLegacy(AppSettings settings) => new()
    {
        MqttHost = settings.MqttHost,
        MqttPort = settings.MqttPort,
        HttpRootDirectory = settings.HttpRootDirectory,
        HttpPort = settings.HttpPort,
        HttpUsesLocalServer = settings.HttpUsesLocalServer,
        PublicHttpBaseUrl = settings.PublicHttpBaseUrl,
        MqttUseTls = settings.MqttUseTls,
        MqttAcceptAnyServerCertificate = settings.MqttAcceptAnyServerCertificate,
        MqttUserName = settings.MqttUserName,
        MqttClientUsesLocalBroker = settings.MqttClientUsesLocalBroker,
        LocalBrokerPort = settings.LocalBrokerPort,
        LocalBrokerUserName = settings.LocalBrokerUserName,
        SftpHost = settings.SftpHost,
        SftpPort = settings.SftpPort,
        SftpUserName = settings.SftpUserName,
        SftpPrivateKeyPath = settings.SftpPrivateKeyPath,
        SftpRemoteDirectory = settings.SftpRemoteDirectory,
        SftpPublicBaseUrl = settings.SftpPublicBaseUrl,
        SftpHostKeySha256 = settings.SftpHostKeySha256,
        LogAnalyzerExecutablePath = settings.LogAnalyzerExecutablePath,
        LogDirectory = settings.LogDirectory,
        SelectedTaskType = settings.SelectedTaskType,
        OldVersion = settings.OldVersion,
        NewVersion = settings.NewVersion,
        ForwardPatchName = settings.ForwardPatchName,
        ReversePatchName = settings.ReversePatchName,
        IsSpecifiedTarget = settings.IsSpecifiedTarget,
        TargetIdList = settings.TargetIdList,
        NodeType = settings.NodeType,
        CustomNodeTypes = settings.CustomNodeTypes,
        NodeTargetsText = settings.NodeTargetsText,
        GatewayId = settings.GatewayId,
        GatewayIdHistory = settings.GatewayIdHistory,
        CycleRounds = settings.CycleRounds,
        CycleIntervalMode = settings.CycleIntervalMode,
        CycleFixedIntervalSeconds = settings.CycleFixedIntervalSeconds,
        CycleRandomMinimumSeconds = settings.CycleRandomMinimumSeconds,
        CycleRandomMaximumSeconds = settings.CycleRandomMaximumSeconds,
        NodePatchLimit = settings.NodePatchLimit,
        AsyncPatchLimit = settings.AsyncPatchLimit,
        SyncPatchLimit = settings.SyncPatchLimit,
        GatewayPatchLimit = settings.GatewayPatchLimit,
        DiscoveryFreshnessMinutes = settings.DiscoveryFreshnessMinutes,
        MinimumNodeRssi = settings.MinimumNodeRssi,
        SelectedUpgradePatchPath = settings.SelectedUpgradePatchPath,
        SelectedReverseUpgradePatchPath = settings.SelectedReverseUpgradePatchPath,
        DiscoveredExtenders = settings.DiscoveredExtenders,
        DiscoveredNodeGroups = settings.DiscoveredNodeGroups,
        NodeDiscoveryCompletedAt = settings.NodeDiscoveryCompletedAt,
        TestPlanTemplates = settings.TestPlanTemplates,
        SelectedTestPlanId = settings.SelectedTestPlanId,
    };
}

public sealed record DiscoveredExtenderSettings(
    uint ExtenderId,
    string Detail,
    byte DeviceType,
    byte SoftwareVersion,
    bool IsSelected,
    byte? AsyncSoftwareVersion = null,
    ushort? AsyncAddress = null,
    int? SyncRssi = null,
    sbyte? SyncSnr = null,
    byte? OnlineCount = null,
    byte? TotalCount = null);

public sealed record NodeTypeDefinitionSettings(int Value, string Name);

public sealed record DiscoveredNodeSettings(ushort NodeId, byte NodeType, byte SoftwareVersion, int Rssi, bool IsSelected);

public sealed record DiscoveredNodeGroupSettings(
    uint ExtenderId,
    IReadOnlyList<DiscoveredNodeSettings> Nodes,
    string Error,
    int? ReportedCount = null);

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

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        using var stream = File.OpenRead(_settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions)
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
