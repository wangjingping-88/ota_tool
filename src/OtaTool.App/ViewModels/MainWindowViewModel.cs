using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OtaTool.Core.Analysis;
using OtaTool.Core.Diff;
using OtaTool.Core.Discovery;
using OtaTool.Core.Execution;
using OtaTool.Core.Http;
using OtaTool.Core.Mqtt;
using OtaTool.Core.Models;
using OtaTool.Core.Publishing;
using OtaTool.Core.Protocols;
using OtaTool.Core.Reports;
using OtaTool.Core.Security;
using OtaTool.Core.Settings;
using OtaTool.Update;

namespace OtaTool.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    public event EventHandler? CloseApplicationRequested;

    private readonly HttpRangeServer _httpRangeServer = new();
    private readonly EmbeddedMqttBroker _embeddedBroker = new();
    private readonly ReconnectingMqttTransport _mqtt = new(() => new Mqtt311Client());
    private readonly DeviceDiscoveryService _deviceDiscovery;
    private readonly SqliteReportStore _reportStore;
    private readonly JsonSettingsStore _settingsStore;
    private readonly AppSettings _startupSettings;
    private readonly ISecretStore _secretStore = new WindowsCredentialStore();
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private readonly Task _initializationTask;
    private CancellationTokenSource? _settingsAutoSaveCancellation;
    private Task _settingsAutoSaveTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _settingsLoaded;
    private bool _isEcoLink = true;
    private const string EcoLinkModeKey = "EcoLink";
    private const string TraditionalModeKey = "Traditional";
    private const string GatewayTaskType = "网关升级";
    private const string SyncTaskType = "拓展器-同步升级";
    private const string AsyncTaskType = "拓展器-异步升级";
    private const string NodeTaskType = "节点升级";
    private readonly Dictionary<string, ModeWorkspaceSettings> _modeWorkspaces = new(StringComparer.OrdinalIgnoreCase)
    {
        [EcoLinkModeKey] = new(),
        [TraditionalModeKey] = new(),
    };
    private string _ecoLinkSelectedTaskType = GatewayTaskType;
    private string _traditionalSelectedTaskType = GatewayTaskType;
    private readonly UpgradeModeUiState _ecoLinkUpgradeUiState = UpgradeModeUiState.CreateEcoLink();
    private readonly UpgradeModeUiState _traditionalUpgradeUiState = UpgradeModeUiState.CreateTraditional();
    private bool _restoringModeWorkspace;
    private bool _isSpecifiedTarget = true;
    private NavigationItem? _selectedPage;
    private string _globalLogText = string.Empty;
    private string _selectedTaskType = GatewayTaskType;
    private string _taskStatusMessage = "当前任务：空闲  · 请选择 Patch 后启动升级";
    private string _patchPath = string.Empty;
    private string _importedPatchPath = string.Empty;
    private PatchSelection? _selectedUpgradePatch;
    private PackageManifest? _selectedPatchManifest;
    private PatchSelection? _selectedReverseUpgradePatch;
    private PatchSelection? _selectedRestorePatch;
    private string _selectedPatchRestoreDirection = "A → B";
    private readonly Dictionary<string, PatchSelection> _patchCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publishedPatchKeys = new(StringComparer.OrdinalIgnoreCase);
    private PatchDialogAction _patchDialogAction;
    private IReadOnlyList<PatchSelection> _pendingPublication = [];
    private PatchSelection? _pendingDeletion;
    private ReportListItem? _pendingReportDeletion;
    private OtaTask? _pendingUpgradeTask;
    private IOtaProtocolProfile? _pendingUpgradeProfile;
    private OtaTask? _pendingCycleForwardTask;
    private OtaTask? _pendingCycleReverseTask;
    private IOtaProtocolProfile? _pendingCycleProfile;
    private OtaCycleIntervalOptions? _pendingCycleInterval;
    private int _pendingCycleRounds;
    private bool _isUpgradeStartInProgress;
    private bool _isPatchDialogOpen;
    private string _patchDialogTitle = string.Empty;
    private string _patchDialogMessage = string.Empty;
    private string _patchDialogConfirmText = "确认";
    private string _dialogResultStampText = string.Empty;
    private string _dialogResultStampColor = "#159E68";
    private long _importedPatchLength;
    private string _importedPatchMd5 = string.Empty;
    private string _importedPatchSha256 = string.Empty;
    private string _patchOutputDirectory = GetHttpRoot();
    private string _oldImagePath = string.Empty;
    private string _newImagePath = string.Empty;
    private string _oldImageSha256 = string.Empty;
    private string _newImageSha256 = string.Empty;
    private FirmwareIdentity? _oldFirmwareIdentity;
    private FirmwareIdentity? _newFirmwareIdentity;
    private bool _areFirmwareImagesCompatible;
    private string _patchStatus = "请先导入 A 版本和 B 版本固件。";
    private string _patchRestoreTestStatus = "请选择尚未验证的外部 Patch。工具自产 Patch 已自动完成双向还原验证。";
    private string _httpServiceStatus = "未启动";
    private string _publicHttpServiceStatus = "未设置";
    private string _targetIdList = "10010001\n10010002\n10010003";
    private int _nodeType = 5;
    private int _selectedNodeTypeValue = 5;
    private string _nodeIdSearch = string.Empty;
    private string _newNodeTypeName = string.Empty;
    private string _newNodeTypeValue = string.Empty;
    private string _nodeTargetsText = "10010001: 1,2,3";
    private string _oldVersion = "V1.2.3";
    private string _newVersion = "V1.3.0";
    private string _gatewayId = "704027";
    private byte? _gatewaySoftwareVersion;
    private string _gatewayVersionGatewayId = string.Empty;
    private const int MaxGatewayIdHistory = 12;
    private const string DefaultExternalMqttHost = "117.172.29.2";
    private const int DefaultExternalMqttPort = 36106;
    private string _mqttHost = DefaultExternalMqttHost;
    private int _mqttPort = DefaultExternalMqttPort;
    private bool _mqttClientUsesLocalBroker = true;
    private int _localBrokerPort = 1883;
    private string _localBrokerUserName = string.Empty;
    private string _localBrokerPassword = string.Empty;
    private bool _mqttUseTls;
    private bool _mqttAcceptAnyServerCertificate;
    private string _mqttUserName = string.Empty;
    private string _mqttPassword = string.Empty;
    private int _httpPort = 8080;
    private bool _httpUsesLocalServer = true;
    private string _publicHttpBaseUrl = "http://117.172.29.2:36109/download/";
    private string _mqttStatus = "未连接";
    private string _embeddedBrokerStatus = "未启动";
    private string _patchUrl = string.Empty;
    private string _patchMd5 = string.Empty;
    private string _patchSha256 = string.Empty;
    private long _patchLength;
    private bool? _patchManifestVerified;
    private string _reversePatchPath = string.Empty;
    private string _reversePatchUrl = string.Empty;
    private string _reversePatchMd5 = string.Empty;
    private string _reversePatchSha256 = string.Empty;
    private long _reversePatchLength;
    private string _forwardPatchName = "a-to-b";
    private string _reversePatchName = "b-to-a";
    private int _cycleRounds = 1;
    private string _cycleIntervalMode = "固定间隔";
    private int _cycleFixedIntervalSeconds;
    private int _cycleRandomMinimumSeconds;
    private int _cycleRandomMaximumSeconds;
    private long _nodePatchLimit = PatchCapacityPolicy.NodePatchLimit;
    private long _asyncPatchLimit = PatchCapacityPolicy.AsyncPatchLimit;
    private long _syncPatchLimit = PatchCapacityPolicy.SyncPatchLimit;
    private long _gatewayPatchLimit = PatchCapacityPolicy.GatewayPatchLimit;
    private int _discoveryFreshnessMinutes = 30;
    private int _minimumNodeRssi = -100;
    private DateTimeOffset? _nodeDiscoveryCompletedAt;
    private readonly HashSet<Guid> _reportTaskIds = [];
    private readonly HashSet<Guid> _autoExportedReportIds = [];
    private readonly SemaphoreSlim _reportWriteLock = new(1, 1);
    private bool _isCycleUpgradeRunning;
    private CancellationTokenSource? _cycleCancellation;
    private string _upgradeRunModeText = "尚未启动";
    private string _upgradeRunModeForeground = "#65758B";
    private string _upgradeRunModeBackground = "#EEF2F7";
    private string _upgradeRunProgressText = "启动任务后显示执行方式和进度。";
    private readonly HashSet<string> _observedGatewayIds = new(StringComparer.Ordinal);
    private string _subscribedGatewayTopic = string.Empty;
    private string _gatewaySubscriptionStatus = "填写 Gateway ID 后订阅固定上行主题。";
    private string _mqttMessageFilter = string.Empty;
    private OtaTaskRunner? _runner;
    private OtaReport? _activeReport;
    private string _sftpHost = "117.172.29.2";
    private int _sftpPort = 36112;
    private string _sftpUserName = "root";
    private string _sftpPassword = string.Empty;
    private string _sftpPrivateKeyPath = string.Empty;
    private string _sftpPrivateKeyPassphrase = string.Empty;
    private string _sftpRemoteDirectory = "/opt/www/static/download/";
    private string _sftpPublicBaseUrl = string.Empty;
    private string _sftpHostKeySha256 = string.Empty;
    private string _publishStatus = "未发布";
    private bool _isPublishing;
    private bool _isTestingPublishConnection;
    private bool _hasPublishedPatches;
    private string _publishConnectionTestStatus = "尚未测试 SFTP 和 HTTP 连接。";
    private string _logAnalyzerExecutablePath = GetDefaultLogAnalyzerPath();
    private string _logDirectory = string.Empty;
    private string _logAnalysisStatus = "未导入日志";
    private string _logAnalysisResultText = "尚未执行日志分析。";
    private IReadOnlyList<LogAnalysisLineViewItem> _logAnalysisResultLines =
        [new("尚未执行日志分析。", false, false)];
    private string _logAnalysisQualityScore = "--";
    private string _logAnalysisQualityGrade = "尚未评估";
    private string _logAnalysisQualitySummary = "分析日志后生成 100 分制质量评估。";
    private string _logAnalysisQualityColor = "#65758B";
    private string _settingsStatus = "设置尚未保存";
    private string _deviceDiscoveryStatus = "尚未刷新在线 Extender。";
    private string _nodeDiscoveryStatus = "尚未刷新 Node。";
    private bool _isRefreshingExtenders;
    private bool _isRefreshingNodes;
    private bool _suppressSelectionSync;
    private string _gatewayStageSummary = "尚未收到 Gateway 阶段状态。";
    private string _gatewayStageColor = "#65758B";
    private string _gatewayPackageSourceSummary = string.Empty;
    private string _gatewayPackageSourceColor = "#65758B";
    private GatewayOtaStatus? _lastGatewayStatus;
    private int? _gatewayTaskSequence;
    private DateTimeOffset? _gatewayTaskStartedAt;
    private DeviceType _gatewayStatusDeviceType = DeviceType.Gateway;
    private ReportListItem? _selectedReport;
    private bool _showArchivedReports;

    public MainWindowViewModel()
    {
        ApplicationUpdate = new ApplicationUpdateViewModel();
        _deviceDiscovery = new DeviceDiscoveryService(_mqtt);
        NavigateCommand = new RelayCommand(Navigate);
        SelectPatchCommand = new RelayCommand(SelectPatch);
        DeletePatchCommand = new RelayCommand(DeletePatch);
        ConfirmPatchDialogCommand = new AsyncRelayCommand(ConfirmPatchDialogAsync);
        CancelPatchDialogCommand = new RelayCommand(_ => CancelPatchDialog());
        SelectOldImageCommand = new AsyncRelayCommand(
            () => SelectFirmwareImageAsync(isOldImage: true));
        SelectNewImageCommand = new AsyncRelayCommand(
            () => SelectFirmwareImageAsync(isOldImage: false));
        GeneratePatchCommand = new AsyncRelayCommand(GeneratePatchAsync);
        BrowsePatchOutputDirectoryCommand = new RelayCommand(BrowsePatchOutputDirectory);
        OpenPatchOutputDirectoryCommand = new RelayCommand(OpenPatchOutputDirectory);
        TestPatchRestoreCommand = new AsyncRelayCommand(TestPatchRestoreAsync);
        SelectReversePatchCommand = new RelayCommand(SelectReversePatch);
        StartForwardTaskCommand = new AsyncRelayCommand(() => StartSingleTaskAsync(reverse: false));
        StartReverseTaskCommand = new AsyncRelayCommand(() => StartSingleTaskAsync(reverse: true));
        StartCycleCommand = new AsyncRelayCommand(StartCycleAsync);
        StartHttpServiceCommand = new AsyncRelayCommand(StartHttpServiceAsync);
        StopHttpServiceCommand = new AsyncRelayCommand(StopHttpServiceAsync);
        ToggleHttpServiceCommand = new AsyncRelayCommand(ToggleHttpServiceAsync);
        ApplyPublicHttpServerCommand = new AsyncRelayCommand(ApplyPublicHttpServerAsync);
        ConnectMqttCommand = new AsyncRelayCommand(ConnectMqttAsync);
        DisconnectMqttCommand = new AsyncRelayCommand(DisconnectMqttAsync);
        ToggleMqttConnectionCommand = new AsyncRelayCommand(ToggleMqttConnectionAsync);
        ToggleLocalMqttConnectionCommand = new AsyncRelayCommand(ToggleLocalMqttConnectionAsync);
        TogglePublicMqttConnectionCommand = new AsyncRelayCommand(TogglePublicMqttConnectionAsync);
        SelectMqttConfigurationCommand = new RelayCommand(SelectMqttConfiguration);
        StartEmbeddedBrokerCommand = new AsyncRelayCommand(StartEmbeddedBrokerAsync);
        StopEmbeddedBrokerCommand = new AsyncRelayCommand(StopEmbeddedBrokerAsync);
        ToggleEmbeddedBrokerCommand = new AsyncRelayCommand(ToggleEmbeddedBrokerAsync);
        TogglePollingCommand = new RelayCommand(TogglePolling);
        CancelTaskCommand = new AsyncRelayCommand(CancelTaskAsync);
        SubscribeGatewayTopicCommand = new AsyncRelayCommand(SubscribeGatewayTopicAsync);
        ClearMqttMessagesCommand = new RelayCommand(_ => ClearMqttMessages());
        ClearGlobalLogCommand = new RelayCommand(_ => GlobalLogText = string.Empty);
        PublishPatchCommand = new AsyncRelayCommand(PublishPatchAsync);
        TestPublishConnectionCommand = new AsyncRelayCommand(TestPublishConnectionAsync);
        AnalyzeLogsCommand = new AsyncRelayCommand(AnalyzeLogsAsync);
        BrowseLogDirectoryCommand = new RelayCommand(BrowseLogDirectory);
        RemoveImportedLogFileCommand = new RelayCommand(RemoveImportedLogFile);
        LoadReportsCommand = new AsyncRelayCommand(() => LoadReportsAsync());
        OpenReportCommand = new AsyncRelayCommand(OpenSelectedReportAsync);
        ToggleReportArchiveCommand = new AsyncRelayCommand(ToggleSelectedReportArchiveAsync);
        DeleteReportCommand = new RelayCommand(_ => RequestSelectedReportDeletion());
        ShowActiveReportsCommand = new RelayCommand(_ => ShowReportScope(showArchived: false));
        ShowArchivedReportsCommand = new RelayCommand(_ => ShowReportScope(showArchived: true));
        RefreshExtendersCommand = new AsyncRelayCommand(
            RefreshExtendersAsync,
            exception => DeviceDiscoveryStatus =
                $"刷新{(SelectedTaskType == GatewayTaskType ? " Gateway" : " Extender")}失败：{exception.Message}");
        ToggleExtenderSelectionCommand = new RelayCommand(ToggleExtenderSelection);
        RefreshNodesCommand = new AsyncRelayCommand(
            RefreshNodesAsync,
            exception => NodeDiscoveryStatus = $"刷新 Node 失败：{exception.Message}");
        ToggleNodeSelectionCommand = new RelayCommand(ToggleNodeSelection);
        AddNodeTypeCommand = new AsyncRelayCommand(AddNodeTypeAsync);
        _reportStore = new SqliteReportStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtaTool", "ota-tool.db"));
        _settingsStore = new JsonSettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtaTool", "settings.json"));
        _startupSettings = LoadStartupSettings();
        ApplyStartupShellSettings(_startupSettings);
        _mqtt.ConnectionStateChanged += (_, status) => RunOnUi(() => MqttStatus = status);
        _mqtt.MessageReceived += OnMqttMessageReceived;
        ApplyMode();
        SelectedPage = NavigationItems.FirstOrDefault(item => item.Name == "MQTT 配置");
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        _initializationTask = InitializeAsync();
    }

    public ApplicationUpdateViewModel ApplicationUpdate { get; }

    public Task Initialization => _initializationTask;

    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];

    public ObservableCollection<string> TaskTypes { get; } = [];

    public ObservableCollection<PatchSelection> UpgradePatchChoices { get; } = [];

    public IReadOnlyList<PatchSelection> ReverseUpgradePatchChoices => UpgradePatchChoices
        .Where(item => !string.Equals(item.FilePath, SelectedUpgradePatch?.FilePath, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public ObservableCollection<string> PatchRestoreDirections { get; } = ["A → B", "B → A"];

    public IReadOnlyList<PatchSelection> PatchCatalog => _patchCatalog.Values
        .Where(item => IsInCurrentPatchWorkspace(item.FilePath))
        .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<PatchSelection> PatchRestoreChoices
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_importedPatchPath) ||
                !_patchCatalog.TryGetValue(_importedPatchPath, out var importedPatch) ||
                importedPatch.IsFullImage)
            {
                return [];
            }

            return [importedPatch];
        }
    }

    public ObservableCollection<ReportListItem> RecentReports { get; } = [];

    public ReportListItem? SelectedReport
    {
        get => _selectedReport;
        set
        {
            if (!SetProperty(ref _selectedReport, value)) return;
            OnPropertyChanged(nameof(SelectedReportVisibility));
            OnPropertyChanged(nameof(ReportSelectionHintVisibility));
        }
    }

    public ObservableCollection<MqttMessageListItem> MqttMessages { get; } = [];

    public ObservableCollection<SelectableExtenderItem> DiscoveredExtenders { get; } = [];

    public ObservableCollection<NodeGroupItem> DiscoveredNodeGroups { get; } = [];

    public ObservableCollection<NodeTypeOption> NodeTypeOptions { get; } = [];

    public ObservableCollection<string> GatewayIdHistory { get; } = [];

    public string NodeIdSearch
    {
        get => _nodeIdSearch;
        set
        {
            if (!SetProperty(ref _nodeIdSearch, value)) return;
            RefreshNodeEligibility();
        }
    }

    public ObservableCollection<GatewayStageViewItem> GatewayStages { get; } = [];

    public ObservableCollection<GatewaySubtaskViewItem> GatewaySubtasks { get; } = [];

    public PatchSelection? SelectedUpgradePatch
    {
        get => _selectedUpgradePatch;
        set
        {
            if (!SetProperty(ref _selectedUpgradePatch, value)) return;
            OnPropertyChanged(nameof(SelectedUpgradePatchSummary));
            OnPropertyChanged(nameof(SelectedHttpPatchUrl));
            OnPropertyChanged(nameof(ReverseUpgradePatchChoices));
            OnPropertyChanged(nameof(CanStartForwardUpgrade));
            OnPropertyChanged(nameof(CanStartReverseUpgrade));
            OnPropertyChanged(nameof(CanStartCycleUpgrade));
            if (value is not null)
            {
                _ = ApplySelectedPatchManifestAsync(value);
            }
            else
            {
                _selectedPatchManifest = null;
                _patchManifestVerified = false;
                RefreshNodeEligibility();
            }
            if (SelectedReverseUpgradePatch is not null &&
                string.Equals(SelectedReverseUpgradePatch.FilePath, value?.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedReverseUpgradePatch = null;
                TaskStatusMessage = "循环升级需要选择与正向 Patch 不同的反向 Patch。";
            }
        }
    }

    private async Task ApplySelectedPatchManifestAsync(PatchSelection patch)
    {
        if (!IsEcoLink || patch.IsFullImage)
        {
            _selectedPatchManifest = null;
            _patchManifestVerified = true;
            RefreshNodeEligibility();
            OnPropertyChanged(nameof(CanStartUpgrade));
            OnPropertyChanged(nameof(CanStartCycleUpgrade));
            return;
        }
        try
        {
            var manifest = await PackageManifestImporter.LoadAndValidateAsync(Path.GetFullPath(patch.FilePath));
            if (!ReferenceEquals(_selectedUpgradePatch, patch))
            {
                return;
            }
            if (manifest.OtaDeviceType != GetSelectedTaskDeviceType())
            {
                SelectedUpgradePatch = null;
                TaskStatusMessage = $"所选 Patch 类型与“{SelectedTaskType}”不匹配，请选择对应类型的 Patch。";
                return;
            }
            ApplyManifestDetails(manifest, updateTaskType: false);
            OnPropertyChanged(nameof(CanStartUpgrade));
            OnPropertyChanged(nameof(CanStartCycleUpgrade));
        }
        catch (Exception exception)
        {
            _patchManifestVerified = false;
            TaskStatusMessage = $"Patch 元数据校验失败：{exception.Message}";
        }
    }

    public PatchSelection? SelectedRestorePatch
    {
        get => _selectedRestorePatch;
        set
        {
            if (!SetProperty(ref _selectedRestorePatch, value)) return;
            if (value is not null)
            {
                SelectedPatchRestoreDirection = InferPatchRestoreDirection(value);
            }
            OnPropertyChanged(nameof(CanTestSelectedPatchRestore));
        }
    }

    public bool CanTestSelectedPatchRestore => SelectedRestorePatch is
    {
        ManifestVerified: false,
        IsFullImage: false,
        FilePath.Length: > 0,
    } patch && File.Exists(patch.FilePath);

    public PatchSelection? SelectedReverseUpgradePatch
    {
        get => _selectedReverseUpgradePatch;
        set
        {
            if (value is not null &&
                string.Equals(value.FilePath, SelectedUpgradePatch?.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (SetProperty(ref _selectedReverseUpgradePatch, null))
                {
                    TaskStatusMessage = "反向 Patch 不能与单次升级使用的正向 Patch 相同。";
                    OnPropertyChanged(nameof(CanStartCycleUpgrade));
                }
                return;
            }
            if (!SetProperty(ref _selectedReverseUpgradePatch, value)) return;
            OnPropertyChanged(nameof(CanStartForwardUpgrade));
            OnPropertyChanged(nameof(CanStartReverseUpgrade));
            OnPropertyChanged(nameof(CanStartCycleUpgrade));
            if (value is null) return;
            _reversePatchPath = value.FilePath;
            _reversePatchLength = value.Length;
            _reversePatchMd5 = value.Md5;
            _reversePatchSha256 = value.Sha256;
            _reversePatchUrl = IsHttpServiceRunning ? GetLocalPatchUrl(value.FilePath) : string.Empty;
            OnPropertyChanged(nameof(ReversePatchFileName));
            OnPropertyChanged(nameof(ReversePatchStatus));
            OnPropertyChanged(nameof(ReversePatchMetadataDetail));
        }
    }

    public string SelectedPatchRestoreDirection
    {
        get => _selectedPatchRestoreDirection;
        set => SetProperty(ref _selectedPatchRestoreDirection, value);
    }

    public Visibility PatchDialogVisibility => IsPatchDialogOpen &&
        _patchDialogAction is PatchDialogAction.Delete or PatchDialogAction.Publish
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility GlobalDialogVisibility => IsPatchDialogOpen &&
        _patchDialogAction is not (PatchDialogAction.Delete or PatchDialogAction.Publish)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DialogCancelVisibility => _patchDialogAction == PatchDialogAction.Information
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsPatchDialogConfirmDefault => IsPatchDialogOpen &&
        _patchDialogAction is PatchDialogAction.Delete or PatchDialogAction.Publish;

    public bool IsGlobalDialogConfirmDefault => IsPatchDialogOpen &&
        _patchDialogAction is not (PatchDialogAction.Delete or PatchDialogAction.Publish);

    public bool IsPatchDialogOpen
    {
        get => _isPatchDialogOpen;
        private set
        {
            if (!SetProperty(ref _isPatchDialogOpen, value)) return;
            OnPropertyChanged(nameof(PatchDialogVisibility));
            OnPropertyChanged(nameof(GlobalDialogVisibility));
            OnPropertyChanged(nameof(DialogCancelVisibility));
            OnPropertyChanged(nameof(IsPatchDialogConfirmDefault));
            OnPropertyChanged(nameof(IsGlobalDialogConfirmDefault));
        }
    }

    public string PatchDialogTitle { get => _patchDialogTitle; private set => SetProperty(ref _patchDialogTitle, value); }

    public string PatchDialogMessage { get => _patchDialogMessage; private set => SetProperty(ref _patchDialogMessage, value); }

    public string PatchDialogConfirmText { get => _patchDialogConfirmText; private set => SetProperty(ref _patchDialogConfirmText, value); }

    public string DialogResultStampText { get => _dialogResultStampText; private set => SetProperty(ref _dialogResultStampText, value); }

    public string DialogResultStampColor { get => _dialogResultStampColor; private set => SetProperty(ref _dialogResultStampColor, value); }

    public Visibility DialogResultStampVisibility => string.IsNullOrWhiteSpace(DialogResultStampText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public ICommand NavigateCommand { get; }

    public ICommand SelectPatchCommand { get; }

    public ICommand DeletePatchCommand { get; }

    public ICommand ConfirmPatchDialogCommand { get; }

    public ICommand CancelPatchDialogCommand { get; }

    public ICommand SelectOldImageCommand { get; }

    public ICommand SelectNewImageCommand { get; }

    public ICommand GeneratePatchCommand { get; }

    public ICommand BrowsePatchOutputDirectoryCommand { get; }

    public ICommand OpenPatchOutputDirectoryCommand { get; }

    public ICommand TestPatchRestoreCommand { get; }

    public ICommand SelectReversePatchCommand { get; }

    public ICommand StartForwardTaskCommand { get; }

    public ICommand StartReverseTaskCommand { get; }

    public ICommand StartCycleCommand { get; }

    public ICommand StartHttpServiceCommand { get; }

    public ICommand StopHttpServiceCommand { get; }

    public ICommand ToggleHttpServiceCommand { get; }

    public ICommand ApplyPublicHttpServerCommand { get; }

    public ICommand ConnectMqttCommand { get; }

    public ICommand DisconnectMqttCommand { get; }

    public ICommand ToggleMqttConnectionCommand { get; }

    public ICommand ToggleLocalMqttConnectionCommand { get; }

    public ICommand TogglePublicMqttConnectionCommand { get; }

    public ICommand SelectMqttConfigurationCommand { get; }

    public ICommand StartEmbeddedBrokerCommand { get; }

    public ICommand StopEmbeddedBrokerCommand { get; }

    public ICommand ToggleEmbeddedBrokerCommand { get; }

    public ICommand TogglePollingCommand { get; }

    public ICommand CancelTaskCommand { get; }

    public ICommand SubscribeGatewayTopicCommand { get; }

    public ICommand ClearMqttMessagesCommand { get; }

    public ICommand ClearGlobalLogCommand { get; }

    public ICommand PublishPatchCommand { get; }

    public ICommand TestPublishConnectionCommand { get; }

    public ICommand AnalyzeLogsCommand { get; }

    public ICommand BrowseLogDirectoryCommand { get; }

    public ICommand RemoveImportedLogFileCommand { get; }

    public ICommand LoadReportsCommand { get; }

    public ICommand OpenReportCommand { get; }

    public ICommand ToggleReportArchiveCommand { get; }

    public ICommand DeleteReportCommand { get; }

    public ICommand ShowActiveReportsCommand { get; }

    public ICommand ShowArchivedReportsCommand { get; }

    public ICommand SaveSettingsCommand { get; }

    public ICommand RefreshExtendersCommand { get; }

    public ICommand ToggleExtenderSelectionCommand { get; }

    public ICommand RefreshNodesCommand { get; }

    public ICommand ToggleNodeSelectionCommand { get; }

    public ICommand AddNodeTypeCommand { get; }

    public NavigationItem? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value) || value is null)
            {
                return;
            }

            if (!_restoringModeWorkspace)
            {
                GetCurrentModeWorkspace().SelectedPageName = value.Name;
            }

            OnPropertyChanged(nameof(CurrentPageTitle));
            OnPropertyChanged(nameof(CurrentPageSubtitle));
            OnPropertyChanged(nameof(EnvironmentPageVisibility));
            OnPropertyChanged(nameof(PatchPageVisibility));
            OnPropertyChanged(nameof(TaskPageVisibility));
            OnPropertyChanged(nameof(LogPageVisibility));
            OnPropertyChanged(nameof(ReportsPageVisibility));
            OnPropertyChanged(nameof(SettingsPageVisibility));
            if (value.Name == "PATCH 中心") _ = LoadPatchCatalogFromOutputDirectoryAsync();
            if (value.Name == "历史报告") _ = LoadReportsAsync(updateStatus: false);
        }
    }

    public bool IsEcoLink
    {
        get => _isEcoLink;
        set
        {
            if (_isEcoLink == value) return;
            if (IsUpgradeInProgress)
            {
                TaskStatusMessage = "升级任务进行中，不能切换协议模式。";
                OnPropertyChanged(nameof(IsEcoLink));
                OnPropertyChanged(nameof(IsTraditional));
                return;
            }
            if (_mqtt.IsConnected || _httpRangeServer.IsRunning || _embeddedBroker.IsRunning || IsPublishing || IsPatchDialogOpen)
            {
                TaskStatusMessage = "请先关闭当前确认框、断开 MQTT、停止本地服务并等待发布结束，再切换协议模式。";
                OnPropertyChanged(nameof(IsEcoLink));
                OnPropertyChanged(nameof(IsTraditional));
                return;
            }

            SaveCurrentModeUiState();
            if (!SetProperty(ref _isEcoLink, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTraditional));
            ApplyMode();
            ApplyCurrentModeWorkspace();
            RefreshUpgradePatchChoices();
            RestoreCurrentModeUpgradeUiState();
            _ = RestoreCurrentModePatchCatalogAsync();
            _ = LoadReportsAsync(updateStatus: false);
            OnPropertyChanged(nameof(TargetScopeVisibility));
            OnPropertyChanged(nameof(SpecifiedTargetVisibility));
            OnPropertyChanged(nameof(BroadcastTargetVisibility));
            OnPropertyChanged(nameof(ExtenderSelectionVisibility));
            OnPropertyChanged(nameof(EcoLinkStatusDetailsVisibility));
            OnPropertyChanged(nameof(TraditionalStatusVisibility));
            ScheduleSettingsAutoSave();
        }
    }

    public bool IsTraditional
    {
        get => !_isEcoLink;
        set
        {
            if (value)
            {
                IsEcoLink = false;
            }
        }
    }

    public bool IsSpecifiedTarget
    {
        get => _isSpecifiedTarget;
        set
        {
            if (!SetProperty(ref _isSpecifiedTarget, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBroadcastTarget));
            OnPropertyChanged(nameof(SpecifiedTargetVisibility));
            OnPropertyChanged(nameof(BroadcastTargetVisibility));
        }
    }

    public bool IsBroadcastTarget
    {
        get => !_isSpecifiedTarget;
        set
        {
            if (value)
            {
                IsSpecifiedTarget = false;
            }
        }
    }

    public string SelectedTaskType
    {
        get => _selectedTaskType;
        set
        {
            if (SetProperty(ref _selectedTaskType, value))
            {
                if (IsEcoLink) _ecoLinkSelectedTaskType = value;
                else _traditionalSelectedTaskType = value;
                if (RequiresSpecifiedTarget)
                {
                    IsSpecifiedTarget = true;
                }
                OnPropertyChanged(nameof(NodeTaskVisibility));
                OnPropertyChanged(nameof(TargetScopeVisibility));
                OnPropertyChanged(nameof(SpecifiedTargetVisibility));
                OnPropertyChanged(nameof(BroadcastTargetVisibility));
                OnPropertyChanged(nameof(ExtenderSelectionVisibility));
                OnPropertyChanged(nameof(ExtenderTargetListVisibility));
                OnPropertyChanged(nameof(DeviceDiscoveryButtonText));
                OnPropertyChanged(nameof(NodeDiscoveryVisibility));
                DeviceDiscoveryStatus = value == GatewayTaskType
                    ? _gatewaySoftwareVersion.HasValue &&
                      string.Equals(_gatewayVersionGatewayId, GatewayId, StringComparison.Ordinal)
                        ? $"Gateway 当前软件版本：{ProtocolVersionFormatter.FormatWithPrefix(_gatewaySoftwareVersion.Value)}。"
                        : "尚未查询 Gateway 当前软件版本。"
                    : "尚未刷新在线 Extender。";
                RefreshUpgradePatchChoices();
            }
        }
    }

    public string OldVersion
    {
        get => _oldVersion;
        set
        {
            if (!SetProperty(ref _oldVersion, value)) return;
            OnPropertyChanged(nameof(OldVersionDisplay));
            NotifyUpgradeActionAvailability();
        }
    }

    public string NewVersion
    {
        get => _newVersion;
        set
        {
            if (!SetProperty(ref _newVersion, value)) return;
            OnPropertyChanged(nameof(NewVersionDisplay));
            NotifyUpgradeActionAvailability();
        }
    }

    public string OldVersionDisplay => ProtocolVersionFormatter.FormatRaw(OldVersion);

    public string NewVersionDisplay => ProtocolVersionFormatter.FormatRaw(NewVersion);

    public string TargetIdList
    {
        get => _targetIdList;
        set => SetProperty(ref _targetIdList, value);
    }

    public int NodeType
    {
        get => _nodeType;
        set
        {
            if (!SetProperty(ref _nodeType, value)) return;
            _selectedNodeTypeValue = value;
            OnPropertyChanged(nameof(SelectedNodeTypeOption));
            RefreshNodeEligibility();
            ScheduleSettingsAutoSave();
        }
    }

    public NodeTypeOption? SelectedNodeTypeOption
    {
        get => NodeTypeOptions.FirstOrDefault(option => option.Value == _selectedNodeTypeValue);
        set
        {
            // ComboBox 在 ItemsSource 重建时会短暂写入 null；此时必须保留已持久化的类型。
            if (value is null)
            {
                return;
            }
            _selectedNodeTypeValue = value.Value;
            OnPropertyChanged();
            if (value.Value == 0)
            {
                ClearNodeSelection();
                return;
            }
            NodeType = value.Value;
            SelectNodesByType(value.Value);
        }
    }

    public string NewNodeTypeName
    {
        get => _newNodeTypeName;
        set => SetProperty(ref _newNodeTypeName, value);
    }

    public string NewNodeTypeValue
    {
        get => _newNodeTypeValue;
        set => SetProperty(ref _newNodeTypeValue, value);
    }

    public string NodeTargetsText
    {
        get => _nodeTargetsText;
        set => SetProperty(ref _nodeTargetsText, value);
    }

    public string GatewayId
    {
        get => _gatewayId;
        set
        {
            if (SetProperty(ref _gatewayId, value))
            {
                _gatewaySoftwareVersion = null;
                _gatewayVersionGatewayId = string.Empty;
                OnPropertyChanged(nameof(GatewayOnlineStatus));
                OnPropertyChanged(nameof(GatewaySubscriptionTopic));
                OnPropertyChanged(nameof(GatewayPublishTopic));
                OnPropertyChanged(nameof(GatewayIdTaskHint));
                OnPropertyChanged(nameof(IsGatewayTopicSubscribed));
                OnPropertyChanged(nameof(GatewaySubscriptionBadgeText));
                OnPropertyChanged(nameof(GatewaySubscriptionBadgeBackground));
                OnPropertyChanged(nameof(GatewaySubscriptionBadgeForeground));
                if (SelectedTaskType == GatewayTaskType)
                {
                    DeviceDiscoveryStatus = "Gateway dev ID 已变更，请重新刷新 Gateway。";
                }
                NotifyUpgradeActionAvailability();
            }
        }
    }

    public string GatewayIdTaskHint => string.IsNullOrWhiteSpace(GatewayId)
        ? "Gateway dev ID（请在 MQTT 订阅配置中填写）由 MQTT 配置页的主题订阅统一设置。"
        : _gatewaySoftwareVersion.HasValue &&
          string.Equals(_gatewayVersionGatewayId, GatewayId, StringComparison.Ordinal)
            ? $"Gateway dev ID {GatewayId} · 当前软件版本 {ProtocolVersionFormatter.FormatWithPrefix(_gatewaySoftwareVersion.Value)}。"
            : $"Gateway dev ID {GatewayId}（在 MQTT 订阅配置中填写）由 MQTT 配置页的主题订阅统一设置。";

    public string MqttHost
    {
        get => _mqttHost;
        set
        {
            if (SetProperty(ref _mqttHost, value)) OnPropertyChanged(nameof(MqttClientEndpoint));
        }
    }

    public int MqttPort
    {
        get => _mqttPort;
        set
        {
            if (SetProperty(ref _mqttPort, value)) OnPropertyChanged(nameof(MqttClientEndpoint));
        }
    }

    public bool MqttClientUsesLocalBroker
    {
        get => _mqttClientUsesLocalBroker;
        set
        {
            if (value == _mqttClientUsesLocalBroker) return;
            if (_mqtt.IsConnected)
            {
                ShowInformationDialog("无法切换 MQTT 服务端", "MQTT 客户端仍处于连接状态，请先断开 MQTT 连接，再切换服务端。");
                OnPropertyChanged(nameof(MqttClientUsesLocalBroker));
                OnPropertyChanged(nameof(MqttClientUsesExternalBroker));
                return;
            }
            if (!SetProperty(ref _mqttClientUsesLocalBroker, value)) return;
            if (!value && MqttHost == "127.0.0.1" && MqttPort == 1883)
            {
                MqttHost = DefaultExternalMqttHost;
                MqttPort = DefaultExternalMqttPort;
            }
            OnPropertyChanged(nameof(MqttClientUsesExternalBroker));
            OnPropertyChanged(nameof(MqttClientEndpoint));
            OnPropertyChanged(nameof(MqttExternalConfigurationVisibility));
            OnPropertyChanged(nameof(MqttLocalConfigurationVisibility));
            ScheduleSettingsAutoSave();
        }
    }

    public bool MqttClientUsesExternalBroker => !_mqttClientUsesLocalBroker;

    public int LocalBrokerPort
    {
        get => _localBrokerPort;
        set
        {
            if (SetProperty(ref _localBrokerPort, value)) OnPropertyChanged(nameof(MqttClientEndpoint));
        }
    }

    public string LocalBrokerUserName { get => _localBrokerUserName; set => SetProperty(ref _localBrokerUserName, value); }

    public string LocalBrokerPassword { get => _localBrokerPassword; set => SetProperty(ref _localBrokerPassword, value); }

    public string MqttClientEndpoint => MqttClientUsesLocalBroker
        ? $"127.0.0.1:{LocalBrokerPort}（本地 Broker）"
        : $"{MqttHost}:{MqttPort}（公网 / 外部 Broker）";

    public bool MqttUseTls { get => _mqttUseTls; set => SetProperty(ref _mqttUseTls, value); }

    public bool MqttAcceptAnyServerCertificate { get => _mqttAcceptAnyServerCertificate; set => SetProperty(ref _mqttAcceptAnyServerCertificate, value); }

    public string MqttUserName { get => _mqttUserName; set => SetProperty(ref _mqttUserName, value); }

    public string MqttPassword { get => _mqttPassword; set => SetProperty(ref _mqttPassword, value); }

    public int HttpPort
    {
        get => _httpPort;
        set
        {
            if (SetProperty(ref _httpPort, value)) OnPropertyChanged(nameof(HttpServiceAddress));
        }
    }

    public bool HttpUsesLocalServer
    {
        get => _httpUsesLocalServer;
        set
        {
            if (value == _httpUsesLocalServer) return;
            if (_httpRangeServer.IsRunning)
            {
                ShowInformationDialog("无法切换 HTTP 文件来源", "本地 HTTP 服务仍在运行，请先停止本地服务，再切换文件来源。");
                OnPropertyChanged(nameof(HttpUsesLocalServer));
                OnPropertyChanged(nameof(HttpUsesPublicServer));
                return;
            }
            if (!SetProperty(ref _httpUsesLocalServer, value)) return;
            OnPropertyChanged(nameof(HttpUsesPublicServer));
            OnPropertyChanged(nameof(SelectedHttpPatchUrl));
            OnPropertyChanged(nameof(HttpLocalConfigurationVisibility));
            OnPropertyChanged(nameof(HttpPublicConfigurationVisibility));
            ScheduleSettingsAutoSave();
        }
    }

    public bool HttpUsesPublicServer
    {
        get => !_httpUsesLocalServer;
        set
        {
            if (value) HttpUsesLocalServer = false;
        }
    }

    public string PublicHttpBaseUrl
    {
        get => _publicHttpBaseUrl;
        set
        {
            if (SetProperty(ref _publicHttpBaseUrl, value)) OnPropertyChanged(nameof(SelectedHttpPatchUrl));
        }
    }

    public string SelectedHttpPatchUrl => GetPatchDownloadUrl(SelectedUpgradePatch?.FilePath ?? _patchPath);

    public Visibility MqttExternalConfigurationVisibility => MqttClientUsesExternalBroker ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MqttLocalConfigurationVisibility => MqttClientUsesLocalBroker ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HttpLocalConfigurationVisibility => HttpUsesLocalServer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HttpPublicConfigurationVisibility => HttpUsesPublicServer ? Visibility.Visible : Visibility.Collapsed;

    public bool IsEmbeddedBrokerRunning => _embeddedBroker.IsRunning;

    public bool IsMqttConnected => _mqtt.IsConnected;

    public bool IsHttpServiceRunning => _httpRangeServer.IsRunning;

    public string EmbeddedBrokerStatus { get => _embeddedBrokerStatus; private set => SetProperty(ref _embeddedBrokerStatus, value); }

    public string EmbeddedBrokerToggleText => IsEmbeddedBrokerRunning ? "停止本地 Broker" : "启动本地 Broker";

    public string MqttConnectionToggleText => IsMqttConnected ? "断开 MQTT" : "连接 MQTT";

    public string LocalMqttConnectionToggleText => IsMqttConnected && MqttClientUsesLocalBroker ? "断开本地 Broker" : "连接本地 Broker";

    public string PublicMqttConnectionToggleText => IsMqttConnected && MqttClientUsesExternalBroker ? "断开公网 Broker" : "连接公网 Broker";

    public string HttpServiceToggleText => IsHttpServiceRunning ? "停止本地服务" : "启动本地服务";

    public string PublicHttpServiceStatus { get => _publicHttpServiceStatus; private set => SetProperty(ref _publicHttpServiceStatus, value); }

    public string MqttStatus
    {
        get => _mqttStatus;
        private set
        {
            if (!SetProperty(ref _mqttStatus, value)) return;
            OnPropertyChanged(nameof(IsMqttConnected));
            OnPropertyChanged(nameof(CanRefreshDiscovery));
            OnPropertyChanged(nameof(MqttConnectionToggleText));
            OnPropertyChanged(nameof(LocalMqttConnectionToggleText));
            OnPropertyChanged(nameof(PublicMqttConnectionToggleText));
        }
    }

    public string CurrentPageTitle => SelectedPage?.Name ?? "PATCH 中心";

    public string CurrentPageSubtitle => IsEcoLink
        ? "EcoLink 协议 · Patch 校验、任务配置与状态轮询"
        : "传统协议 · Gateway / Sync 定向或广播升级";

    public string ModeBadge => IsEcoLink ? "EcoLink 模式" : "传统模式";

    public Visibility EcoLinkVisibility => IsEcoLink ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EnvironmentPageVisibility => IsSelectedPage("MQTT 配置") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PatchPageVisibility => IsSelectedPage("PATCH 中心") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TaskPageVisibility => IsSelectedPage("升级任务") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LogPageVisibility => IsSelectedPage("日志分析") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReportsPageVisibility => IsSelectedPage("历史报告") ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectedReportVisibility => SelectedReport is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ReportSelectionHintVisibility => SelectedReport is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsShowingActiveReports => !_showArchivedReports;

    public bool IsShowingArchivedReports => _showArchivedReports;

    public string ActiveReportsHeader => _showArchivedReports ? "归档报告" : "当前报告";

    public string ReportScopeDescription => _showArchivedReports
        ? "已归档报告不会被删除，可随时恢复到当前报告。"
        : "测试结束后自动加入列表并导出 HTML、JSON 报告。";

    public Visibility SettingsPageVisibility => IsSelectedPage("系统设置") ? Visibility.Visible : Visibility.Collapsed;

    public bool RequiresSpecifiedTarget => IsEcoLink && SelectedTaskType is AsyncTaskType or NodeTaskType;

    public Visibility TargetScopeVisibility => SelectedTaskType is GatewayTaskType or NodeTaskType
        || (IsEcoLink && SelectedTaskType == AsyncTaskType)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility SpecifiedTargetVisibility => IsSpecifiedTarget && SelectedTaskType is not NodeTaskType and not GatewayTaskType
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility NodeTaskVisibility => IsEcoLink && SelectedTaskType == NodeTaskType ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExtenderSelectionVisibility => IsEcoLink &&
        (SelectedTaskType is GatewayTaskType or AsyncTaskType or NodeTaskType ||
         (SelectedTaskType == SyncTaskType && IsSpecifiedTarget))
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ExtenderTargetListVisibility => SelectedTaskType == GatewayTaskType
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility NodeDiscoveryVisibility => IsEcoLink && SelectedTaskType == NodeTaskType
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility EcoLinkStatusDetailsVisibility => IsEcoLink ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TraditionalStatusVisibility => IsTraditional ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BroadcastTargetVisibility => !IsSpecifiedTarget && TargetScopeVisibility == Visibility.Visible
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanStartUpgrade => !_isUpgradeStartInProgress &&
        _pendingUpgradeTask is null &&
        _pendingCycleForwardTask is null &&
        _runner?.HasActiveTask != true;

    public bool CanStartForwardUpgrade => CanStartUpgrade &&
        IsPatchConfiguredForDirection(SelectedUpgradePatch, reverse: false) &&
        IsSelectedTargetAtDirectionStartVersion(reverse: false);

    public bool CanStartReverseUpgrade => CanStartUpgrade &&
        IsPatchConfiguredForDirection(SelectedReverseUpgradePatch, reverse: true) &&
        IsSelectedTargetAtDirectionStartVersion(reverse: true);

    public bool CanStartCycleUpgrade => CanStartUpgrade &&
        IsPatchConfiguredForDirection(SelectedUpgradePatch, reverse: false) &&
        IsPatchConfiguredForDirection(SelectedReverseUpgradePatch, reverse: true) &&
        IsSelectedTargetAtDirectionStartVersion(reverse: false);

    public bool CanControlPolling => _runner?.HasActiveTask == true;

    public bool CanCancelTask => _runner?.HasActiveTask == true || _isCycleUpgradeRunning;

    public string GatewaySubscriptionTopic => $"ucchip/up/sgw/{GatewayId}/#";

    public string GatewayPublishTopic => $"ucchip/down/sgw/{GatewayId}/{{sequence}}";

    public bool IsGatewayTopicSubscribed => _mqtt.IsConnected
        && string.Equals(_subscribedGatewayTopic, GatewaySubscriptionTopic, StringComparison.Ordinal);

    public string GatewaySubscriptionBadgeText => IsGatewayTopicSubscribed ? "已订阅" : "未订阅";

    public string GatewaySubscriptionBadgeBackground => IsGatewayTopicSubscribed ? "#E8F8EF" : "#EAF0F6";

    public string GatewaySubscriptionBadgeForeground => IsGatewayTopicSubscribed ? "#18A665" : "#65758B";

    public string GatewaySubscriptionStatus
    {
        get => _gatewaySubscriptionStatus;
        private set => SetProperty(ref _gatewaySubscriptionStatus, value);
    }

    public string PatchFileName => string.IsNullOrWhiteSpace(_patchPath) ? "未选择 Patch" : Path.GetFileName(_patchPath);

    public string ImportedPatchFileName => string.IsNullOrWhiteSpace(_importedPatchPath) ? "未导入已有 Patch" : Path.GetFileName(_importedPatchPath);

    public string ImportedPatchMetadataDetail => string.IsNullOrWhiteSpace(_importedPatchPath)
        ? "暂无导入 Patch 详情"
        : $"大小：{_importedPatchLength:N0} B\nMD5：{_importedPatchMd5}\nSHA256：{_importedPatchSha256}";

    public string SelectedUpgradePatchSummary => SelectedUpgradePatch is null
        ? $"当前没有适用于“{SelectedTaskType}”的 Patch，请在 PATCH 中心制作或导入对应固件类型的 Patch。"
        : $"将使用：{SelectedUpgradePatch.FileName}（MD5：{SelectedUpgradePatch.Md5}）";

    public string PatchOutputDirectory
    {
        get => _patchOutputDirectory;
        set
        {
            if (!SetProperty(ref _patchOutputDirectory, value)) return;
            _patchCatalog.Clear();
            if (!_restoringModeWorkspace)
            {
                _ = LoadPatchCatalogFromOutputDirectoryAsync();
            }
        }
    }

    public string OldImageFileName => string.IsNullOrWhiteSpace(_oldImagePath) ? "未导入 A 版本固件" : Path.GetFileName(_oldImagePath);

    public string NewImageFileName => string.IsNullOrWhiteSpace(_newImagePath) ? "未导入 B 版本固件" : Path.GetFileName(_newImagePath);

    public string OldImageIdentityDetail => FormatFirmwareIdentity(_oldFirmwareIdentity);

    public string NewImageIdentityDetail => FormatFirmwareIdentity(_newFirmwareIdentity);

    public bool CanGeneratePatch =>
        !string.IsNullOrWhiteSpace(_oldImagePath) &&
        !string.IsNullOrWhiteSpace(_newImagePath) &&
        _oldFirmwareIdentity is not null &&
        _newFirmwareIdentity is not null &&
        _areFirmwareImagesCompatible;

    public string PatchDetail => string.IsNullOrWhiteSpace(_patchPath) ? "导入 A/B 固件后制作 Patch，或导入已有 Patch。" : _patchPath;

    public string PatchMetadataDetail => string.IsNullOrWhiteSpace(_patchPath)
        ? "暂无 Patch 详情"
        : $"大小：{_patchLength:N0} B\nMD5：{_patchMd5}\nSHA256：{_patchSha256}";

    public string PatchUrl
    {
        get => _patchUrl;
        private set
        {
            if (SetProperty(ref _patchUrl, value)) OnPropertyChanged(nameof(SelectedHttpPatchUrl));
        }
    }

    public string ReversePatchFileName => string.IsNullOrWhiteSpace(_reversePatchPath) ? "未选择反向 Patch" : Path.GetFileName(_reversePatchPath);

    public string ReversePatchStatus => string.IsNullOrWhiteSpace(_reversePatchPath) ? "反向版本（新版本→旧版本）需要独立 Patch。" : _reversePatchUrl;

    public string ReversePatchMetadataDetail => string.IsNullOrWhiteSpace(_reversePatchPath)
        ? "暂无反向 Patch 详情"
        : $"大小：{_reversePatchLength:N0} B\nMD5：{_reversePatchMd5}\nSHA256：{_reversePatchSha256}";

    public string ForwardPatchName
    {
        get => _forwardPatchName;
        set
        {
            if (SetProperty(ref _forwardPatchName, value)) ScheduleSettingsAutoSave();
        }
    }

    public string ReversePatchName
    {
        get => _reversePatchName;
        set
        {
            if (SetProperty(ref _reversePatchName, value)) ScheduleSettingsAutoSave();
        }
    }

    public int CycleRounds { get => _cycleRounds; set => SetProperty(ref _cycleRounds, value); }

    public IReadOnlyList<string> CycleIntervalModes { get; } = ["固定间隔", "随机间隔"];

    public string CycleIntervalMode
    {
        get => _cycleIntervalMode;
        set
        {
            var normalized = CycleIntervalModes.Contains(value) ? value : CycleIntervalModes[0];
            if (!SetProperty(ref _cycleIntervalMode, normalized)) return;
            OnPropertyChanged(nameof(FixedCycleIntervalVisibility));
            OnPropertyChanged(nameof(RandomCycleIntervalVisibility));
            OnPropertyChanged(nameof(CycleIntervalSummary));
        }
    }

    public int CycleFixedIntervalSeconds
    {
        get => _cycleFixedIntervalSeconds;
        set
        {
            if (SetProperty(ref _cycleFixedIntervalSeconds, Math.Clamp(value, 0, 86400)))
            {
                OnPropertyChanged(nameof(CycleIntervalSummary));
            }
        }
    }

    public int CycleRandomMinimumSeconds
    {
        get => _cycleRandomMinimumSeconds;
        set
        {
            if (SetProperty(ref _cycleRandomMinimumSeconds, Math.Clamp(value, 0, 86400)))
            {
                OnPropertyChanged(nameof(CycleIntervalSummary));
            }
        }
    }

    public int CycleRandomMaximumSeconds
    {
        get => _cycleRandomMaximumSeconds;
        set
        {
            if (SetProperty(ref _cycleRandomMaximumSeconds, Math.Clamp(value, 0, 86400)))
            {
                OnPropertyChanged(nameof(CycleIntervalSummary));
            }
        }
    }

    public Visibility FixedCycleIntervalVisibility => CycleIntervalMode == "固定间隔"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RandomCycleIntervalVisibility => CycleIntervalMode == "随机间隔"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CycleIntervalSummary => CycleIntervalMode == "固定间隔"
        ? CycleFixedIntervalSeconds == 0
            ? "间隔为 0 秒，正反向升级连续执行。"
            : $"每次单次升级完成后固定等待 {CycleFixedIntervalSeconds} 秒。"
        : CycleRandomMinimumSeconds > CycleRandomMaximumSeconds
            ? "随机间隔无效：起始秒数不能大于结束秒数。"
        : CycleRandomMinimumSeconds == 0 && CycleRandomMaximumSeconds == 0
            ? "随机区间为 0 到 0 秒，正反向升级连续执行。"
            : $"每次单次升级完成后随机等待 {CycleRandomMinimumSeconds} 到 {CycleRandomMaximumSeconds} 秒。";

    public long NodePatchLimit { get => _nodePatchLimit; set => SetProperty(ref _nodePatchLimit, Math.Max(1, value)); }

    public long AsyncPatchLimit { get => _asyncPatchLimit; set => SetProperty(ref _asyncPatchLimit, Math.Max(1, value)); }

    public long SyncPatchLimit { get => _syncPatchLimit; set => SetProperty(ref _syncPatchLimit, Math.Max(1, value)); }

    public long GatewayPatchLimit { get => _gatewayPatchLimit; set => SetProperty(ref _gatewayPatchLimit, Math.Max(1, value)); }

    public int DiscoveryFreshnessMinutes { get => _discoveryFreshnessMinutes; set => SetProperty(ref _discoveryFreshnessMinutes, Math.Clamp(value, 1, 1440)); }

    public int MinimumNodeRssi { get => _minimumNodeRssi; set => SetProperty(ref _minimumNodeRssi, Math.Clamp(value, -200, 0)); }

    private PatchCapacityLimits GetPatchCapacityLimits()
        => new(NodePatchLimit, AsyncPatchLimit, SyncPatchLimit, GatewayPatchLimit);

    public string PatchStatus
    {
        get => _patchStatus;
        private set => SetProperty(ref _patchStatus, value);
    }

    public string PatchRestoreTestStatus { get => _patchRestoreTestStatus; private set => SetProperty(ref _patchRestoreTestStatus, value); }

    public string DiffEngineStatus => new NativeBsdiffEngine().GetInfo().StatusMessage;

    public string HttpServiceStatus
    {
        get => _httpServiceStatus;
        private set => SetProperty(ref _httpServiceStatus, value);
    }

    public string HttpServiceAddress => _httpRangeServer.BaseAddress?.ToString() ?? $"http://127.0.0.1:{HttpPort}/";

    public string TaskStatusMessage
    {
        get => _taskStatusMessage;
        private set
        {
            if (!SetProperty(ref _taskStatusMessage, value)) return;
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {value}";
            var merged = string.IsNullOrWhiteSpace(_globalLogText) ? line : _globalLogText + Environment.NewLine + line;
            var lines = merged.Split(Environment.NewLine, StringSplitOptions.None);
            GlobalLogText = lines.Length <= 300 ? merged : string.Join(Environment.NewLine, lines[^300..]);
        }
    }

    public string GlobalLogText { get => _globalLogText; private set => SetProperty(ref _globalLogText, value); }

    public string SftpHost { get => _sftpHost; set => SetProperty(ref _sftpHost, value); }

    public int SftpPort { get => _sftpPort; set => SetProperty(ref _sftpPort, value); }

    public string SftpUserName { get => _sftpUserName; set => SetProperty(ref _sftpUserName, value); }

    public string SftpPassword { get => _sftpPassword; set => SetProperty(ref _sftpPassword, value); }

    public string SftpPrivateKeyPath { get => _sftpPrivateKeyPath; set => SetProperty(ref _sftpPrivateKeyPath, value); }

    public string SftpPrivateKeyPassphrase { get => _sftpPrivateKeyPassphrase; set => SetProperty(ref _sftpPrivateKeyPassphrase, value); }

    public string SftpRemoteDirectory { get => _sftpRemoteDirectory; set => SetProperty(ref _sftpRemoteDirectory, value); }

    public string SftpPublicBaseUrl { get => _sftpPublicBaseUrl; set => SetProperty(ref _sftpPublicBaseUrl, value); }

    public string SftpHostKeySha256 { get => _sftpHostKeySha256; set => SetProperty(ref _sftpHostKeySha256, value); }

    public string PublishStatus
    {
        get => _publishStatus;
        private set
        {
            if (SetProperty(ref _publishStatus, value)) TaskStatusMessage = $"Patch 发布：{value}";
        }
    }

    public bool IsPublishing
    {
        get => _isPublishing;
        private set
        {
            if (!SetProperty(ref _isPublishing, value)) return;
            OnPropertyChanged(nameof(PublishProgressVisibility));
            OnPropertyChanged(nameof(PublishSuccessVisibility));
            OnPropertyChanged(nameof(PublishButtonText));
            OnPropertyChanged(nameof(CanOperatePublication));
        }
    }

    public bool IsTestingPublishConnection
    {
        get => _isTestingPublishConnection;
        private set
        {
            if (!SetProperty(ref _isTestingPublishConnection, value)) return;
            OnPropertyChanged(nameof(TestPublishConnectionButtonText));
            OnPropertyChanged(nameof(CanOperatePublication));
        }
    }

    public bool HasPublishedPatches
    {
        get => _hasPublishedPatches;
        private set
        {
            if (!SetProperty(ref _hasPublishedPatches, value)) return;
            OnPropertyChanged(nameof(PublishSuccessVisibility));
        }
    }

    public Visibility PublishProgressVisibility => IsPublishing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PublishSuccessVisibility => !IsPublishing && HasPublishedPatches ? Visibility.Visible : Visibility.Collapsed;

    public string PublishButtonText => IsPublishing ? "发布中…" : "Patch 发布";

    public string TestPublishConnectionButtonText => IsTestingPublishConnection ? "测试连接中…" : "测试连接";

    public bool CanOperatePublication => !IsPublishing && !IsTestingPublishConnection;

    public string PublishConnectionTestStatus
    {
        get => _publishConnectionTestStatus;
        private set
        {
            if (SetProperty(ref _publishConnectionTestStatus, value)) TaskStatusMessage = $"Patch 连接测试：{value}";
        }
    }

    public string LogAnalyzerExecutablePath { get => _logAnalyzerExecutablePath; set => SetProperty(ref _logAnalyzerExecutablePath, value); }

    public string LogDirectory { get => _logDirectory; set => SetProperty(ref _logDirectory, value); }

    public ObservableCollection<ImportedLogFileItem> ImportedLogFiles { get; } = [];

    public bool HasImportedLogFiles => ImportedLogFiles.Count > 0;

    public string ImportedLogFilesSummary => ImportedLogFiles.Count == 0
        ? "尚未导入 .log 文件"
        : $"本次分析包含 {ImportedLogFiles.Count} 个 .log 文件";

    public string LogAnalysisStatus { get => _logAnalysisStatus; private set => SetProperty(ref _logAnalysisStatus, value); }

    public string LogAnalysisResultText
    {
        get => _logAnalysisResultText;
        private set
        {
            if (!SetProperty(ref _logAnalysisResultText, value)) return;
            LogAnalysisResultLines = BuildLogAnalysisResultLines(value);
        }
    }

    public IReadOnlyList<LogAnalysisLineViewItem> LogAnalysisResultLines
    {
        get => _logAnalysisResultLines;
        private set => SetProperty(ref _logAnalysisResultLines, value);
    }

    public string LogAnalysisQualityScore { get => _logAnalysisQualityScore; private set => SetProperty(ref _logAnalysisQualityScore, value); }

    public string LogAnalysisQualityGrade { get => _logAnalysisQualityGrade; private set => SetProperty(ref _logAnalysisQualityGrade, value); }

    public string LogAnalysisQualitySummary { get => _logAnalysisQualitySummary; private set => SetProperty(ref _logAnalysisQualitySummary, value); }

    public string LogAnalysisQualityColor { get => _logAnalysisQualityColor; private set => SetProperty(ref _logAnalysisQualityColor, value); }

    public string SettingsStatus { get => _settingsStatus; private set => SetProperty(ref _settingsStatus, value); }

    public string DeviceDiscoveryStatus
    {
        get => _deviceDiscoveryStatus;
        private set => SetProperty(ref _deviceDiscoveryStatus, value);
    }

    public string NodeDiscoveryStatus
    {
        get => _nodeDiscoveryStatus;
        private set => SetProperty(ref _nodeDiscoveryStatus, value);
    }

    public bool IsRefreshingExtenders
    {
        get => _isRefreshingExtenders;
        private set
        {
            if (SetProperty(ref _isRefreshingExtenders, value))
            {
                OnPropertyChanged(nameof(IsDiscoveringDevices));
                OnPropertyChanged(nameof(CanRefreshDiscovery));
                OnPropertyChanged(nameof(DeviceDiscoveryButtonText));
            }
        }
    }

    public bool IsRefreshingNodes
    {
        get => _isRefreshingNodes;
        private set
        {
            if (SetProperty(ref _isRefreshingNodes, value))
            {
                OnPropertyChanged(nameof(IsDiscoveringDevices));
                OnPropertyChanged(nameof(CanRefreshDiscovery));
                OnPropertyChanged(nameof(NodeDiscoveryButtonText));
            }
        }
    }

    public bool IsDiscoveringDevices => IsRefreshingExtenders || IsRefreshingNodes;

    private bool IsUpgradeInProgress => _isUpgradeStartInProgress || _isCycleUpgradeRunning || _runner?.HasActiveTask == true;

    public bool CanRefreshDiscovery => IsEcoLink && IsMqttConnected && !IsDiscoveringDevices && !IsUpgradeInProgress;

    public string DeviceDiscoveryButtonText => IsRefreshingExtenders
        ? "刷新中…"
        : SelectedTaskType == GatewayTaskType
            ? "刷新 Gateway"
            : "刷新 Extender";

    public string NodeDiscoveryButtonText => IsRefreshingNodes ? "刷新中…" : "刷新 Node";

    public string ExtenderSelectionToggleText => DiscoveredExtenders.Count > 0 &&
        DiscoveredExtenders.All(item => item.IsSelected)
        ? "全部取消"
        : "全部选择";

    public string NodeSelectionToggleText
    {
        get
        {
            var nodes = DiscoveredNodeGroups.SelectMany(group => group.Nodes).ToArray();
            return nodes.Length > 0 && nodes.All(node => node.IsSelected)
                ? "全部取消"
                : "全部选择";
        }
    }

    public string GatewayStageSummary
    {
        get => _gatewayStageSummary;
        private set => SetProperty(ref _gatewayStageSummary, value);
    }

    public string GatewayStageColor
    {
        get => _gatewayStageColor;
        private set => SetProperty(ref _gatewayStageColor, value);
    }

    public string GatewayPackageSourceSummary
    {
        get => _gatewayPackageSourceSummary;
        private set
        {
            if (!SetProperty(ref _gatewayPackageSourceSummary, value)) return;
            OnPropertyChanged(nameof(GatewayPackageSourceVisibility));
        }
    }

    public string GatewayPackageSourceColor
    {
        get => _gatewayPackageSourceColor;
        private set => SetProperty(ref _gatewayPackageSourceColor, value);
    }

    public Visibility GatewayPackageSourceVisibility =>
        string.IsNullOrWhiteSpace(GatewayPackageSourceSummary)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string UpgradeRunModeText
    {
        get => _upgradeRunModeText;
        private set => SetProperty(ref _upgradeRunModeText, value);
    }

    public string UpgradeRunModeForeground
    {
        get => _upgradeRunModeForeground;
        private set => SetProperty(ref _upgradeRunModeForeground, value);
    }

    public string UpgradeRunModeBackground
    {
        get => _upgradeRunModeBackground;
        private set => SetProperty(ref _upgradeRunModeBackground, value);
    }

    public string UpgradeRunProgressText
    {
        get => _upgradeRunProgressText;
        private set => SetProperty(ref _upgradeRunProgressText, value);
    }

    public string MqttMessageFilter
    {
        get => _mqttMessageFilter;
        set
        {
            if (SetProperty(ref _mqttMessageFilter, value)) OnPropertyChanged(nameof(VisibleMqttMessages));
        }
    }

    public IEnumerable<MqttMessageListItem> VisibleMqttMessages => string.IsNullOrWhiteSpace(MqttMessageFilter)
        ? MqttMessages
        : MqttMessages.Where(item => item.Topic.Contains(MqttMessageFilter, StringComparison.OrdinalIgnoreCase)
            || item.Payload.Contains(MqttMessageFilter, StringComparison.OrdinalIgnoreCase));

    public string PollingToggleText => _runner?.HasActiveTask != true
        ? "轮询已停止"
        : _runner.IsPollingPaused ? "恢复轮询" : "暂停轮询";

    public string GatewayOnlineStatus => _observedGatewayIds.Contains(GatewayId) ? "已观察到上行消息" : "尚未观察到上行消息";

    public bool CanPrepareForApplicationUpdate(out string reason)
    {
        if (_runner?.HasActiveTask == true || _isCycleUpgradeRunning || _isUpgradeStartInProgress)
        {
            reason = "当前存在 OTA 升级任务，请等待任务结束或取消任务后再更新工具。";
            return false;
        }

        if (_isPublishing || _isTestingPublishConnection)
        {
            reason = "当前正在执行 Patch 发布操作，请等待操作结束后再更新工具。";
            return false;
        }

        return ApplicationUpdate.Service.CanInstallInPlace(
            ApplicationUpdate.BuildInfo.InstallDirectory,
            out reason);
    }

    public async Task<(bool Success, string Reason)> PrepareForApplicationUpdateAsync()
    {
        if (!CanPrepareForApplicationUpdate(out var reason))
        {
            return (false, reason);
        }

        await DisposeAsync();
        return (true, string.Empty);
    }

    public bool ConfirmUpdatedStartup(IReadOnlyList<string> arguments, out string? error) =>
        UpdateStartupConfirmation.TryConfirmFromCommandLine(
            arguments,
            UpdatePaths.DefaultUpdateRoot,
            out error);

    public bool RequestCloseApplicationConfirmation()
    {
        if (!IsUpgradeInProgress) return false;
        if (_patchDialogAction == PatchDialogAction.CloseApplication && IsPatchDialogOpen) return true;

        OpenPatchDialog(
            PatchDialogAction.CloseApplication,
            "升级任务仍在进行",
            "当前仍有正在进行的升级任务。\n\n关闭软件将停止工具端状态跟踪；如果正在执行循环升级，后续步骤不会继续。确定关闭吗？",
            "仍然关闭");
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _settingsLoaded = false;
        _settingsAutoSaveCancellation?.Cancel();
        try
        {
            await _settingsAutoSaveTask;
        }
        catch (OperationCanceledException)
        {
            // 关闭时以最后一次完整保存为准。
        }
        await SaveSettingsAsync();
        _settingsAutoSaveCancellation?.Dispose();
        _cycleCancellation?.Cancel();
        if (_runner is not null) await _runner.DisposeAsync();
        await _httpRangeServer.DisposeAsync();
        await _embeddedBroker.DisposeAsync();
        await _mqtt.DisposeAsync();
        ApplicationUpdate.Dispose();
    }

    private async Task InitializeAsync()
    {
        await _reportStore.InitializeAsync();
        await LoadSettingsAsync(_startupSettings);
    }

    private AppSettings LoadStartupSettings()
    {
        try
        {
            return _settingsStore.Load();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void ApplyStartupShellSettings(AppSettings settings)
    {
        var modeKey = string.Equals(settings.ActiveMode, TraditionalModeKey, StringComparison.OrdinalIgnoreCase)
            ? TraditionalModeKey
            : EcoLinkModeKey;
        _isEcoLink = modeKey == EcoLinkModeKey;
        var workspace = settings.ModeWorkspaces?
            .FirstOrDefault(pair => string.Equals(pair.Key, modeKey, StringComparison.OrdinalIgnoreCase))
            .Value;
        _mqttClientUsesLocalBroker = workspace?.MqttClientUsesLocalBroker
            ?? settings.MqttClientUsesLocalBroker;
    }

    private static string NormalizeTaskType(string? taskType) => taskType switch
    {
        "Gateway 升级" or GatewayTaskType => GatewayTaskType,
        "Sync 升级" or SyncTaskType => SyncTaskType,
        "Async 升级" or AsyncTaskType => AsyncTaskType,
        "Node 升级" or NodeTaskType => NodeTaskType,
        _ => GatewayTaskType,
    };

    private string CurrentModeKey => IsEcoLink ? EcoLinkModeKey : TraditionalModeKey;

    private ModeWorkspaceSettings GetCurrentModeWorkspace()
    {
        if (_modeWorkspaces.TryGetValue(CurrentModeKey, out var workspace))
        {
            return workspace;
        }

        workspace = new ModeWorkspaceSettings();
        _modeWorkspaces[CurrentModeKey] = workspace;
        return workspace;
    }

    private void SaveCurrentModeUiState()
    {
        _modeWorkspaces[CurrentModeKey] = CaptureCurrentModeWorkspace();
        SaveCurrentModeSecrets();

        var state = IsEcoLink ? _ecoLinkUpgradeUiState : _traditionalUpgradeUiState;
        state.TaskStatusMessage = TaskStatusMessage;
        state.GlobalLogText = GlobalLogText;
        state.GatewayStageSummary = GatewayStageSummary;
        state.GatewayStageColor = GatewayStageColor;
        state.LastGatewayStatus = _lastGatewayStatus;
        state.GatewayTaskSequence = _gatewayTaskSequence;
        state.GatewayTaskStartedAt = _gatewayTaskStartedAt;
        state.GatewayStatusDeviceType = _gatewayStatusDeviceType;
        state.UpgradeRunModeText = UpgradeRunModeText;
        state.UpgradeRunModeForeground = UpgradeRunModeForeground;
        state.UpgradeRunModeBackground = UpgradeRunModeBackground;
        state.UpgradeRunProgressText = UpgradeRunProgressText;
        state.DeviceDiscoveryStatus = DeviceDiscoveryStatus;
        state.NodeDiscoveryStatus = NodeDiscoveryStatus;
        state.LogAnalysisStatus = LogAnalysisStatus;
        state.LogAnalysisResultText = LogAnalysisResultText;
        state.LogAnalysisQualityScore = LogAnalysisQualityScore;
        state.LogAnalysisQualityGrade = LogAnalysisQualityGrade;
        state.LogAnalysisQualitySummary = LogAnalysisQualitySummary;
        state.LogAnalysisQualityColor = LogAnalysisQualityColor;
        state.SettingsStatus = SettingsStatus;
        state.MqttMessages = MqttMessages.ToArray();
        state.MqttMessageFilter = MqttMessageFilter;
        state.GatewaySubscriptionStatus = GatewaySubscriptionStatus;
        state.SubscribedGatewayTopic = _subscribedGatewayTopic;
        state.ObservedGatewayIds = _observedGatewayIds.ToArray();
        state.SelectedReportId = SelectedReport?.Id;
        state.ImportedPatchPath = _importedPatchPath;
        state.ImportedPatchLength = _importedPatchLength;
        state.ImportedPatchMd5 = _importedPatchMd5;
        state.ImportedPatchSha256 = _importedPatchSha256;
        state.OldImagePath = _oldImagePath;
        state.NewImagePath = _newImagePath;
        state.OldImageSha256 = _oldImageSha256;
        state.NewImageSha256 = _newImageSha256;
        state.OldFirmwareIdentity = _oldFirmwareIdentity;
        state.NewFirmwareIdentity = _newFirmwareIdentity;
        state.AreFirmwareImagesCompatible = _areFirmwareImagesCompatible;
        state.PatchPath = _patchPath;
        state.PatchUrl = _patchUrl;
        state.PatchMd5 = _patchMd5;
        state.PatchSha256 = _patchSha256;
        state.PatchLength = _patchLength;
        state.PatchManifestVerified = _patchManifestVerified;
        state.ReversePatchPath = _reversePatchPath;
        state.ReversePatchUrl = _reversePatchUrl;
        state.ReversePatchMd5 = _reversePatchMd5;
        state.ReversePatchSha256 = _reversePatchSha256;
        state.ReversePatchLength = _reversePatchLength;
        state.SelectedPatchManifest = _selectedPatchManifest;
        state.SelectedRestorePatchPath = SelectedRestorePatch?.FilePath ?? string.Empty;
        state.SelectedPatchRestoreDirection = SelectedPatchRestoreDirection;
        state.PatchStatus = PatchStatus;
        state.PatchRestoreTestStatus = PatchRestoreTestStatus;
        state.PublishStatus = PublishStatus;
        state.PublishConnectionTestStatus = PublishConnectionTestStatus;
        state.HasPublishedPatches = HasPublishedPatches;
    }

    private void RestoreCurrentModeUpgradeUiState()
    {
        var state = IsEcoLink ? _ecoLinkUpgradeUiState : _traditionalUpgradeUiState;
        _taskStatusMessage = state.TaskStatusMessage;
        OnPropertyChanged(nameof(TaskStatusMessage));
        _globalLogText = state.GlobalLogText;
        OnPropertyChanged(nameof(GlobalLogText));
        GatewayStageSummary = state.GatewayStageSummary;
        GatewayStageColor = state.GatewayStageColor;
        _lastGatewayStatus = state.LastGatewayStatus;
        _gatewayTaskSequence = state.GatewayTaskSequence;
        _gatewayTaskStartedAt = state.GatewayTaskStartedAt;
        _gatewayStatusDeviceType = state.GatewayStatusDeviceType;
        UpgradeRunModeText = state.UpgradeRunModeText;
        UpgradeRunModeForeground = state.UpgradeRunModeForeground;
        UpgradeRunModeBackground = state.UpgradeRunModeBackground;
        UpgradeRunProgressText = state.UpgradeRunProgressText;
        DeviceDiscoveryStatus = state.DeviceDiscoveryStatus;
        NodeDiscoveryStatus = state.NodeDiscoveryStatus;
        LogAnalysisStatus = state.LogAnalysisStatus;
        LogAnalysisResultText = state.LogAnalysisResultText;
        LogAnalysisQualityScore = state.LogAnalysisQualityScore;
        LogAnalysisQualityGrade = state.LogAnalysisQualityGrade;
        LogAnalysisQualitySummary = state.LogAnalysisQualitySummary;
        LogAnalysisQualityColor = state.LogAnalysisQualityColor;
        SettingsStatus = state.SettingsStatus;
        MqttMessages.Clear();
        foreach (var message in state.MqttMessages)
        {
            MqttMessages.Add(message);
        }
        MqttMessageFilter = state.MqttMessageFilter;
        GatewaySubscriptionStatus = state.GatewaySubscriptionStatus;
        _subscribedGatewayTopic = state.SubscribedGatewayTopic;
        _observedGatewayIds.Clear();
        foreach (var gatewayId in state.ObservedGatewayIds)
        {
            _observedGatewayIds.Add(gatewayId);
        }
        OnPropertyChanged(nameof(VisibleMqttMessages));
        OnPropertyChanged(nameof(GatewayOnlineStatus));
        OnPropertyChanged(nameof(IsGatewayTopicSubscribed));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeText));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeBackground));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeForeground));

        _importedPatchPath = state.ImportedPatchPath;
        _importedPatchLength = state.ImportedPatchLength;
        _importedPatchMd5 = state.ImportedPatchMd5;
        _importedPatchSha256 = state.ImportedPatchSha256;
        _oldImagePath = state.OldImagePath;
        _newImagePath = state.NewImagePath;
        _oldImageSha256 = state.OldImageSha256;
        _newImageSha256 = state.NewImageSha256;
        _oldFirmwareIdentity = state.OldFirmwareIdentity;
        _newFirmwareIdentity = state.NewFirmwareIdentity;
        _areFirmwareImagesCompatible = state.AreFirmwareImagesCompatible;
        _patchPath = state.PatchPath;
        _patchUrl = state.PatchUrl;
        _patchMd5 = state.PatchMd5;
        _patchSha256 = state.PatchSha256;
        _patchLength = state.PatchLength;
        _patchManifestVerified = state.PatchManifestVerified;
        _reversePatchPath = state.ReversePatchPath;
        _reversePatchUrl = state.ReversePatchUrl;
        _reversePatchMd5 = state.ReversePatchMd5;
        _reversePatchSha256 = state.ReversePatchSha256;
        _reversePatchLength = state.ReversePatchLength;
        _selectedPatchManifest = state.SelectedPatchManifest;
        _selectedPatchRestoreDirection = state.SelectedPatchRestoreDirection;
        PatchStatus = state.PatchStatus;
        PatchRestoreTestStatus = state.PatchRestoreTestStatus;
        _publishStatus = state.PublishStatus;
        _publishConnectionTestStatus = state.PublishConnectionTestStatus;
        _hasPublishedPatches = state.HasPublishedPatches;
        SelectedRestorePatch = _patchCatalog.Values.FirstOrDefault(item =>
            string.Equals(item.FilePath, state.SelectedRestorePatchPath, StringComparison.OrdinalIgnoreCase));
        NotifyPatchWorkspaceChanged();

        GatewayStages.Clear();
        GatewaySubtasks.Clear();
        GatewayPackageSourceSummary = string.Empty;
        if (_lastGatewayStatus is not null)
        {
            UpdateGatewayStatus(_lastGatewayStatus);
        }
    }

    private void NotifyPatchWorkspaceChanged()
    {
        OnPropertyChanged(nameof(ImportedPatchFileName));
        OnPropertyChanged(nameof(ImportedPatchMetadataDetail));
        OnPropertyChanged(nameof(OldImageFileName));
        OnPropertyChanged(nameof(NewImageFileName));
        OnPropertyChanged(nameof(OldImageIdentityDetail));
        OnPropertyChanged(nameof(NewImageIdentityDetail));
        OnPropertyChanged(nameof(CanGeneratePatch));
        OnPropertyChanged(nameof(PatchFileName));
        OnPropertyChanged(nameof(PatchDetail));
        OnPropertyChanged(nameof(PatchMetadataDetail));
        OnPropertyChanged(nameof(PatchUrl));
        OnPropertyChanged(nameof(SelectedHttpPatchUrl));
        OnPropertyChanged(nameof(ReversePatchFileName));
        OnPropertyChanged(nameof(ReversePatchStatus));
        OnPropertyChanged(nameof(ReversePatchMetadataDetail));
        OnPropertyChanged(nameof(SelectedPatchRestoreDirection));
        OnPropertyChanged(nameof(PublishStatus));
        OnPropertyChanged(nameof(PublishConnectionTestStatus));
        OnPropertyChanged(nameof(HasPublishedPatches));
        OnPropertyChanged(nameof(PublishSuccessVisibility));
    }

    private ModeWorkspaceSettings CaptureCurrentModeWorkspace() => new()
    {
        SelectedPageName = SelectedPage?.Name ?? "MQTT 配置",
        MqttHost = MqttHost,
        MqttPort = MqttPort,
        MqttClientUsesLocalBroker = MqttClientUsesLocalBroker,
        LocalBrokerPort = LocalBrokerPort,
        LocalBrokerUserName = LocalBrokerUserName,
        HttpRootDirectory = GetPatchOutputDirectory(),
        HttpPort = HttpPort,
        HttpUsesLocalServer = HttpUsesLocalServer,
        PublicHttpBaseUrl = PublicHttpBaseUrl,
        MqttUseTls = MqttUseTls,
        MqttAcceptAnyServerCertificate = MqttAcceptAnyServerCertificate,
        MqttUserName = MqttUserName,
        SftpHost = SftpHost,
        SftpPort = SftpPort,
        SftpUserName = SftpUserName,
        SftpPrivateKeyPath = SftpPrivateKeyPath,
        SftpRemoteDirectory = SftpRemoteDirectory,
        SftpPublicBaseUrl = SftpPublicBaseUrl,
        SftpHostKeySha256 = SftpHostKeySha256,
        LogAnalyzerExecutablePath = LogAnalyzerExecutablePath,
        LogDirectory = LogDirectory,
        SelectedTaskType = SelectedTaskType,
        OldVersion = OldVersion,
        NewVersion = NewVersion,
        ForwardPatchName = ForwardPatchName,
        ReversePatchName = ReversePatchName,
        IsSpecifiedTarget = IsSpecifiedTarget,
        TargetIdList = TargetIdList,
        NodeType = NodeType,
        CustomNodeTypes = NodeTypeCatalog.CustomOptions
            .Select(item => new NodeTypeDefinitionSettings(item.Value, item.Name))
            .ToArray(),
        NodeTargetsText = NodeTargetsText,
        GatewayId = GatewayId,
        GatewayIdHistory = GatewayIdHistory.ToArray(),
        CycleRounds = CycleRounds,
        CycleIntervalMode = CycleIntervalMode,
        CycleFixedIntervalSeconds = CycleFixedIntervalSeconds,
        CycleRandomMinimumSeconds = CycleRandomMinimumSeconds,
        CycleRandomMaximumSeconds = CycleRandomMaximumSeconds,
        NodePatchLimit = NodePatchLimit,
        AsyncPatchLimit = AsyncPatchLimit,
        SyncPatchLimit = SyncPatchLimit,
        GatewayPatchLimit = GatewayPatchLimit,
        DiscoveryFreshnessMinutes = DiscoveryFreshnessMinutes,
        MinimumNodeRssi = MinimumNodeRssi,
        SelectedUpgradePatchPath = SelectedUpgradePatch?.FilePath ?? string.Empty,
        SelectedReverseUpgradePatchPath = SelectedReverseUpgradePatch?.FilePath ?? string.Empty,
        DiscoveredExtenders = DiscoveredExtenders.Select(extender => new DiscoveredExtenderSettings(
            extender.ExtenderId,
            extender.Detail,
            extender.DeviceType,
            extender.SoftwareVersion,
            extender.IsSelected,
            extender.AsyncSoftwareVersion,
            extender.AsyncAddress,
            extender.SyncRssi,
            extender.SyncSnr,
            extender.OnlineCount,
            extender.TotalCount)).ToArray(),
        DiscoveredNodeGroups = DiscoveredNodeGroups.Select(group => new DiscoveredNodeGroupSettings(
            group.ExtenderId,
            group.Nodes.Select(node => new DiscoveredNodeSettings(
                node.NodeId,
                node.NodeType,
                node.SoftwareVersion,
                node.Rssi,
                node.IsSelected)).ToArray(),
            group.Error,
            group.ReportedCount)).ToArray(),
        NodeDiscoveryCompletedAt = _nodeDiscoveryCompletedAt,
        ShowArchivedReports = _showArchivedReports,
    };

    private void ApplyCurrentModeWorkspace()
    {
        var workspace = GetCurrentModeWorkspace();
        _restoringModeWorkspace = true;
        try
        {
            MqttHost = workspace.MqttHost;
            MqttPort = workspace.MqttPort;
            MqttClientUsesLocalBroker = workspace.MqttClientUsesLocalBroker;
            LocalBrokerPort = workspace.LocalBrokerPort;
            LocalBrokerUserName = workspace.LocalBrokerUserName;
            if (!string.IsNullOrWhiteSpace(workspace.HttpRootDirectory)) PatchOutputDirectory = workspace.HttpRootDirectory;
            HttpPort = workspace.HttpPort;
            HttpUsesLocalServer = workspace.HttpUsesLocalServer;
            PublicHttpBaseUrl = string.IsNullOrWhiteSpace(workspace.PublicHttpBaseUrl)
                ? "http://117.172.29.2:36109/download/"
                : workspace.PublicHttpBaseUrl;
            MqttUseTls = workspace.MqttUseTls;
            MqttAcceptAnyServerCertificate = workspace.MqttAcceptAnyServerCertificate;
            MqttUserName = workspace.MqttUserName;
            SftpHost = string.IsNullOrWhiteSpace(workspace.SftpHost) ? "117.172.29.2" : workspace.SftpHost;
            SftpPort = workspace.SftpPort is <= 0 or 22 ? 36112 : workspace.SftpPort;
            SftpUserName = string.IsNullOrWhiteSpace(workspace.SftpUserName) ? "root" : workspace.SftpUserName;
            SftpPrivateKeyPath = workspace.SftpPrivateKeyPath;
            SftpRemoteDirectory = string.Equals(workspace.SftpRemoteDirectory?.Trim(), "/ota", StringComparison.OrdinalIgnoreCase)
                ? "/opt/www/static/download/"
                : string.IsNullOrWhiteSpace(workspace.SftpRemoteDirectory) ? "/opt/www/static/download/" : workspace.SftpRemoteDirectory;
            SftpPublicBaseUrl = workspace.SftpPublicBaseUrl;
            SftpHostKeySha256 = workspace.SftpHostKeySha256;
            LogAnalyzerExecutablePath = File.Exists(workspace.LogAnalyzerExecutablePath)
                ? workspace.LogAnalyzerExecutablePath
                : GetDefaultLogAnalyzerPath();
            LogDirectory = workspace.LogDirectory;
            LoadImportedLogFiles();
            SelectedTaskType = TaskTypes.Contains(workspace.SelectedTaskType) ? workspace.SelectedTaskType : TaskTypes[0];
            OldVersion = workspace.OldVersion;
            NewVersion = workspace.NewVersion;
            ForwardPatchName = string.IsNullOrWhiteSpace(workspace.ForwardPatchName) ? "a-to-b" : workspace.ForwardPatchName;
            ReversePatchName = string.IsNullOrWhiteSpace(workspace.ReversePatchName) ? "b-to-a" : workspace.ReversePatchName;
            IsSpecifiedTarget = workspace.IsSpecifiedTarget;
            TargetIdList = workspace.TargetIdList;
            NodeTypeCatalog.ReplaceCustom(workspace.CustomNodeTypes ?? []);
            NodeType = NodeTypeCatalog.IsSelectable(workspace.NodeType) ? workspace.NodeType : 5;
            NodeTargetsText = workspace.NodeTargetsText;
            RestoreGatewayIdHistory(workspace.GatewayIdHistory, workspace.GatewayId);
            GatewayId = workspace.GatewayId;
            CycleRounds = workspace.CycleRounds > 0 ? workspace.CycleRounds : 1;
            CycleIntervalMode = CycleIntervalModes.Contains(workspace.CycleIntervalMode) ? workspace.CycleIntervalMode : CycleIntervalModes[0];
            CycleFixedIntervalSeconds = workspace.CycleFixedIntervalSeconds;
            CycleRandomMinimumSeconds = workspace.CycleRandomMinimumSeconds;
            CycleRandomMaximumSeconds = workspace.CycleRandomMaximumSeconds;
            NodePatchLimit = workspace.NodePatchLimit > 0 ? workspace.NodePatchLimit : PatchCapacityPolicy.NodePatchLimit;
            AsyncPatchLimit = workspace.AsyncPatchLimit > 0 ? workspace.AsyncPatchLimit : PatchCapacityPolicy.AsyncPatchLimit;
            SyncPatchLimit = workspace.SyncPatchLimit is > 0 and < long.MaxValue ? workspace.SyncPatchLimit : PatchCapacityPolicy.SyncPatchLimit;
            GatewayPatchLimit = workspace.GatewayPatchLimit is > 0 and < long.MaxValue ? workspace.GatewayPatchLimit : PatchCapacityPolicy.GatewayPatchLimit;
            DiscoveryFreshnessMinutes = workspace.DiscoveryFreshnessMinutes > 0 ? workspace.DiscoveryFreshnessMinutes : 30;
            MinimumNodeRssi = workspace.MinimumNodeRssi is >= -200 and <= 0 ? workspace.MinimumNodeRssi : -100;
            _nodeDiscoveryCompletedAt = workspace.NodeDiscoveryCompletedAt;
            RestoreDiscoveryCollections(workspace);
            _showArchivedReports = workspace.ShowArchivedReports;
            NotifyReportScopeChanged();
            LoadCurrentModeSecrets();
        }
        finally
        {
            _restoringModeWorkspace = false;
        }
    }

    private void RestoreDiscoveryCollections(ModeWorkspaceSettings workspace)
    {
        _suppressSelectionSync = true;
        try
        {
            DiscoveredExtenders.Clear();
            foreach (var extender in workspace.DiscoveredExtenders ?? [])
            {
                DiscoveredExtenders.Add(new SelectableExtenderItem(
                    extender.ExtenderId, extender.Detail, extender.DeviceType, extender.SoftwareVersion,
                    extender.AsyncSoftwareVersion,
                    extender.AsyncAddress,
                    extender.SyncRssi,
                    extender.SyncSnr,
                    extender.OnlineCount,
                    extender.TotalCount,
                    extender.IsSelected, OnExtenderSelectionChanged));
            }
            DiscoveredNodeGroups.Clear();
            foreach (var group in workspace.DiscoveredNodeGroups ?? [])
            {
                var nodes = group.Nodes ?? [];
                DiscoveredNodeGroups.Add(new NodeGroupItem(
                    group.ExtenderId,
                    nodes.Select(node => new GatewayNodeInfo(node.NodeId, node.NodeType, node.SoftwareVersion, node.Rssi)).ToArray(),
                    nodes.Where(node => node.IsSelected).Select(node => node.NodeId).ToHashSet(),
                    group.Error,
                    group.ReportedCount ?? nodes.Count,
                    OnNodeSelectionChanged));
            }
            RefreshNodeTypeOptions();
        }
        finally
        {
            _suppressSelectionSync = false;
        }
        OnPropertyChanged(nameof(ExtenderSelectionToggleText));
        OnPropertyChanged(nameof(NodeSelectionToggleText));
        RefreshNodeEligibility();
        if (_selectedNodeTypeValue > 0 && DiscoveredNodeGroups.Count > 0)
        {
            SelectNodesByType(_selectedNodeTypeValue);
        }
    }

    private async Task RestoreCurrentModePatchCatalogAsync()
    {
        var modeKey = CurrentModeKey;
        var workspace = GetCurrentModeWorkspace();
        await LoadPatchCatalogFromOutputDirectoryAsync();
        if (!string.Equals(modeKey, CurrentModeKey, StringComparison.Ordinal)) return;
        SelectedUpgradePatch = UpgradePatchChoices.FirstOrDefault(item =>
            string.Equals(item.FilePath, workspace.SelectedUpgradePatchPath, StringComparison.OrdinalIgnoreCase));
        SelectedReverseUpgradePatch = UpgradePatchChoices.FirstOrDefault(item =>
            string.Equals(item.FilePath, workspace.SelectedReverseUpgradePatchPath, StringComparison.OrdinalIgnoreCase));
        var state = IsEcoLink ? _ecoLinkUpgradeUiState : _traditionalUpgradeUiState;
        SelectedRestorePatch = _patchCatalog.Values.FirstOrDefault(item =>
            string.Equals(item.FilePath, state.SelectedRestorePatchPath, StringComparison.OrdinalIgnoreCase));
        _selectedPatchRestoreDirection = state.SelectedPatchRestoreDirection;
        OnPropertyChanged(nameof(SelectedPatchRestoreDirection));
        _taskStatusMessage = state.TaskStatusMessage;
        OnPropertyChanged(nameof(TaskStatusMessage));
    }

    private string ModeSecretName(string suffix) => $"OtaTool/{CurrentModeKey}/{suffix}";

    private void LoadCurrentModeSecrets()
    {
        MqttPassword = ReadModeSecret("MqttPassword", "OtaTool/MqttPassword");
        LocalBrokerPassword = ReadModeSecret("LocalBrokerPassword", "OtaTool/LocalBrokerPassword");
        SftpPassword = ReadModeSecret("SftpPassword", "OtaTool/SftpPassword");
        SftpPrivateKeyPassphrase = ReadModeSecret("SftpPrivateKeyPassphrase", "OtaTool/SftpPrivateKeyPassphrase");
    }

    private string ReadModeSecret(string suffix, string legacyName)
    {
        if (_secretStore.TryGet(ModeSecretName(suffix), out var value)) return value ?? string.Empty;
        return _secretStore.TryGet(legacyName, out value) ? value ?? string.Empty : string.Empty;
    }

    private void SaveCurrentModeSecrets()
    {
        if (!string.IsNullOrEmpty(MqttPassword)) _secretStore.Save(ModeSecretName("MqttPassword"), MqttPassword);
        if (!string.IsNullOrEmpty(LocalBrokerPassword)) _secretStore.Save(ModeSecretName("LocalBrokerPassword"), LocalBrokerPassword);
        if (!string.IsNullOrEmpty(SftpPassword)) _secretStore.Save(ModeSecretName("SftpPassword"), SftpPassword);
        if (!string.IsNullOrEmpty(SftpPrivateKeyPassphrase)) _secretStore.Save(ModeSecretName("SftpPrivateKeyPassphrase"), SftpPrivateKeyPassphrase);
    }

    private void ApplyMode(bool restoreSelectedPage = true)
    {
        var workspace = GetCurrentModeWorkspace();
        NavigationItems.Clear();
        AddNavigation("01", "MQTT 配置");
        AddNavigation("02", "PATCH 中心");
        AddNavigation("03", "升级任务");
        AddNavigation("04", "历史报告");
        if (IsEcoLink)
        {
            AddNavigation("05", "日志分析");
        }
        AddNavigation(IsEcoLink ? "06" : "05", "系统设置");

        TaskTypes.Clear();
        TaskTypes.Add(GatewayTaskType);
        TaskTypes.Add(SyncTaskType);
        if (IsEcoLink)
        {
            TaskTypes.Add(AsyncTaskType);
            TaskTypes.Add(NodeTaskType);
        }

        var modeTaskType = IsEcoLink ? _ecoLinkSelectedTaskType : _traditionalSelectedTaskType;
        SelectedTaskType = TaskTypes.Contains(modeTaskType) ? modeTaskType : TaskTypes[0];
        if (RequiresSpecifiedTarget)
        {
            IsSpecifiedTarget = true;
        }

        SelectedPage = restoreSelectedPage
            ? NavigationItems.FirstOrDefault(item => item.Name == workspace.SelectedPageName)
                ?? NavigationItems.FirstOrDefault(item => item.Name == "MQTT 配置")
            : NavigationItems.FirstOrDefault(item => item.Name == "MQTT 配置");
        OnPropertyChanged(nameof(CurrentPageSubtitle));
        OnPropertyChanged(nameof(ModeBadge));
        OnPropertyChanged(nameof(EcoLinkVisibility));
        OnPropertyChanged(nameof(NodeTaskVisibility));
        OnPropertyChanged(nameof(ExtenderSelectionVisibility));
        OnPropertyChanged(nameof(ExtenderTargetListVisibility));
        OnPropertyChanged(nameof(DeviceDiscoveryButtonText));
        OnPropertyChanged(nameof(NodeDiscoveryVisibility));
        OnPropertyChanged(nameof(TargetScopeVisibility));
        OnPropertyChanged(nameof(SpecifiedTargetVisibility));
        OnPropertyChanged(nameof(BroadcastTargetVisibility));
        OnPropertyChanged(nameof(EnvironmentPageVisibility));
        OnPropertyChanged(nameof(PatchPageVisibility));
        OnPropertyChanged(nameof(TaskPageVisibility));
        OnPropertyChanged(nameof(LogPageVisibility));
        OnPropertyChanged(nameof(ReportsPageVisibility));
        OnPropertyChanged(nameof(SettingsPageVisibility));
    }

    private void AddNavigation(string index, string name) => NavigationItems.Add(new NavigationItem(index, name));

    private void NotifyUpgradeActionAvailability()
    {
        OnPropertyChanged(nameof(CanStartUpgrade));
        OnPropertyChanged(nameof(CanStartForwardUpgrade));
        OnPropertyChanged(nameof(CanStartReverseUpgrade));
        OnPropertyChanged(nameof(CanStartCycleUpgrade));
        OnPropertyChanged(nameof(CanRefreshDiscovery));
        OnPropertyChanged(nameof(CanCancelTask));
    }

    private void ToggleExtenderSelection(object? _)
    {
        if (DiscoveredExtenders.Count == 0)
        {
            DeviceDiscoveryStatus = "暂无可选择的 Extender，请先刷新。";
            return;
        }
        var selected = DiscoveredExtenders.Any(item => !item.IsSelected);
        _suppressSelectionSync = true;
        foreach (var extender in DiscoveredExtenders)
        {
            extender.IsSelected = selected;
        }
        _suppressSelectionSync = false;
        OnExtenderSelectionChanged();
        DeviceDiscoveryStatus = selected
            ? $"已选择全部 {DiscoveredExtenders.Count} 个 Extender。"
            : "已取消全部 Extender。";
    }

    private void Navigate(object? parameter)
    {
        if (parameter is string pageName)
        {
            SelectedPage = NavigationItems.FirstOrDefault(item => item.Name == pageName);
        }
    }

    private async void SelectPatch(object? _)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 OTA 升级文件",
            Filter = "升级文件|*.patch;*.bin|Patch 文件|*.patch|Gateway 完整镜像|*.bin|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var extension = Path.GetExtension(dialog.FileName);
            var isFullImage = string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase);
            if (!isFullImage && !string.Equals(extension, ".patch", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("仅支持 .patch 差分包或 .bin Gateway 完整镜像。");
            }

            if (isFullImage)
            {
                var firmware = await FirmwareIdentityReader.ReadAsync(dialog.FileName);
                if (firmware.DeviceType != FirmwareDeviceType.Gateway)
                {
                    throw new InvalidOperationException($"完整镜像升级只支持 Gateway，当前文件识别为{firmware.DisplayName}（类型 {firmware.DeviceTypeCode}）。");
                }
            }

            var metadata = await PatchMetadata.FromFileAsync(dialog.FileName);
            var manifestVerified = isFullImage;
            DeviceType? patchDeviceType = isFullImage ? DeviceType.Gateway : null;
            var manifestNote = isFullImage
                ? " · Gateway 完整镜像（传统模式）"
                : " · 缺少 Package Manifest，请导入 A/B 固件并执行还原测试";
            if (!isFullImage && File.Exists(metadata.FilePath + ".json"))
            {
                manifestNote = await ApplySidecarManifestAsync(metadata.FilePath);
                manifestVerified = true;
                patchDeviceType = _selectedPatchManifest?.OtaDeviceType;
            }
            _importedPatchPath = await CopyPatchToOutputDirectoryAsync(metadata);
            var publishedMetadata = await PatchMetadata.FromFileAsync(_importedPatchPath);
            _importedPatchMd5 = publishedMetadata.Md5;
            _importedPatchSha256 = publishedMetadata.Sha256;
            _importedPatchLength = publishedMetadata.Length;
            PatchStatus = isFullImage
                ? $"已导入 Gateway 完整镜像 · {publishedMetadata.Length:N0} B · SHA256 {publishedMetadata.Sha256[..12]}…"
                : manifestVerified
                ? $"已校验 · {publishedMetadata.Length:N0} B · SHA256 {publishedMetadata.Sha256[..12]}…{manifestNote}"
                : $"已导入待验证 · {publishedMetadata.Length:N0} B · SHA256 {publishedMetadata.Sha256[..12]}…{manifestNote}";
            TaskStatusMessage = isFullImage
                ? "Gateway 完整镜像已保存到输出目录，可在 EcoLink 或传统模式下直接用于升级。"
                : manifestVerified
                ? "Patch 已保存到输出目录；启动本地 HTTP Range 服务后即可下载。"
                : "Patch 已加入详情列表；请导入对应 A/B 固件并执行“导入 Patch 验证”，验证通过后才能升级或发布。";
            OnPropertyChanged(nameof(ImportedPatchFileName));
            OnPropertyChanged(nameof(ImportedPatchMetadataDetail));
            RegisterPatch(
                isFullImage ? "导入完整镜像" : "导入 Patch",
                _importedPatchPath,
                _importedPatchLength,
                _importedPatchMd5,
                _importedPatchSha256,
                manifestVerified: manifestVerified,
                isFullImage: isFullImage,
                otaDeviceType: patchDeviceType,
                oldVersion: manifestVerified && !isFullImage ? _selectedPatchManifest?.OldVersion : null,
                newVersion: manifestVerified && !isFullImage ? _selectedPatchManifest?.NewVersion : null);
            RefreshUpgradePatchChoices();
        }
        catch (Exception exception)
        {
            PatchStatus = $"导入失败：{exception.Message}";
            TaskStatusMessage = "Patch 导入失败。";
        }
    }

    private void BrowsePatchOutputDirectory(object? _)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Patch 输出目录",
            InitialDirectory = GetPatchOutputDirectory(),
        };
        if (dialog.ShowDialog() == true)
        {
            PatchOutputDirectory = dialog.FolderName;
        }
    }

    private void BrowseLogDirectory(object? _)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择日志目录",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() == true)
        {
            LogDirectory = dialog.FolderName;
            LoadImportedLogFiles();
            LogAnalysisStatus = ImportedLogFiles.Count == 0
                ? "所选目录中没有 .log 文件。"
                : $"已导入 {ImportedLogFiles.Count} 个 .log 文件，可删除不参与本次分析的文件。";
        }
    }

    private void LoadImportedLogFiles()
    {
        ImportedLogFiles.Clear();
        if (Directory.Exists(LogDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "*.log", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                var file = new FileInfo(path);
                ImportedLogFiles.Add(new ImportedLogFileItem(file.FullName, file.Length, file.LastWriteTime));
            }
        }
        NotifyImportedLogFilesChanged();
    }

    private void RemoveImportedLogFile(object? parameter)
    {
        if (parameter is not ImportedLogFileItem item || !ImportedLogFiles.Remove(item)) return;
        NotifyImportedLogFilesChanged();
        LogAnalysisStatus = $"已从本次分析列表删除 {item.FileName}，磁盘源文件未删除。";
    }

    private void NotifyImportedLogFilesChanged()
    {
        OnPropertyChanged(nameof(HasImportedLogFiles));
        OnPropertyChanged(nameof(ImportedLogFilesSummary));
    }

    private void OpenPatchOutputDirectory(object? _)
    {
        try
        {
            var directory = GetPatchOutputDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            TaskStatusMessage = $"已打开 Patch 输出目录：{directory}";
        }
        catch (Exception exception)
        {
            PatchStatus = $"打开 Patch 输出目录失败：{exception.Message}";
        }
    }

    private async Task SelectFirmwareImageAsync(bool isOldImage)
    {
        var dialog = new OpenFileDialog
        {
            Title = isOldImage ? "导入 A 版本固件" : "导入 B 版本固件",
            Filter = "固件文件|*.bin;*.hex;*.img;*.fw|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        FirmwareIdentity identity;
        try
        {
            identity = await FirmwareIdentityReader.ReadAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"固件导入失败：{exception.Message}";
            return;
        }

        var otherHash = isOldImage ? _newImageSha256 : _oldImageSha256;
        if (!string.IsNullOrWhiteSpace(otherHash) &&
            string.Equals(
                identity.Sha256,
                otherHash,
                StringComparison.OrdinalIgnoreCase))
        {
            const string message = "A/B 镜像内容相同，请重新导入不同版本镜像。";
            TaskStatusMessage = message;
            PatchStatus = $"制作失败：{message}";
            ShowInformationDialog("镜像相同", message);
            return;
        }

        if (isOldImage)
        {
            _oldImagePath = dialog.FileName;
            _oldImageSha256 = identity.Sha256;
            _oldFirmwareIdentity = identity;
            OnPropertyChanged(nameof(OldImageFileName));
            OnPropertyChanged(nameof(OldImageIdentityDetail));
        }
        else
        {
            _newImagePath = dialog.FileName;
            _newImageSha256 = identity.Sha256;
            _newFirmwareIdentity = identity;
            OnPropertyChanged(nameof(NewImageFileName));
            OnPropertyChanged(nameof(NewImageIdentityDetail));
        }
        ApplyFirmwareIdentityDefaults();
        OnPropertyChanged(nameof(CanGeneratePatch));
        if (CanGeneratePatch)
        {
            PatchStatus = "A/B 固件已就绪，可以制作 Patch。";
        }
        else if (_oldFirmwareIdentity is null || _newFirmwareIdentity is null)
        {
            PatchStatus = isOldImage
                ? "A 版本固件已导入，请继续导入 B 版本固件。"
                : "B 版本固件已导入，请继续导入 A 版本固件。";
        }
        TaskStatusMessage = isOldImage ? "A 版本固件已导入。" : "B 版本固件已导入。";
    }

    private static string FormatFirmwareIdentity(FirmwareIdentity? identity)
        => identity is null
            ? "尚未识别固件身份"
            : $"{identity.DisplayName}（类型 {identity.DeviceTypeCode}） · {identity.VersionText}" +
              (identity.IsLegacyEcoMarker ? " · 旧 ECO 标识，版本需手动填写" : string.Empty);

    private void ApplyFirmwareIdentityDefaults()
    {
        _areFirmwareImagesCompatible = false;
        if (_oldFirmwareIdentity is null || _newFirmwareIdentity is null)
        {
            return;
        }

        try
        {
            _oldFirmwareIdentity.EnsureCompatibleWith(_newFirmwareIdentity);
            _areFirmwareImagesCompatible = true;
            SelectedTaskType = _oldFirmwareIdentity.OtaDeviceType switch
            {
                DeviceType.Gateway => GatewayTaskType,
                DeviceType.Sync => SyncTaskType,
                DeviceType.Async => AsyncTaskType,
                DeviceType.Node => NodeTaskType,
                _ => SelectedTaskType,
            };
            if (_oldFirmwareIdentity.IsNode)
            {
                NodeType = _oldFirmwareIdentity.DeviceTypeCode;
            }
            if (_oldFirmwareIdentity.Version.HasValue)
            {
                OldVersion = _oldFirmwareIdentity.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (_newFirmwareIdentity.Version.HasValue)
            {
                NewVersion = _newFirmwareIdentity.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (_oldFirmwareIdentity.Version.HasValue && _newFirmwareIdentity.Version.HasValue)
            {
                ForwardPatchName = _oldFirmwareIdentity.SuggestedPatchNameTo(_newFirmwareIdentity);
                ReversePatchName = _newFirmwareIdentity.SuggestedPatchNameTo(_oldFirmwareIdentity);
            }
            TaskStatusMessage = $"已识别 A/B 镜像：{FormatFirmwareIdentity(_oldFirmwareIdentity)} → {FormatFirmwareIdentity(_newFirmwareIdentity)}。";
        }
        catch (Exception exception)
        {
            PatchStatus = $"镜像身份校验失败：{exception.Message}";
            TaskStatusMessage = PatchStatus;
        }
    }

    private async Task GeneratePatchAsync()
    {
        if (string.IsNullOrWhiteSpace(_oldImagePath) || string.IsNullOrWhiteSpace(_newImagePath))
        {
            PatchStatus = "制作失败：请先导入 A 版本和 B 版本固件。";
            return;
        }

        try
        {
            _oldFirmwareIdentity ??= await FirmwareIdentityReader.ReadAsync(_oldImagePath);
            _newFirmwareIdentity ??= await FirmwareIdentityReader.ReadAsync(_newImagePath);
            _oldFirmwareIdentity.EnsureCompatibleWith(_newFirmwareIdentity);
            ApplyFirmwareIdentityDefaults();
            if (!byte.TryParse(OldVersion, out var oldVersion) ||
                !byte.TryParse(NewVersion, out var newVersion) ||
                oldVersion is < 1 or > 254 || newVersion is < 1 or > 254 ||
                oldVersion == newVersion)
            {
                throw new InvalidOperationException("旧 ECO 镜像兼容模式下必须手动填写 1～254 的版本号后再制作 Patch。");
            }
            ForwardPatchName = $"{_oldFirmwareIdentity.PatchPrefix}-v{oldVersion}-to-v{newVersion}.patch";
            ReversePatchName = $"{_oldFirmwareIdentity.PatchPrefix}-v{newVersion}-to-v{oldVersion}.patch";
        }
        catch (Exception exception)
        {
            PatchStatus = $"制作失败：{exception.Message}";
            TaskStatusMessage = PatchStatus;
            return;
        }

        if (await FirmwareImageHash.AreIdenticalAsync(
                _oldImagePath,
                _newImagePath))
        {
            PatchStatus = "制作失败：A/B 镜像内容相同，请重新导入不同版本镜像。";
            TaskStatusMessage = PatchStatus;
            return;
        }

        var root = GetPatchOutputDirectory();
        Directory.CreateDirectory(root);
        var tempRoot = Path.Combine(root, $".ota-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
        var forwardOutputPath = Path.Combine(tempRoot, NormalizePatchFileName(ForwardPatchName));
        var reverseOutputPath = Path.Combine(tempRoot, NormalizePatchFileName(ReversePatchName));
        if (string.Equals(forwardOutputPath, reverseOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            PatchStatus = "制作失败：正向和反向 Patch 名称不能相同。";
            TaskStatusMessage = PatchStatus;
            return;
        }
        var engine = new NativeBsdiffEngine();
        var otaDeviceType = _oldFirmwareIdentity!.OtaDeviceType;
        var forwardRequest = new DiffRequest(_oldImagePath, _newImagePath, forwardOutputPath, otaDeviceType, OldVersion, NewVersion);
        var reverseRequest = new DiffRequest(_newImagePath, _oldImagePath, reverseOutputPath, otaDeviceType, NewVersion, OldVersion);

        PatchStatus = "正在制作正向和反向 Patch…";
        var forward = await engine.GenerateAsync(forwardRequest);
        if (!forward.IsSuccess)
        {
            PatchStatus = forward.Message;
            TaskStatusMessage = forward.Message;
            return;
        }

        var reverse = await engine.GenerateAsync(reverseRequest);
        if (!reverse.IsSuccess)
        {
            PatchStatus = $"正向 Patch 已生成；反向 Patch 失败：{reverse.Message}";
            TaskStatusMessage = PatchStatus;
            return;
        }

        _patchPath = forwardOutputPath;
        _reversePatchPath = reverseOutputPath;
        var forwardMetadata = forward.Patch ?? await PatchMetadata.FromFileAsync(_patchPath);
        var reverseMetadata = reverse.Patch ?? await PatchMetadata.FromFileAsync(_reversePatchPath);
        var limits = GetPatchCapacityLimits();
        var forwardCapacity = PatchCapacityPolicy.Check(otaDeviceType, forwardMetadata.Length, limits);
        var reverseCapacity = PatchCapacityPolicy.Check(otaDeviceType, reverseMetadata.Length, limits);
        if (!forwardCapacity.IsAllowed || !reverseCapacity.IsAllowed)
        {
            PatchStatus = !forwardCapacity.IsAllowed ? forwardCapacity.Message : reverseCapacity.Message;
            TaskStatusMessage = PatchStatus;
            return;
        }

        PatchStatus = "Patch 已生成，正在自动执行正向和反向还原校验…";
        var role = _oldFirmwareIdentity.PatchPrefix switch
        {
            "ext-a" => "async",
            "ext-s" => "sync",
            "gateway" => "gateway",
            _ => "node",
        };
        await RunOtaToolPatchTestAsync("正向 Patch", _oldImagePath, _newImagePath, _patchPath, role, forwardCapacity.Limit);
        await RunOtaToolPatchTestAsync("反向 Patch", _newImagePath, _oldImagePath, _reversePatchPath, role, reverseCapacity.Limit);

        var forwardManifest = await PackageManifestFactory.CreateAsync(engine.GetInfo(), forwardRequest, forwardMetadata, true);
        var reverseManifest = await PackageManifestFactory.CreateAsync(engine.GetInfo(), reverseRequest, reverseMetadata, true);
        await PackageManifestExporter.ExportAsync(forwardManifest, _patchPath + ".json");
        await PackageManifestExporter.ExportAsync(reverseManifest, _reversePatchPath + ".json");
        var finalForwardPath = Path.Combine(root, Path.GetFileName(_patchPath));
        var finalReversePath = Path.Combine(root, Path.GetFileName(_reversePatchPath));
        File.Move(_patchPath, finalForwardPath, true);
        File.Move(_patchPath + ".json", finalForwardPath + ".json", true);
        File.Move(_reversePatchPath, finalReversePath, true);
        File.Move(_reversePatchPath + ".json", finalReversePath + ".json", true);
        _patchPath = finalForwardPath;
        _reversePatchPath = finalReversePath;
        _patchLength = forwardMetadata.Length;
        _patchMd5 = forwardMetadata.Md5;
        _patchSha256 = forwardMetadata.Sha256;
        _reversePatchMd5 = reverseMetadata.Md5;
        _reversePatchSha256 = reverseMetadata.Sha256;
        _reversePatchLength = reverseMetadata.Length;
        PatchUrl = IsHttpServiceRunning ? GetLocalPatchUrl(_patchPath) : string.Empty;
        _reversePatchUrl = IsHttpServiceRunning ? GetLocalPatchUrl(_reversePatchPath) : string.Empty;
        _patchManifestVerified = true;
        PatchStatus = $"已制作并通过双向还原校验：{Path.GetFileName(_patchPath)}、{Path.GetFileName(_reversePatchPath)}。";
        TaskStatusMessage = IsHttpServiceRunning
            ? "正向和反向 Patch 已生成并放入本地 HTTP 服务目录。"
            : "正向和反向 Patch 已生成；启动本地 HTTP Range 服务后即可提供下载。";
        OnPropertyChanged(nameof(PatchFileName));
        OnPropertyChanged(nameof(PatchDetail));
        OnPropertyChanged(nameof(PatchMetadataDetail));
        OnPropertyChanged(nameof(ReversePatchFileName));
        OnPropertyChanged(nameof(ReversePatchStatus));
        OnPropertyChanged(nameof(ReversePatchMetadataDetail));
        RegisterPatch(
            "正向 Patch",
            _patchPath,
            _patchLength,
            _patchMd5,
            _patchSha256,
            manifestVerified: true,
            otaDeviceType: otaDeviceType,
            oldVersion: forwardManifest.OldVersion,
            newVersion: forwardManifest.NewVersion);
        RegisterPatch(
            "反向 Patch",
            _reversePatchPath,
            _reversePatchLength,
            _reversePatchMd5,
            _reversePatchSha256,
            manifestVerified: true,
            otaDeviceType: otaDeviceType,
            oldVersion: reverseManifest.OldVersion,
            newVersion: reverseManifest.NewVersion);
        RefreshUpgradePatchChoices();
        SelectedReverseUpgradePatch = UpgradePatchChoices.FirstOrDefault(item => string.Equals(item.FilePath, _reversePatchPath, StringComparison.OrdinalIgnoreCase));
        await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            PatchStatus = $"制作失败：{exception.Message}";
            TaskStatusMessage = PatchStatus;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
            catch
            {
                // 临时目录清理失败不覆盖原始制作结果。
            }
        }
    }

    private async Task TestPatchRestoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_oldImagePath) || string.IsNullOrWhiteSpace(_newImagePath))
        {
            PatchRestoreTestStatus = "导入 Patch 验证失败：请先导入 A、B 版本固件。";
            return;
        }

        var patch = SelectedRestorePatch;
        if (patch is null || !File.Exists(patch.FilePath))
        {
            PatchRestoreTestStatus = "导入 Patch 验证失败：请选择尚未验证的外部 Patch。";
            return;
        }

        var isReverse = string.Equals(SelectedPatchRestoreDirection, "B → A", StringComparison.Ordinal);
        var testName = $"{patch.FileName}（{SelectedPatchRestoreDirection}）";
        var sourceImage = isReverse ? _newImagePath : _oldImagePath;
        var expectedImage = isReverse ? _oldImagePath : _newImagePath;
        PatchRestoreTestStatus = $"正在验证导入 Patch：{testName}…";
        try
        {
            var identity = await FirmwareIdentityReader.ReadAsync(sourceImage);
            var role = identity.PatchPrefix switch
            {
                "ext-a" => "async",
                "ext-s" => "sync",
                "gateway" => "gateway",
                _ => "node",
            };
            var limit = GetPatchCapacityLimits().For(identity.OtaDeviceType);
            await RunOtaToolPatchTestAsync(testName, sourceImage, expectedImage, patch.FilePath, role, limit);
            var metadata = await PatchMetadata.FromFileAsync(patch.FilePath);
            var request = new DiffRequest(
                sourceImage,
                expectedImage,
                patch.FilePath,
                identity.OtaDeviceType,
                isReverse ? NewVersion : OldVersion,
                isReverse ? OldVersion : NewVersion);
            var manifest = await PackageManifestFactory.CreateAsync(new NativeBsdiffEngine().GetInfo(), request, metadata, true);
            await PackageManifestExporter.ExportAsync(manifest, patch.FilePath + ".json");
            RegisterPatch(
                patch.Source,
                patch.FilePath,
                metadata.Length,
                metadata.Md5,
                metadata.Sha256,
                manifestVerified: true,
                otaDeviceType: identity.OtaDeviceType,
                oldVersion: manifest.OldVersion,
                newVersion: manifest.NewVersion);
            await ApplySidecarManifestAsync(patch.FilePath);
            RefreshUpgradePatchChoices();
            PatchRestoreTestStatus = $"{testName} 导入验证通过。";
            TaskStatusMessage = PatchRestoreTestStatus;
        }
        catch (Exception exception)
        {
            PatchRestoreTestStatus = $"导入 Patch 验证失败：{CompactError(exception.Message)}";
            TaskStatusMessage = PatchRestoreTestStatus;
        }
    }

    private static async Task RunOtaToolPatchTestAsync(
        string name,
        string oldImagePath,
        string newImagePath,
        string patchPath,
        string role,
        long patchLimitBytes)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "TestPatchWithOtaTool.ps1");
        var otaToolPath = Path.Combine(AppContext.BaseDirectory, "Tools", "OTA_TOOL", "OTA_TOOL.exe");
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("缺少 PatchTest 脚本，请重新安装桌面工具。", scriptPath);
        if (!File.Exists(otaToolPath)) throw new FileNotFoundException("发布包缺少内置 Patch 还原工具，请重新安装 OTA 测试平台。", otaToolPath);

        var testRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtaTool", "patch-restore-tests", Guid.NewGuid().ToString("N"));
        var firmwareDirectory = Path.Combine(testRoot, "firmware");
        var workDirectory = Path.Combine(testRoot, "work");
        var packageDirectory = Path.Combine(testRoot, "package");
        Directory.CreateDirectory(firmwareDirectory);
        File.Copy(oldImagePath, Path.Combine(firmwareDirectory, $"{role}-old.bin"));
        File.Copy(newImagePath, Path.Combine(firmwareDirectory, $"{role}-new.bin"));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-Role");
        process.StartInfo.ArgumentList.Add(role);
        process.StartInfo.ArgumentList.Add("-FirmwareDirectory");
        process.StartInfo.ArgumentList.Add(firmwareDirectory);
        process.StartInfo.ArgumentList.Add("-PackageDirectory");
        process.StartInfo.ArgumentList.Add(packageDirectory);
        process.StartInfo.ArgumentList.Add("-WorkDirectory");
        process.StartInfo.ArgumentList.Add(workDirectory);
        process.StartInfo.ArgumentList.Add("-OtaToolPath");
        process.StartInfo.ArgumentList.Add(otaToolPath);
        process.StartInfo.ArgumentList.Add("-PatchToTest");
        process.StartInfo.ArgumentList.Add(patchPath);
        process.StartInfo.ArgumentList.Add("-SkippedBootloaderBytes");
        process.StartInfo.ArgumentList.Add(FirmwareIdentityReader.BootloaderLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (patchLimitBytes < long.MaxValue)
        {
            process.StartInfo.ArgumentList.Add("-PatchLimitBytes");
            process.StartInfo.ArgumentList.Add(patchLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        process.StartInfo.ArgumentList.Add("-TimeoutSeconds");
        process.StartInfo.ArgumentList.Add("90");

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var detail = (await error).Trim();
            if (string.IsNullOrWhiteSpace(detail)) detail = (await output).Trim();
            throw new InvalidOperationException($"{name}：{detail}");
        }
    }

    private static string CompactError(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "未返回具体原因。";
        var normalized = detail.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        var mismatchIndex = normalized.IndexOf("PatchTest output mismatch:", StringComparison.OrdinalIgnoreCase);
        if (mismatchIndex >= 0)
        {
            var mismatch = normalized[mismatchIndex..].Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return mismatch.Length <= 280 ? mismatch : mismatch[..280] + "…";
        }

        var firstLine = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !line.TrimStart().StartsWith("at ", StringComparison.OrdinalIgnoreCase))?.Trim() ?? normalized;
        return firstLine.Length <= 280 ? firstLine : firstLine[..280] + "…";
    }

    private async void SelectReversePatch(object? _)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择反向 OTA Patch",
            Filter = "Patch 文件|*.patch;*.bin;*.ecop;*.dat|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var root = GetPatchOutputDirectory();
            Directory.CreateDirectory(root);
            var fileName = $"reverse-{Path.GetFileName(dialog.FileName)}";
            _reversePatchPath = Path.Combine(root, fileName);
            File.Copy(dialog.FileName, _reversePatchPath, overwrite: true);
            var metadata = await PatchMetadata.FromFileAsync(_reversePatchPath);
            _reversePatchMd5 = metadata.Md5;
            _reversePatchSha256 = metadata.Sha256;
            _reversePatchLength = metadata.Length;
            _reversePatchUrl = GetLocalPatchUrl(_reversePatchPath);
            TaskStatusMessage = "反向 Patch 已导入本地 HTTP 根目录。";
            OnPropertyChanged(nameof(ReversePatchFileName));
            OnPropertyChanged(nameof(ReversePatchStatus));
            OnPropertyChanged(nameof(ReversePatchMetadataDetail));
            RegisterPatch("反向 Patch", _reversePatchPath, _reversePatchLength, _reversePatchMd5, _reversePatchSha256);
            RefreshUpgradePatchChoices();
            SelectedReverseUpgradePatch = UpgradePatchChoices.FirstOrDefault(item => string.Equals(item.FilePath, _reversePatchPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"反向 Patch 导入失败：{exception.Message}";
        }
    }

    private async Task StartHttpServiceAsync()
    {
        try
        {
            if (!_httpRangeServer.IsRunning)
            {
                var root = GetPatchOutputDirectory();
                Directory.CreateDirectory(root);
                await _httpRangeServer.StartAsync(new HttpRangeServerOptions(root, HttpPort));
            }

            HttpServiceStatus = "运行中";
            if (!string.IsNullOrWhiteSpace(_patchPath))
            {
                PatchUrl = GetLocalPatchUrl(_patchPath);
            }
            if (!string.IsNullOrWhiteSpace(_reversePatchPath))
            {
                _reversePatchUrl = GetLocalPatchUrl(_reversePatchPath);
                OnPropertyChanged(nameof(ReversePatchStatus));
            }
            TaskStatusMessage = $"本地 HTTP Range 服务已启动：{HttpServiceAddress}";
            OnPropertyChanged(nameof(HttpServiceAddress));
            OnPropertyChanged(nameof(IsHttpServiceRunning));
            OnPropertyChanged(nameof(HttpServiceToggleText));
            OnPropertyChanged(nameof(SelectedHttpPatchUrl));
        }
        catch (Exception exception)
        {
            HttpServiceStatus = $"启动失败：{exception.Message}";
            TaskStatusMessage = "本地 HTTP Range 服务启动失败。";
        }
    }

    private async Task StopHttpServiceAsync()
    {
        await _httpRangeServer.StopAsync();
        HttpServiceStatus = "未启动";
        TaskStatusMessage = "本地 HTTP Range 服务已停止。";
        OnPropertyChanged(nameof(HttpServiceAddress));
        OnPropertyChanged(nameof(IsHttpServiceRunning));
        OnPropertyChanged(nameof(HttpServiceToggleText));
        OnPropertyChanged(nameof(SelectedHttpPatchUrl));
    }

    private async Task ToggleHttpServiceAsync()
    {
        if (IsHttpServiceRunning) await StopHttpServiceAsync();
        else
        {
            HttpUsesLocalServer = true;
            await StartHttpServiceAsync();
        }
    }

    private async Task ApplyPublicHttpServerAsync()
    {
        if (IsHttpServiceRunning && HttpUsesLocalServer)
        {
            PublicHttpServiceStatus = "设置被阻止";
            TaskStatusMessage = "公网 HTTP 服务未设置：请先停止本地 HTTP Range 服务。";
            ShowInformationDialog("无法设置公网 HTTP 服务", "本地 HTTP Range 服务仍在运行，请先停止本地服务，再设置公网 HTTP 服务。");
            return;
        }

        if (!Uri.TryCreate(PublicHttpBaseUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            ShowInformationDialog("公网 HTTP 地址无效", "请输入以 http:// 或 https:// 开头的公网 HTTP 基地址。");
            return;
        }

        PublicHttpBaseUrl = publicUri.ToString().TrimEnd('/') + "/";
        HttpUsesLocalServer = false;
        PublicHttpServiceStatus = "已设置";
        TaskStatusMessage = $"公网 HTTP 服务已设置：{PublicHttpBaseUrl}";
        await SaveSettingsAsync();
    }

    private async Task StartSingleTaskAsync(bool reverse)
    {
        var canStartDirection = reverse ? CanStartReverseUpgrade : CanStartForwardUpgrade;
        var directionText = reverse ? "反向" : "正向";
        if (!canStartDirection)
        {
            TaskStatusMessage = CanStartUpgrade
                ? $"{directionText}升级不可用：请确认对应 Patch、版本方向和所选目标当前版本。"
                : "已有升级任务正在确认或执行中，不能重复启动。";
            return;
        }

        var mode = IsEcoLink ? OtaMode.EcoLink : OtaMode.Traditional;
        IOtaProtocolProfile profile = mode == OtaMode.EcoLink ? new EcoLinkProtocolProfile() : new TraditionalProtocolProfile();
        var deviceType = SelectedTaskType switch
        {
            GatewayTaskType => DeviceType.Gateway,
            SyncTaskType => DeviceType.Sync,
            AsyncTaskType => DeviceType.Async,
            NodeTaskType => DeviceType.Node,
            _ => throw new InvalidOperationException("未知升级类型。"),
        };
        var deviceIds = TargetIdList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IReadOnlyList<OtaExtenderTarget> extenderTargets;
        try
        {
            extenderTargets = deviceType == DeviceType.Node ? ParseNodeTargets(NodeTargetsText) : [];
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"任务未启动：{exception.Message}";
            return;
        }
        if (deviceType == DeviceType.Node && ValidateDiscoveredNodeTypes(extenderTargets, NodeType) is { } nodeTypeError)
        {
            TaskStatusMessage = $"任务未启动：{nodeTypeError}";
            return;
        }
        if (!uint.TryParse(GatewayId, out var numericGatewayId) || numericGatewayId == 0)
        {
            TaskStatusMessage = "任务未启动：Gateway ID 必须填写十进制正整数。";
            return;
        }
        GatewayId = numericGatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var selectedPatch = reverse ? SelectedReverseUpgradePatch : SelectedUpgradePatch;
        if (selectedPatch is null || !File.Exists(selectedPatch.FilePath))
        {
            TaskStatusMessage = $"任务未启动：请先选择一个可用的{directionText}升级 Patch。";
            return;
        }
        PatchMetadata selectedPatchMetadata;
        PackageManifest? selectedManifest = null;
        try
        {
            selectedPatchMetadata = await PatchMetadata.FromFileAsync(selectedPatch.FilePath);
            if (IsEcoLink && !selectedPatch.IsFullImage)
            {
                selectedManifest = await PackageManifestImporter.LoadAndValidateAsync(selectedPatch.FilePath);
            }
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"任务未启动：无法读取所选 Patch：{exception.Message}";
            return;
        }
        if (selectedPatch.IsFullImage && deviceType != DeviceType.Gateway)
        {
            TaskStatusMessage = "任务未启动：完整 .bin 镜像仅支持网关升级。";
            return;
        }
        if (IsEcoLink && !selectedPatch.IsFullImage && selectedManifest is not null &&
            ValidateUpgradePreflight(
                deviceType,
                deviceIds,
                extenderTargets,
                selectedPatchMetadata,
                selectedManifest,
                reverse ? NewVersion : OldVersion,
                reverse ? OldVersion : NewVersion) is { } preflightError)
        {
            TaskStatusMessage = $"任务未启动：{preflightError}";
            return;
        }
        var selectedPatchUrl = GetPatchDownloadUrl(selectedPatch.FilePath);
        var task = new OtaTask
        {
            Mode = mode,
            DeviceType = deviceType,
            GatewayId = GatewayId,
            Target = BuildTaskTarget(deviceType, deviceIds, extenderTargets),
            NodeType = deviceType == DeviceType.Node ? NodeType : null,
            ExtenderTargets = extenderTargets,
            OldVersion = reverse ? NewVersion : OldVersion,
            NewVersion = reverse ? OldVersion : NewVersion,
            PatchPath = selectedPatch.FilePath,
            PatchUrl = selectedPatchUrl,
            PatchMd5 = selectedPatchMetadata.Md5,
            PatchSha256 = selectedPatchMetadata.Sha256,
            ProtocolProfileId = mode == OtaMode.EcoLink ? "ecolink-gateway" : "traditional",
            ProtocolProfileVersion = "1.0",
        };
        var validation = OtaTaskValidator.Validate(task, profile);
        if (!validation.IsValid)
        {
            TaskStatusMessage = $"任务未启动：{validation.Message}";
            return;
        }
        if (!_mqtt.IsConnected)
        {
            TaskStatusMessage = "任务未启动：MQTT 尚未连接。";
            return;
        }
        if (IsEcoLink && !IsGatewayTopicSubscribed)
        {
            TaskStatusMessage = "任务未启动：请先订阅当前 Gateway dev ID 对应的主题。";
            return;
        }
        if (string.IsNullOrWhiteSpace(selectedPatchUrl))
        {
            TaskStatusMessage = "任务未启动：本地 HTTP Range 服务未运行，且未配置可用的公网 HTTP 地址。";
            return;
        }
        if (IsEcoLink && !selectedPatch.IsFullImage && !selectedPatch.ManifestVerified)
        {
            TaskStatusMessage = "任务未启动：Package Manifest 标记 PatchTest 未通过。";
            return;
        }
        if (_runner is not null && _runner.HasActiveTask)
        {
            TaskStatusMessage = "任务未启动：当前已有活动 OTA 任务。";
            return;
        }
        if (ValidateGatewayVersionBeforeUpgrade(task) is { } gatewayVersionError)
        {
            TaskStatusMessage = $"任务未启动：{gatewayVersionError}";
            return;
        }
        _pendingUpgradeTask = task;
        _pendingUpgradeProfile = profile;
        NotifyUpgradeActionAvailability();
        OpenPatchDialog(
            PatchDialogAction.StartUpgrade,
            $"确认启动{directionText}升级",
            BuildUpgradeConfirmationMessage(task, selectedPatch, directionText),
            "确认启动");
    }

    private async Task VerifyAndStartValidatedTaskAsync(
        OtaTask task,
        IOtaProtocolProfile profile)
    {
        TaskStatusMessage = "正在校验 Patch HTTP 完整性，校验通过后自动发送升级请求…";
        try
        {
            if (!Uri.TryCreate(task.PatchUrl, UriKind.Absolute, out var patchUri))
            {
                TaskStatusMessage = "任务未启动：未配置可用的 HTTP 文件地址。";
                return;
            }

            var metadata = await PatchMetadata.FromFileAsync(task.PatchPath);
            if (!string.Equals(metadata.Md5, task.PatchMd5, StringComparison.OrdinalIgnoreCase))
            {
                PatchStatus = "HTTP 校验失败：确认期间本地 Patch 内容已发生变化。";
                TaskStatusMessage = "任务未启动：请重新选择 Patch 后再试。";
                return;
            }

            var verification = await HttpFileVerifier.VerifyAsync(
                patchUri,
                metadata.Length,
                metadata.Md5,
                verifyFullMd5: true);
            if (!verification.IsSuccess)
            {
                PatchStatus = $"HTTP 校验失败：{verification.Message}";
                TaskStatusMessage = "任务未启动：Patch HTTP 完整性校验失败。";
                return;
            }
            PatchStatus = verification.Message;
            await SaveSettingsAsync();
            await StartValidatedTaskAsync(task, profile);
        }
        catch (Exception exception)
        {
            PatchStatus = $"HTTP 校验失败：{exception.Message}";
            TaskStatusMessage = "任务未启动：无法确认 Patch 可被 HTTP Range 服务正确获取。";
        }
        finally
        {
            _isUpgradeStartInProgress = false;
            NotifyUpgradeActionAvailability();
        }
    }

    private async Task StartValidatedTaskAsync(OtaTask task, IOtaProtocolProfile profile)
    {
        if (_runner?.HasActiveTask == true)
        {
            TaskStatusMessage = "已有活动 OTA 任务，不能重复启动。";
            _isUpgradeStartInProgress = false;
            NotifyUpgradeActionAvailability();
            return;
        }

        _isUpgradeStartInProgress = true;
        NotifyUpgradeActionAvailability();
        if (_runner is not null) await _runner.DisposeAsync();
        _runner = new OtaTaskRunner(_mqtt, profile, _reportStore);
        _runner.Updated += OnTaskUpdated;
        _runner.MessagePublished += OnMqttMessagePublished;
        _gatewayStatusDeviceType = task.DeviceType;
        GatewayStages.Clear();
        GatewaySubtasks.Clear();
        GatewayPackageSourceSummary = string.Empty;
        _gatewayTaskSequence = null;
        _gatewayTaskStartedAt = null;
        GatewayStageSummary = task.Mode == OtaMode.EcoLink
            ? "等待 Gateway 阶段状态…"
            : "等待 Gateway 最终升级结果上报…";
        UpgradeRunModeText = $"单次 {task.OldVersion} to {task.NewVersion}";
        UpgradeRunModeForeground = "#2570E8";
        UpgradeRunModeBackground = "#E9F0FF";
        UpgradeRunProgressText = $"{SelectedTaskType} · 正在发送升级请求";
        _activeReport = new OtaReport { Task = task, LogAnalysisConclusion = task.Mode == OtaMode.Traditional ? "日志解析不支持" : null };
        _reportTaskIds.Clear();
        _reportTaskIds.Add(task.Id);
        try
        {
            var result = await _runner.StartAsync(task);
            TaskStatusMessage = result.State == OtaTaskState.Running
                ? $"{SelectedTaskType} 已发送升级请求。"
                : $"任务未启动：{result.Message}";
            UpgradeRunProgressText = result.State == OtaTaskState.Running
                ? $"{SelectedTaskType} · 升级请求已发送"
                : $"启动失败 · {result.Message}";
            if (result.State != OtaTaskState.Running)
            {
                _activeReport = null;
                _reportTaskIds.Clear();
            }
        }
        finally
        {
            _isUpgradeStartInProgress = false;
            NotifyUpgradeActionAvailability();
            OnPropertyChanged(nameof(CanControlPolling));
        }
    }

    private string BuildUpgradeConfirmationMessage(OtaTask task, PatchSelection patch, string directionText)
    {
        var target = task.DeviceType switch
        {
            DeviceType.Gateway => "网关升级（无需指定目标 ID）",
            DeviceType.Node => $"Node 类型：{NodeTypeCatalog.Format(task.NodeType)}；目标：{string.Join("；", task.ExtenderTargets.Select(item => $"{item.ExtenderId}: {string.Join(',', item.NodeIds)}"))}",
            _ when task.Target.Scope == TargetScope.Broadcast => "目标范围：广播",
            _ => $"目标 ID：{string.Join("、", task.Target.DeviceIds)}",
        };

        var warnings = new List<string>();
        if (task.DeviceType == DeviceType.Node)
        {
            if (!_nodeDiscoveryCompletedAt.HasValue ||
                DateTimeOffset.Now - _nodeDiscoveryCompletedAt.Value > TimeSpan.FromMinutes(DiscoveryFreshnessMinutes))
            {
                warnings.Add($"Node 发现结果已超过 {DiscoveryFreshnessMinutes} 分钟");
            }
            var lowRssiCount = DiscoveredNodeGroups.SelectMany(group => group.Nodes)
                .Count(node => node.IsSelected && node.Rssi < MinimumNodeRssi);
            if (lowRssiCount > 0)
            {
                warnings.Add($"{lowRssiCount} 个 Node 的 RSSI 低于 {MinimumNodeRssi} dBm");
            }
        }
        var warningText = warnings.Count == 0
            ? "警告项：无"
            : $"警告项：{string.Join("；", warnings)}";

        return string.Join(
            Environment.NewLine,
            $"模式：{(task.Mode == OtaMode.EcoLink ? "EcoLink" : "传统")}",
            $"升级类型：{task.DeviceType} 升级",
            $"升级方向：{directionText}",
            $"Gateway dev ID：{task.GatewayId}",
            target,
            warningText,
            $"版本：{task.OldVersion} → {task.NewVersion}",
            $"Patch：{patch.FileName}",
            $"大小：{patch.Length:N0} B",
            $"MD5：{task.PatchMd5}",
            $"下载地址：{task.PatchUrl}",
            string.Empty,
            "确认后将向设备发送 OTA 升级请求。");
    }

    private static string BuildCycleUpgradeConfirmationMessage(
        OtaTask forward,
        OtaTask reverse,
        OtaCycleIntervalOptions interval,
        int rounds)
    {
        var type = forward.DeviceType switch
        {
            DeviceType.Gateway => "网关升级",
            DeviceType.Sync => "拓展器-同步升级",
            DeviceType.Async => "拓展器-异步升级",
            DeviceType.Node => "节点升级",
            _ => forward.DeviceType.ToString(),
        };
        var target = forward.DeviceType switch
        {
            DeviceType.Gateway => "网关升级（无需指定目标 ID）",
            DeviceType.Node => $"Node 类型：{NodeTypeCatalog.Format(forward.NodeType)}；目标：{string.Join("；", forward.ExtenderTargets.Select(item => $"{item.ExtenderId}: {string.Join(',', item.NodeIds)}"))}",
            _ when forward.Target.Scope == TargetScope.Broadcast => "目标范围：广播",
            _ => $"目标 ID：{string.Join("、", forward.Target.DeviceIds)}",
        };
        var intervalText = interval.Mode == OtaCycleIntervalMode.Random
            ? $"随机 {interval.RandomMinimumSeconds}～{interval.RandomMaximumSeconds} 秒"
            : interval.FixedSeconds == 0
                ? "固定 0 秒（连续执行）"
                : $"固定 {interval.FixedSeconds} 秒";

        return string.Join(
            Environment.NewLine,
            $"模式：{(forward.Mode == OtaMode.EcoLink ? "EcoLink" : "传统")}",
            $"升级类型：{type}",
            $"Gateway dev ID：{forward.GatewayId}",
            target,
            $"循环轮数：{rounds} 轮（共 {rounds * 2} 次单次升级）",
            $"循环顺序：{forward.OldVersion} → {forward.NewVersion} → {reverse.NewVersion}",
            $"单次间隔：{intervalText}",
            $"正向 Patch：{Path.GetFileName(forward.PatchPath)}",
            $"反向 Patch：{Path.GetFileName(reverse.PatchPath)}",
            $"正向下载地址：{forward.PatchUrl}",
            $"反向下载地址：{reverse.PatchUrl}",
            string.Empty,
            "确认后将先校验正、反向 Patch，再按上述参数启动循环升级。");
    }

    private OtaTaskTarget BuildTaskTarget(
        DeviceType deviceType,
        IReadOnlyList<string> deviceIds,
        IReadOnlyList<OtaExtenderTarget> extenderTargets)
    {
        if (deviceType == DeviceType.Gateway)
        {
            return OtaTaskTarget.Broadcast();
        }

        if (deviceType == DeviceType.Node)
        {
            return OtaTaskTarget.Specified(extenderTargets.SelectMany(target => target.NodeIds).ToArray());
        }

        return IsSpecifiedTarget || (IsEcoLink && deviceType == DeviceType.Async)
            ? OtaTaskTarget.Specified(deviceIds.ToArray())
            : OtaTaskTarget.Broadcast();
    }

    private async Task ReplaceGatewaySubscriptionAsync(string newTopic)
    {
        if (string.Equals(_subscribedGatewayTopic, newTopic, StringComparison.Ordinal))
        {
            return;
        }

        var previousTopic = _subscribedGatewayTopic;
        if (!string.IsNullOrWhiteSpace(previousTopic))
        {
            await _mqtt.UnsubscribeAsync(previousTopic);
            _subscribedGatewayTopic = string.Empty;
            NotifyGatewaySubscriptionChanged();
        }

        await _mqtt.SubscribeAsync(newTopic, qualityOfService: 1);
        _subscribedGatewayTopic = newTopic;
        MqttMessages.Clear();
        _observedGatewayIds.Clear();
        NotifyGatewaySubscriptionChanged();
        OnPropertyChanged(nameof(GatewayOnlineStatus));
    }

    private void NotifyGatewaySubscriptionChanged()
    {
        OnPropertyChanged(nameof(IsGatewayTopicSubscribed));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeText));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeBackground));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeForeground));
    }

    private void RememberGatewayId(string gatewayId)
    {
        if (!uint.TryParse(gatewayId, out var numericGatewayId) || numericGatewayId == 0)
        {
            return;
        }

        var normalized = numericGatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var existing = GatewayIdHistory.FirstOrDefault(item =>
            string.Equals(item, normalized, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }
        GatewayIdHistory.Insert(0, normalized);
        while (GatewayIdHistory.Count > MaxGatewayIdHistory)
        {
            GatewayIdHistory.RemoveAt(GatewayIdHistory.Count - 1);
        }
    }

    private void RestoreGatewayIdHistory(IReadOnlyList<string>? history, string currentGatewayId)
    {
        GatewayIdHistory.Clear();
        foreach (var value in (history ?? []).Reverse())
        {
            RememberGatewayId(value);
        }
        RememberGatewayId(currentGatewayId);
    }

    private async Task SubscribeGatewayTopicAsync()
    {
        if (!_mqtt.IsConnected)
        {
            GatewaySubscriptionStatus = "MQTT 未连接，暂不能订阅 Gateway 主题。";
            TaskStatusMessage = GatewaySubscriptionStatus;
            return;
        }

        if (!uint.TryParse(GatewayId, out var gatewayId) || gatewayId == 0)
        {
            GatewaySubscriptionStatus = "请填写十进制正整数 Gateway ID 后再订阅主题。";
            TaskStatusMessage = GatewaySubscriptionStatus;
            return;
        }

        GatewayId = gatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsGatewayTopicSubscribed)
        {
            RememberGatewayId(GatewayId);
            await SaveSettingsAsync();
            GatewaySubscriptionStatus = $"已订阅：{GatewaySubscriptionTopic}，无需重复订阅。";
            TaskStatusMessage = GatewaySubscriptionStatus;
            return;
        }

        try
        {
            await ReplaceGatewaySubscriptionAsync(GatewaySubscriptionTopic);
            RememberGatewayId(GatewayId);
            await SaveSettingsAsync();
            GatewaySubscriptionStatus = $"已订阅：{GatewaySubscriptionTopic}";
            TaskStatusMessage = GatewaySubscriptionStatus;
        }
        catch (Exception exception)
        {
            GatewaySubscriptionStatus = $"主题订阅失败：{exception.Message}";
            TaskStatusMessage = GatewaySubscriptionStatus;
        }
    }

    private async Task ConnectMqttAsync()
    {
        try
        {
            var hasGatewayId = !string.IsNullOrWhiteSpace(GatewayId);
            uint gatewayId = 0;
            if (hasGatewayId && (!uint.TryParse(GatewayId, out gatewayId) || gatewayId == 0))
            {
                throw new InvalidOperationException("Gateway dev ID 必须填写十进制正整数。");
            }
            var options = MqttClientUsesLocalBroker
                ? new MqttClientOptions("127.0.0.1", LocalBrokerPort, $"ota-tool-{Environment.MachineName}-{Environment.ProcessId}",
                    UseTls: false, UserName: LocalBrokerUserName, Password: LocalBrokerPassword)
                : new MqttClientOptions(MqttHost, MqttPort, $"ota-tool-{Environment.MachineName}-{Environment.ProcessId}",
                    MqttUseTls, MqttUserName, MqttPassword, MqttAcceptAnyServerCertificate);
            await _mqtt.ConnectAsync(options);
            if (hasGatewayId)
            {
                GatewayId = gatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ReplaceGatewaySubscriptionAsync(GatewaySubscriptionTopic);
                RememberGatewayId(GatewayId);
                GatewaySubscriptionStatus = $"已订阅：{GatewaySubscriptionTopic}";
            }
            else
            {
                GatewaySubscriptionStatus = "MQTT 已连接；填写 Gateway dev ID 后可单独订阅主题。";
            }
            MqttStatus = "已连接";
            TaskStatusMessage = hasGatewayId
                ? $"MQTT 客户端已连接：{MqttClientEndpoint}，已订阅 Gateway 主题。"
                : $"MQTT 客户端已连接：{MqttClientEndpoint}。尚未订阅 Gateway 主题。";
        }
        catch (Exception exception)
        {
            MqttStatus = $"连接失败：{exception.Message}";
            TaskStatusMessage = MqttStatus;
        }
    }

    private async Task DisconnectMqttAsync()
    {
        await _mqtt.DisconnectAsync();
        _subscribedGatewayTopic = string.Empty;
        OnPropertyChanged(nameof(IsGatewayTopicSubscribed));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeText));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeBackground));
        OnPropertyChanged(nameof(GatewaySubscriptionBadgeForeground));
        MqttStatus = "未连接";
        TaskStatusMessage = "MQTT 已断开。";
    }

    private async Task ToggleMqttConnectionAsync()
    {
        if (IsMqttConnected) await DisconnectMqttAsync();
        else await ConnectMqttAsync();
    }

    private async Task ToggleLocalMqttConnectionAsync()
    {
        if (IsMqttConnected)
        {
            if (!MqttClientUsesLocalBroker)
            {
                TaskStatusMessage = "当前已连接公网 Broker，请先点击“断开公网 Broker”再连接本地 Broker。";
                return;
            }

            await DisconnectMqttAsync();
            return;
        }

        if (!IsEmbeddedBrokerRunning)
        {
            MqttStatus = "连接失败：本地 Broker 未启动";
            TaskStatusMessage = "MQTT 连接失败：本地 Broker 未启动，请先启动本地 Broker。";
            return;
        }

        MqttClientUsesLocalBroker = true;
        await ConnectMqttAsync();
    }

    private async Task TogglePublicMqttConnectionAsync()
    {
        if (IsMqttConnected)
        {
            if (MqttClientUsesLocalBroker)
            {
                TaskStatusMessage = "当前已连接本地 Broker，请先点击“断开本地 Broker”再连接公网 Broker。";
                return;
            }

            await DisconnectMqttAsync();
            return;
        }

        MqttClientUsesLocalBroker = false;
        await ConnectMqttAsync();
    }

    private void SelectMqttConfiguration(object? parameter)
    {
        if (parameter is not string selection) return;
        MqttClientUsesLocalBroker = string.Equals(selection, "Local", StringComparison.Ordinal);
    }

    private async Task StartEmbeddedBrokerAsync()
    {
        try
        {
            await _embeddedBroker.StartAsync(new EmbeddedMqttBrokerOptions(LocalBrokerPort,
                string.IsNullOrWhiteSpace(LocalBrokerUserName) ? null : LocalBrokerUserName,
                string.IsNullOrWhiteSpace(LocalBrokerUserName) ? null : LocalBrokerPassword));
            EmbeddedBrokerStatus = $"已启动：{LocalBrokerPort}";
            OnPropertyChanged(nameof(IsEmbeddedBrokerRunning));
            OnPropertyChanged(nameof(EmbeddedBrokerToggleText));
            TaskStatusMessage = "本地 MQTT Broker 已启动，可点击连接建立工具客户端连接。";
        }
        catch (Exception exception)
        {
            MqttStatus = $"Broker 启动失败：{exception.Message}";
        }
    }

    private async Task StopEmbeddedBrokerAsync()
    {
        if (MqttClientUsesLocalBroker)
        {
            await _mqtt.DisconnectAsync();
            _subscribedGatewayTopic = string.Empty;
            OnPropertyChanged(nameof(IsGatewayTopicSubscribed));
            OnPropertyChanged(nameof(GatewaySubscriptionBadgeText));
            OnPropertyChanged(nameof(GatewaySubscriptionBadgeBackground));
            OnPropertyChanged(nameof(GatewaySubscriptionBadgeForeground));
            MqttStatus = "未连接";
        }
        await _embeddedBroker.StopAsync();
        EmbeddedBrokerStatus = "已停止";
        OnPropertyChanged(nameof(IsEmbeddedBrokerRunning));
        OnPropertyChanged(nameof(EmbeddedBrokerToggleText));
        TaskStatusMessage = "本地 MQTT Broker 已停止。";
    }

    private async Task ToggleEmbeddedBrokerAsync()
    {
        if (IsEmbeddedBrokerRunning) await StopEmbeddedBrokerAsync();
        else await StartEmbeddedBrokerAsync();
    }

    private async Task RefreshExtendersAsync()
    {
        if (!IsEcoLink || IsDiscoveringDevices)
        {
            return;
        }
        if (IsUpgradeInProgress)
        {
            DeviceDiscoveryStatus = "升级过程中不能刷新 Extender。";
            return;
        }
        if (!_mqtt.IsConnected)
        {
            ClearDiscoveredExtenderResults();
            if (SelectedTaskType == GatewayTaskType) ClearGatewayVersionQueryResult();
            DeviceDiscoveryStatus = "MQTT 尚未连接，无法刷新在线 Extender。";
            return;
        }
        if (!uint.TryParse(GatewayId, out var gatewayId) || gatewayId == 0)
        {
            ClearDiscoveredExtenderResults();
            if (SelectedTaskType == GatewayTaskType) ClearGatewayVersionQueryResult();
            DeviceDiscoveryStatus = "Gateway ID 必须是十进制正整数。";
            return;
        }
        if (!IsGatewayTopicSubscribed)
        {
            ClearDiscoveredExtenderResults();
            if (SelectedTaskType == GatewayTaskType) ClearGatewayVersionQueryResult();
            DeviceDiscoveryStatus = "请先订阅当前 Gateway dev ID 对应的主题。";
            return;
        }

        var isGatewayVersionQuery = SelectedTaskType == GatewayTaskType;
        IsRefreshingExtenders = true;
        DeviceDiscoveryStatus = isGatewayVersionQuery
            ? "正在查询 Gateway 基础信息…"
            : "正在查询 Gateway 在线鉴权列表…";
        try
        {
            if (isGatewayVersionQuery)
            {
                ClearDiscoveredExtenderResults();
                var info = await _deviceDiscovery.QueryGatewayBasicInfoAsync(gatewayId.ToString());
                _gatewaySoftwareVersion = info.SoftwareVersion;
                _gatewayVersionGatewayId = gatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DeviceDiscoveryStatus = $"Gateway 当前软件版本：{ProtocolVersionFormatter.FormatWithPrefix(info.SoftwareVersion)}。";
                OnPropertyChanged(nameof(GatewayIdTaskHint));
                NotifyUpgradeActionAvailability();
                await SaveSettingsAsync();
                return;
            }
            var selectedIds = DiscoveredExtenders.Where(item => item.IsSelected)
                .Select(item => item.ExtenderId)
                .ToHashSet();
            if (SelectedTaskType is SyncTaskType or AsyncTaskType)
            {
                foreach (var value in ParsePositiveUIntLines(TargetIdList)) selectedIds.Add(value);
            }
            else if (SelectedTaskType == NodeTaskType)
            {
                foreach (var target in TryParseNodeTargets(NodeTargetsText))
                {
                    if (uint.TryParse(target.ExtenderId, out var extenderId)) selectedIds.Add(extenderId);
                }
            }

            ClearDiscoveredExtenderResults();
            var extenders = await _deviceDiscovery.DiscoverExtendersAsync(gatewayId.ToString());
            var extenderStatuses = new Dictionary<uint, GatewayExtenderStatus>();
            var statusFailures = 0;
            if (SelectedTaskType == AsyncTaskType && extenders.Count > 0)
            {
                DeviceDiscoveryStatus =
                    $"已发现 {extenders.Count} 个在线 Extender，正在查询 Sync/Async 状态…";
                var statusResults = await _deviceDiscovery.DiscoverExtenderStatusesAsync(
                    gatewayId.ToString(),
                    extenders.Select(item => item.ExtenderId));
                extenderStatuses = statusResults
                    .Where(item => item.IsSuccess &&
                                   item.Status is not null &&
                                   ProtocolVersionFormatter.IsKnown(item.Status.AsyncSoftwareVersion))
                    .ToDictionary(item => item.ExtenderId, item => item.Status!);
                statusFailures = statusResults.Count - extenderStatuses.Count;
                if (extenderStatuses.Count == 0)
                {
                    ClearDiscoveredExtenderResults();
                    DeviceDiscoveryStatus =
                        "刷新 Extender 失败：所有 0x11 状态查询均超时；Async 固件可能尚未支持 cmd=100/0x10→0x11，已清空 Async 可升级目标。";
                    await SaveSettingsAsync();
                    return;
                }
            }
            _suppressSelectionSync = true;
            foreach (var extender in extenders)
            {
                extenderStatuses.TryGetValue(extender.ExtenderId, out var status);
                if (SelectedTaskType == AsyncTaskType &&
                    (status is null || !ProtocolVersionFormatter.IsKnown(status.AsyncSoftwareVersion)))
                {
                    continue;
                }
                DiscoveredExtenders.Add(new SelectableExtenderItem(
                    extender.ExtenderId,
                    extender.Detail,
                    extender.DeviceType,
                    extender.SoftwareVersion,
                    status?.AsyncSoftwareVersion,
                    status?.AsyncAddress,
                    status?.SyncRssi,
                    status?.SyncSnr,
                    status?.OnlineCount,
                    status?.TotalCount,
                    selectedIds.Contains(extender.ExtenderId),
                    OnExtenderSelectionChanged));
            }
            if (SelectedTaskType == NodeTaskType &&
                DiscoveredExtenders.Count > 0 &&
                DiscoveredExtenders.All(item => !item.IsSelected))
            {
                foreach (var item in DiscoveredExtenders.Take(16)) item.IsSelected = true;
            }
            _suppressSelectionSync = false;
            OnExtenderSelectionChanged();
            DeviceDiscoveryStatus = SelectedTaskType == AsyncTaskType
                ? statusFailures == 0
                    ? $"已发现 {extenders.Count} 个在线 Extender，Sync/Async 状态查询完成。"
                    : $"已发现 {extenders.Count} 个在线 Extender；{statusFailures} 个状态查询失败，已保留 {DiscoveredExtenders.Count} 个成功结果。"
                : $"已发现 {DiscoveredExtenders.Count} 个在线 Extender。";
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            ClearDiscoveredExtenderResults();
            if (isGatewayVersionQuery)
            {
                ClearGatewayVersionQueryResult();
            }
            DeviceDiscoveryStatus = isGatewayVersionQuery
                ? "刷新 Gateway 已取消或等待基础信息响应超时。"
                : "刷新 Extender 已取消或等待响应超时。";
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            ClearDiscoveredExtenderResults();
            if (isGatewayVersionQuery)
            {
                ClearGatewayVersionQueryResult();
            }
            DeviceDiscoveryStatus = isGatewayVersionQuery
                ? $"刷新 Gateway 失败：{exception.Message}"
                : $"刷新 Extender 失败：{exception.Message}";
            await SaveSettingsAsync();
        }
        finally
        {
            _suppressSelectionSync = false;
            IsRefreshingExtenders = false;
        }
    }

    private void ClearGatewayVersionQueryResult()
    {
        _gatewaySoftwareVersion = null;
        _gatewayVersionGatewayId = string.Empty;
        OnPropertyChanged(nameof(GatewayIdTaskHint));
        NotifyUpgradeActionAvailability();
    }

    private async Task RefreshNodesAsync()
    {
        if (!IsEcoLink || IsDiscoveringDevices)
        {
            return;
        }
        if (IsUpgradeInProgress)
        {
            NodeDiscoveryStatus = "升级过程中不能刷新 Node。";
            return;
        }
        if (!_mqtt.IsConnected)
        {
            ClearDiscoveredNodeResults();
            NodeDiscoveryStatus = "MQTT 尚未连接，无法刷新 Node。";
            return;
        }
        if (!uint.TryParse(GatewayId, out var gatewayId) || gatewayId == 0)
        {
            ClearDiscoveredNodeResults();
            NodeDiscoveryStatus = "Gateway ID 必须是十进制正整数。";
            return;
        }
        if (!IsGatewayTopicSubscribed)
        {
            ClearDiscoveredNodeResults();
            NodeDiscoveryStatus = "请先订阅当前 Gateway dev ID 对应的主题。";
            return;
        }
        IsRefreshingNodes = true;
        try
        {
            var extenderIds = DiscoveredExtenders.Where(item => item.IsSelected)
                .Select(item => item.ExtenderId)
                .Take(16)
                .ToArray();
            if (extenderIds.Length == 0)
            {
                ClearDiscoveredNodeResults();
                NodeDiscoveryStatus = "请先勾选至少一个 Extender。";
                return;
            }

            var previous = TryParseNodeTargets(NodeTargetsText)
                .Where(target => uint.TryParse(target.ExtenderId, out _))
                .GroupBy(target => uint.Parse(target.ExtenderId))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .SelectMany(target => target.NodeIds)
                        .Select(value => ushort.TryParse(value, out var nodeId) ? nodeId : (ushort)0)
                        .Where(value => value > 0)
                        .ToHashSet());
            ClearDiscoveredNodeResults();
            NodeDiscoveryStatus = $"正在查询 {extenderIds.Length} 个 Extender 的 Node 注册列表…";
            var results = await _deviceDiscovery.DiscoverNodesAsync(gatewayId.ToString(), extenderIds);
            _suppressSelectionSync = true;
            foreach (var result in results.Where(result => result.IsSuccess))
            {
                previous.TryGetValue(result.ExtenderId, out var selectedNodes);
                DiscoveredNodeGroups.Add(new NodeGroupItem(
                    result.ExtenderId,
                    result.Nodes,
                    selectedNodes ?? [],
                    result.Error,
                    result.ReportedCount,
                    OnNodeSelectionChanged));
            }
            _nodeDiscoveryCompletedAt = DateTimeOffset.Now;
            RefreshNodeTypeOptions();
            RefreshNodeEligibility();
            SelectEligibleNodesAfterRefresh();
            _suppressSelectionSync = false;
            OnNodeSelectionChanged();
            var failed = results.Count(item => !item.IsSuccess);
            var protocolTotal = results.Where(item => item.IsSuccess).Sum(item => item.ReportedCount);
            var onlineVisible = results.Where(item => item.IsSuccess).Sum(item => item.Nodes.Count);
            NodeDiscoveryStatus = failed == results.Count
                ? "Node 列表刷新失败：所有 Extender 均未响应，请确认固件支持 cmd=100/0x0E→0x0F。"
                : $"Node 列表刷新完成：协议返回 {protocolTotal} 个，在线显示 {onlineVisible} 个，失败 Extender {failed} 个。";
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            NodeDiscoveryStatus = "刷新 Node 已取消或等待响应超时。";
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            NodeDiscoveryStatus = $"刷新 Node 失败：{exception.Message}";
            await SaveSettingsAsync();
        }
        finally
        {
            _suppressSelectionSync = false;
            IsRefreshingNodes = false;
        }
    }

    private void ClearDiscoveredExtenderResults()
    {
        _suppressSelectionSync = true;
        DiscoveredExtenders.Clear();
        DiscoveredNodeGroups.Clear();
        _suppressSelectionSync = false;
        OnPropertyChanged(nameof(ExtenderSelectionToggleText));
        OnPropertyChanged(nameof(NodeSelectionToggleText));
    }

    private void ClearDiscoveredNodeResults()
    {
        _suppressSelectionSync = true;
        DiscoveredNodeGroups.Clear();
        _suppressSelectionSync = false;
        OnPropertyChanged(nameof(NodeSelectionToggleText));
    }

    private void OnExtenderSelectionChanged()
    {
        if (_suppressSelectionSync)
        {
            return;
        }
        if (SelectedTaskType is SyncTaskType or AsyncTaskType)
        {
            TargetIdList = string.Join(
                Environment.NewLine,
                DiscoveredExtenders.Where(item => item.IsSelected)
                    .Select(item => item.ExtenderId));
        }
        OnPropertyChanged(nameof(ExtenderSelectionToggleText));
        NotifyUpgradeActionAvailability();
        ScheduleSettingsAutoSave();
    }

    private void OnNodeSelectionChanged()
    {
        if (_suppressSelectionSync)
        {
            return;
        }
        NodeTargetsText = string.Join(
            Environment.NewLine,
            DiscoveredNodeGroups
                .Select(group => new
                {
                    group.ExtenderId,
                    Nodes = group.Nodes.Where(node => node.IsSelected).Select(node => node.NodeId).ToArray(),
                })
                .Where(item => item.Nodes.Length > 0)
                .Select(item => $"{item.ExtenderId}: {string.Join(',', item.Nodes)}"));
        OnPropertyChanged(nameof(NodeSelectionToggleText));
        NotifyUpgradeActionAvailability();
        ScheduleSettingsAutoSave();
    }

    private void ToggleNodeSelection(object? _)
    {
        var nodes = DiscoveredNodeGroups.SelectMany(group => group.Nodes).ToArray();
        if (nodes.Length == 0)
        {
            NodeDiscoveryStatus = "暂无可选择的 Node，请先刷新。";
            return;
        }
        var selected = nodes.Any(node => !node.IsSelected);
        _suppressSelectionSync = true;
        foreach (var group in DiscoveredNodeGroups)
        {
            group.SetAll(selected);
        }
        _suppressSelectionSync = false;
        OnNodeSelectionChanged();
        NodeDiscoveryStatus = selected
            ? $"已选择全部 {nodes.Length} 个 Node。"
            : "已取消全部 Node。";
    }

    private void SelectNodesByType(int nodeType)
    {
        if (_suppressSelectionSync || DiscoveredNodeGroups.Count == 0)
        {
            return;
        }
        _suppressSelectionSync = true;
        foreach (var node in DiscoveredNodeGroups.SelectMany(group => group.Nodes))
        {
            node.IsSelected = node.NodeType == nodeType && node.CanSelect;
        }
        _suppressSelectionSync = false;
        OnNodeSelectionChanged();
        NodeDiscoveryStatus = $"已按 {NodeTypeCatalog.Format(nodeType)} 选择匹配节点。";
    }

    private void ClearNodeSelection()
    {
        _suppressSelectionSync = true;
        foreach (var node in DiscoveredNodeGroups.SelectMany(group => group.Nodes))
        {
            node.IsSelected = false;
        }
        _suppressSelectionSync = false;
        OnNodeSelectionChanged();
        NodeDiscoveryStatus = "已取消选择全部 Node。";
    }

    private void SelectEligibleNodesAfterRefresh()
    {
        foreach (var node in DiscoveredNodeGroups.SelectMany(group => group.Nodes))
        {
            node.IsSelected = _selectedNodeTypeValue > 0 &&
                              node.NodeType == _selectedNodeTypeValue &&
                              node.CanSelect;
        }
    }

    private void UpdateGatewayStatus(GatewayOtaStatus? status)
    {
        if (status is null)
        {
            return;
        }
        _lastGatewayStatus = status;
        var freezeRunningAnimation = _runner?.HasActiveTask != true ||
                                     _runner.IsPollingPaused;
        var transferProgressPercent = CalculateProgressPercent(status);
        var activeSubtask = status.Subtasks.FirstOrDefault(subtask =>
            subtask.Result.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            ?? status.Subtasks.FirstOrDefault(subtask =>
                subtask.Result.Equals("RUNNING", StringComparison.OrdinalIgnoreCase));
        var displayState = status.Status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) &&
            activeSubtask?.Result.Equals("FAILED", StringComparison.OrdinalIgnoreCase) == true
                ? "FAILED"
                : status.Status;
        var usesCachedPackage = status.UsesCachedPackage;
        GatewayPackageSourceSummary = OtaStatusDisplay.PackageSourceSummary(status);
        GatewayPackageSourceColor = usesCachedPackage
            ? "#168A55"
            : string.Equals(status.PackageSource, "TRANSFER", StringComparison.OrdinalIgnoreCase)
                ? "#2C68D8"
                : "#65758B";
        var displayStage = status.Stage;
        var progressPercent = displayStage.Equals("TRANSFER", StringComparison.OrdinalIgnoreCase) && !usesCachedPackage
            ? transferProgressPercent
            : null;
        var progressText = status.Status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) && progressPercent.HasValue
            ? $" · 进度 {progressPercent.Value:0.0}%"
            : string.Empty;
        var isNodePrepareTimeout = activeSubtask is not null &&
            OtaStatusDisplay.IsNodePrepareTimeout(
                activeSubtask.Result,
                activeSubtask.Reason,
                activeSubtask.PreparedCount,
                activeSubtask.TargetCount);
        var subtaskText = activeSubtask is null
            ? string.Empty
            : isNodePrepareTimeout
                ? $" · Extender {activeSubtask.ExtenderId}"
                : $" · Extender {activeSubtask.ExtenderId}：成功 {activeSubtask.SuccessCount}/{activeSubtask.TargetCount}";
        GatewayStageSummary = $"{OtaStatusDisplay.State(displayState)} · {OtaStatusDisplay.StageSummary(displayStage, activeSubtask, _gatewayStatusDeviceType, usesCachedPackage)}{progressText}{subtaskText} · 已用时 {DurationDisplay.Format(status.TaskElapsedMs ?? 0)}";
        GatewayStageColor = StatusColor.For(displayState);
        if (_gatewayTaskSequence != status.TaskSequence)
        {
            _gatewayTaskSequence = status.TaskSequence;
            _gatewayTaskStartedAt = status.TaskElapsedMs.HasValue
                ? DateTimeOffset.Now - TimeSpan.FromMilliseconds(Math.Max(0, status.TaskElapsedMs.Value))
                : null;
        }
        else if (_gatewayTaskStartedAt is null && status.TaskElapsedMs.HasValue)
        {
            _gatewayTaskStartedAt = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Math.Max(0, status.TaskElapsedMs.Value));
        }
        GatewayStages.Clear();
        foreach (var stage in status.Stages.Where(stage =>
                     OtaStagePolicy.IsApplicable(_gatewayStatusDeviceType, stage.Stage)))
        {
            var isCacheReuseTransfer = usesCachedPackage &&
                stage.Stage.Equals("TRANSFER", StringComparison.OrdinalIgnoreCase);
            var stageState = isCacheReuseTransfer ? "SKIPPED" : stage.State;
            var stageReason = isCacheReuseTransfer ? "CACHE_REUSED" : stage.Reason;
            var localStartTime = isCacheReuseTransfer
                ? "—"
                : stageState.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
                ? "未开始"
                : _gatewayTaskStartedAt?.AddMilliseconds(stage.StartOffsetMs).ToLocalTime().ToString("HH:mm:ss.fff")
                    ?? $"偏移 {stage.StartOffsetMs} ms";
            GatewayStages.Add(new GatewayStageViewItem(
                stage.Stage,
                _gatewayStatusDeviceType,
                stageState,
                stage.StartOffsetMs,
                isCacheReuseTransfer ? 0 : stage.DurationMs,
                stageReason,
                localStartTime,
                !isCacheReuseTransfer &&
                stageState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) &&
                stage.Stage.Equals("TRANSFER", StringComparison.OrdinalIgnoreCase)
                    ? transferProgressPercent
                    : null,
                freezeRunningAnimation,
                usesCachedPackage));
        }
        GatewaySubtasks.Clear();
        foreach (var subtask in status.Subtasks)
        {
            GatewaySubtasks.Add(new GatewaySubtaskViewItem(
                subtask.ExtenderId,
                subtask.Stage,
                subtask.Result,
                subtask.ElapsedMs,
                subtask.TargetCount,
                subtask.PreparedCount,
                subtask.SuccessCount,
                subtask.FailedCount,
                subtask.Reason,
                subtask.CacheResult));
        }
    }

    private static double? CalculateProgressPercent(GatewayOtaStatus status)
    {
        if (status.FileSize is not > 0 || status.TransferredBytes is not { } transferredBytes)
        {
            return null;
        }
        return Math.Clamp(transferredBytes * 100.0 / status.FileSize.Value, 0.0, 100.0);
    }

    private static IEnumerable<uint> ParsePositiveUIntLines(string text)
        => text.Split(['\r', '\n', ',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => uint.TryParse(value, out var parsed) ? parsed : 0U)
            .Where(value => value > 0);

    private static IReadOnlyList<OtaExtenderTarget> TryParseNodeTargets(string text)
    {
        try
        {
            return ParseNodeTargets(text);
        }
        catch
        {
            return [];
        }
    }

    private async void OnTaskUpdated(object? sender, OtaExecutionUpdate update)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => OnTaskUpdated(sender, update));
            return;
        }
        TaskStatusMessage = update.Message;
        if (update.State == OtaTaskState.Succeeded &&
            _activeReport?.Task is { } completedTask &&
            completedTask.Id == update.TaskId)
        {
            ApplySuccessfulUpgradeVersion(completedTask);
        }
        if (!_isCycleUpgradeRunning)
        {
            UpgradeRunProgressText = $"单次升级 · {update.Message}";
        }
        UpdateGatewayStatus(update.GatewayStatus);
        UpdateTerminalSummary(update);
        OnPropertyChanged(nameof(PollingToggleText));
        NotifyUpgradeActionAvailability();
        OnPropertyChanged(nameof(CanControlPolling));
        OnPropertyChanged(nameof(CanCancelTask));
        if (_activeReport is null || !_reportTaskIds.Contains(update.TaskId))
        {
            return;
        }
        var report = _activeReport;
        report.AddUpdate(update);
        try
        {
            var exportedPaths = await SaveReportAsync(
                report,
                autoExport: IsTerminalState(update.State) && !_isCycleUpgradeRunning);
            if (exportedPaths is { } paths)
            {
                await RefreshCurrentReportsAfterExportAsync();
                NotifyReportExported(report, paths);
            }
        }
        catch (Exception exception)
        {
            NotifyReportExportFailed(exception);
        }
    }

    private static bool IsTerminalState(OtaTaskState state)
        => state is OtaTaskState.Succeeded or OtaTaskState.Failed or OtaTaskState.Cancelled or OtaTaskState.TimedOut;

    private void UpdateTerminalSummary(OtaExecutionUpdate update)
    {
        if (update.GatewayStatus is not null ||
            update.State is not (OtaTaskState.Succeeded or
                OtaTaskState.Failed or
                OtaTaskState.Cancelled or
                OtaTaskState.TimedOut))
        {
            return;
        }
        var stateCode = update.State switch
        {
            OtaTaskState.Succeeded => "SUCCESS",
            OtaTaskState.Cancelled => "CANCELLED",
            OtaTaskState.TimedOut => "TIMEDOUT",
            _ => "FAILED",
        };
        var terminalStage = update.Message.Contains("cmd=8", StringComparison.OrdinalIgnoreCase)
            ? "状态查询中断"
            : "任务已结束";
        var hasTerminalGatewayFact = _lastGatewayStatus is not null &&
            (!_lastGatewayStatus.Status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ||
             _lastGatewayStatus.Subtasks.Any(subtask =>
                 subtask.Result.Equals("FAILED", StringComparison.OrdinalIgnoreCase)));
        if (hasTerminalGatewayFact)
        {
            UpdateGatewayStatus(_lastGatewayStatus!);
            GatewayStageColor = StatusColor.For(stateCode);
            return;
        }
        GatewayStageSummary = $"{OtaStatusDisplay.State(stateCode)} · {terminalStage}";
        GatewayStageColor = StatusColor.For(stateCode);
    }

    private void TogglePolling(object? _)
    {
        if (_runner is null || !_runner.HasActiveTask)
        {
            TaskStatusMessage = "当前没有可暂停或恢复的 EcoLink 轮询任务。";
            return;
        }
        if (_runner.IsPollingPaused)
        {
            if (!_runner.ResumePolling()) TaskStatusMessage = "传统模式不支持状态轮询。";
        }
        else if (!_runner.PausePolling())
        {
            TaskStatusMessage = "传统模式不支持状态轮询。";
        }
        OnPropertyChanged(nameof(PollingToggleText));
        if (_lastGatewayStatus is not null)
        {
            UpdateGatewayStatus(_lastGatewayStatus);
        }
    }

    private Task CancelTaskAsync()
    {
        if (!CanCancelTask)
        {
            TaskStatusMessage = "当前没有可取消的 OTA 任务。";
            return Task.CompletedTask;
        }

        OpenPatchDialog(
            PatchDialogAction.CancelTask,
            "确认取消任务",
            _runner?.HasActiveTask == true
                ? "确定要取消当前升级任务吗？\n\n取消后，工具将停止状态跟踪，并通知 Gateway 终止升级。"
                : "确定要取消当前循环升级吗？\n\n取消后，等待立即结束，后续单次升级不会启动。",
            "确认取消");
        return Task.CompletedTask;
    }

    private async Task CancelActiveTaskAsync()
    {
        if (!CanCancelTask)
        {
            TaskStatusMessage = "当前任务已经结束，无需取消。";
            return;
        }
        var hadActiveTask = _runner?.HasActiveTask == true;
        if (hadActiveTask)
        {
            await _runner!.CancelAndNotifyGatewayAsync();
        }
        else
        {
            TaskStatusMessage = "已取消循环升级等待，后续单次升级不会启动。";
        }
        _cycleCancellation?.Cancel();
        OnPropertyChanged(nameof(PollingToggleText));
        NotifyUpgradeActionAvailability();
        OnPropertyChanged(nameof(CanControlPolling));
        OnPropertyChanged(nameof(CanCancelTask));
    }

    private async Task StartCycleAsync()
    {
        if (!CanStartUpgrade)
        {
            TaskStatusMessage = "已有升级任务正在确认或执行中，不能重复启动循环升级。";
            return;
        }

        if (CycleRounds <= 0)
        {
            TaskStatusMessage = "循环轮数必须大于 0。";
            return;
        }
        var cycleInterval = CycleIntervalMode == "随机间隔"
            ? new OtaCycleIntervalOptions(
                OtaCycleIntervalMode.Random,
                RandomMinimumSeconds: CycleRandomMinimumSeconds,
                RandomMaximumSeconds: CycleRandomMaximumSeconds)
            : new OtaCycleIntervalOptions(
                OtaCycleIntervalMode.Fixed,
                FixedSeconds: CycleFixedIntervalSeconds);
        if (cycleInterval.Validate() is { } cycleIntervalError)
        {
            TaskStatusMessage = $"循环任务未启动：{cycleIntervalError}";
            return;
        }
        var selectedForwardPatch = SelectedUpgradePatch;
        var selectedReversePatch = SelectedReverseUpgradePatch;
        if (selectedForwardPatch is null || selectedReversePatch is null ||
            !File.Exists(selectedForwardPatch.FilePath) || !File.Exists(selectedReversePatch.FilePath))
        {
            TaskStatusMessage = "循环任务未启动：必须选择正向和反向 Patch。";
            return;
        }
        _patchPath = selectedForwardPatch.FilePath;
        _reversePatchPath = selectedReversePatch.FilePath;
        if (string.IsNullOrWhiteSpace(_reversePatchPath) || !File.Exists(_reversePatchPath))
        {
            TaskStatusMessage = "循环任务未启动：请先导入新版本→旧版本的反向 Patch。";
            return;
        }
        if (string.Equals(_reversePatchPath, _patchPath, StringComparison.OrdinalIgnoreCase))
        {
            TaskStatusMessage = "循环任务未启动：反向 Patch 不能与正向 Patch 相同，请选择新版本→旧版本的 Patch。";
            return;
        }
        if (!_mqtt.IsConnected)
        {
            TaskStatusMessage = "循环任务未启动：请先连接 MQTT。";
            return;
        }
        var mode = IsEcoLink ? OtaMode.EcoLink : OtaMode.Traditional;
        IOtaProtocolProfile profile = mode == OtaMode.EcoLink ? new EcoLinkProtocolProfile() : new TraditionalProtocolProfile();
        var deviceType = SelectedTaskType switch
        {
            GatewayTaskType => DeviceType.Gateway,
            SyncTaskType => DeviceType.Sync,
            AsyncTaskType => DeviceType.Async,
            NodeTaskType => DeviceType.Node,
            _ => throw new InvalidOperationException("未知升级类型。"),
        };
        IReadOnlyList<OtaExtenderTarget> extenderTargets;
        try { extenderTargets = deviceType == DeviceType.Node ? ParseNodeTargets(NodeTargetsText) : []; }
        catch (Exception exception) { TaskStatusMessage = $"循环任务未启动：{exception.Message}"; return; }
        if (deviceType == DeviceType.Node && ValidateDiscoveredNodeTypes(extenderTargets, NodeType) is { } nodeTypeError)
        {
            TaskStatusMessage = $"循环任务未启动：{nodeTypeError}";
            return;
        }
        if (!uint.TryParse(GatewayId, out var numericGatewayId) || numericGatewayId == 0)
        {
            TaskStatusMessage = "循环任务未启动：Gateway ID 必须填写十进制正整数。";
            return;
        }
        GatewayId = numericGatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsEcoLink && !IsGatewayTopicSubscribed)
        {
            TaskStatusMessage = "循环任务未启动：请先订阅当前 Gateway dev ID 对应的主题。";
            return;
        }
        var ids = TargetIdList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var target = BuildTaskTarget(deviceType, ids, extenderTargets);
        if ((selectedForwardPatch.IsFullImage || selectedReversePatch.IsFullImage) && deviceType != DeviceType.Gateway)
        {
            TaskStatusMessage = "循环任务未启动：完整 .bin 镜像仅支持网关升级。";
            return;
        }
        if (selectedForwardPatch.IsFullImage != selectedReversePatch.IsFullImage)
        {
            TaskStatusMessage = "循环任务未启动：正向和反向升级文件必须同为完整镜像或同为差分 Patch。";
            return;
        }
        try
        {
            var forwardMetadata = await PatchMetadata.FromFileAsync(_patchPath);
            var reverseMetadata = await PatchMetadata.FromFileAsync(_reversePatchPath);
            PackageManifest? forwardManifest = null;
            PackageManifest? reverseManifest = null;
            if (IsEcoLink && !selectedForwardPatch.IsFullImage)
            {
                forwardManifest = await PackageManifestImporter.LoadAndValidateAsync(_patchPath);
                reverseManifest = await PackageManifestImporter.LoadAndValidateAsync(_reversePatchPath);
                if (ValidateUpgradePreflight(
                        deviceType,
                        ids,
                        extenderTargets,
                        forwardMetadata,
                        forwardManifest,
                        OldVersion,
                        NewVersion) is { } preflightError)
                {
                    TaskStatusMessage = $"循环任务未启动：{preflightError}";
                    return;
                }
            }
            if (IsEcoLink && forwardManifest is not null && reverseManifest is not null &&
                (reverseManifest.OtaDeviceType != deviceType ||
                reverseManifest.DeviceTypeCode != forwardManifest.DeviceTypeCode ||
                reverseManifest.OldVersion != forwardManifest.NewVersion ||
                reverseManifest.NewVersion != forwardManifest.OldVersion))
            {
                TaskStatusMessage = "循环任务未启动：反向 Patch 元数据不是正向 Patch 的严格逆向版本。";
                return;
            }
            var reverseCapacity = PatchCapacityPolicy.Check(deviceType, reverseMetadata.Length, GetPatchCapacityLimits());
            if (IsEcoLink && !selectedReversePatch.IsFullImage && !reverseCapacity.IsAllowed)
            {
                TaskStatusMessage = $"循环任务未启动：{reverseCapacity.Message}";
                return;
            }
            _patchLength = forwardMetadata.Length;
            _patchMd5 = forwardMetadata.Md5;
            _patchSha256 = forwardMetadata.Sha256;
            _reversePatchLength = reverseMetadata.Length;
            _reversePatchMd5 = reverseMetadata.Md5;
            _reversePatchSha256 = reverseMetadata.Sha256;
            _reversePatchUrl = GetPatchDownloadUrl(_reversePatchPath);
            if (string.IsNullOrWhiteSpace(SelectedHttpPatchUrl) || string.IsNullOrWhiteSpace(_reversePatchUrl))
            {
                TaskStatusMessage = "循环任务未启动：本地 HTTP Range 服务未运行，且未配置可用的公网 HTTP 地址。";
                return;
            }
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"循环任务未启动：{exception.Message}";
            return;
        }
        var forward = new OtaTask
        {
            Mode = mode, DeviceType = deviceType, GatewayId = GatewayId, Target = target,
            ExtenderTargets = extenderTargets, NodeType = deviceType == DeviceType.Node ? NodeType : null,
            OldVersion = OldVersion, NewVersion = NewVersion, PatchPath = _patchPath, PatchUrl = SelectedHttpPatchUrl, PatchMd5 = _patchMd5, PatchSha256 = _patchSha256,
            ProtocolProfileId = mode == OtaMode.EcoLink ? "ecolink-gateway" : "traditional", ProtocolProfileVersion = "1.0",
        };
        var reverse = new OtaTask
        {
            Mode = mode, DeviceType = deviceType, GatewayId = GatewayId, Target = target,
            ExtenderTargets = extenderTargets, NodeType = deviceType == DeviceType.Node ? NodeType : null,
            OldVersion = NewVersion, NewVersion = OldVersion, PatchPath = _reversePatchPath, PatchUrl = _reversePatchUrl, PatchMd5 = _reversePatchMd5, PatchSha256 = _reversePatchSha256,
            ProtocolProfileId = mode == OtaMode.EcoLink ? "ecolink-gateway" : "traditional", ProtocolProfileVersion = "1.0",
        };
        var validation = OtaTaskValidator.Validate(forward, profile);
        if (!validation.IsValid) { TaskStatusMessage = $"循环任务未启动：{validation.Message}"; return; }
        validation = OtaTaskValidator.Validate(reverse, profile);
        if (!validation.IsValid) { TaskStatusMessage = $"循环任务未启动：{validation.Message}"; return; }

        if (ValidateGatewayVersionBeforeUpgrade(forward) is { } gatewayVersionError)
        {
            TaskStatusMessage = $"循环任务未启动：{gatewayVersionError}";
            return;
        }

        _pendingCycleForwardTask = forward;
        _pendingCycleReverseTask = reverse;
        _pendingCycleProfile = profile;
        _pendingCycleInterval = cycleInterval;
        _pendingCycleRounds = CycleRounds;
        NotifyUpgradeActionAvailability();
        OpenPatchDialog(
            PatchDialogAction.StartCycleUpgrade,
            "确认启动循环升级",
            BuildCycleUpgradeConfirmationMessage(forward, reverse, cycleInterval, CycleRounds),
            "确认启动");
    }

    private async Task VerifyAndStartCycleAsync(
        OtaTask forward,
        OtaTask reverse,
        IOtaProtocolProfile profile,
        OtaCycleIntervalOptions cycleInterval,
        int cycleRounds)
    {
        try
        {
        try
        {
            foreach (var (cycleTask, direction) in new[] { (forward, "正向"), (reverse, "反向") })
            {
                TaskStatusMessage = $"正在校验循环升级{direction} Patch HTTP 完整性…";
                var metadata = await PatchMetadata.FromFileAsync(cycleTask.PatchPath);
                var verification = await HttpFileVerifier.VerifyAsync(new Uri(cycleTask.PatchUrl), metadata.Length, metadata.Md5, verifyFullMd5: true);
                if (!verification.IsSuccess) { TaskStatusMessage = $"循环任务未启动：{direction} Patch HTTP 校验失败：{verification.Message}"; return; }
            }
        }
        catch (Exception exception) { TaskStatusMessage = $"循环任务未启动：{exception.Message}"; return; }
        if (_runner is not null && _runner.HasActiveTask) { TaskStatusMessage = "循环任务未启动：当前已有活动 OTA 任务。"; return; }
        if (_runner is not null) await _runner.DisposeAsync();
        _runner = new OtaTaskRunner(_mqtt, profile, _reportStore);
        _runner.Updated += OnTaskUpdated;
        _runner.MessagePublished += OnMqttMessagePublished;
        _gatewayStatusDeviceType = forward.DeviceType;
        GatewayStages.Clear();
        GatewaySubtasks.Clear();
        GatewayPackageSourceSummary = string.Empty;
        _activeReport = new OtaReport { Task = forward, LogAnalysisConclusion = forward.Mode == OtaMode.Traditional ? "日志解析不支持" : null };
        _reportTaskIds.Clear();
        _reportTaskIds.Add(forward.Id);
        _reportTaskIds.Add(reverse.Id);
        var cycle = new OtaCycleRunner();
        var startedAt = DateTimeOffset.Now;
        var completedSteps = 0;
        var successfulSteps = 0;
        cycle.StepStarting += (_, update) => RunOnUi(() =>
        {
            var task = update.IsForward ? forward : reverse;
            UpgradeRunModeText = $"循环 {update.Round}/{cycleRounds} {task.OldVersion} to {task.NewVersion}";
            UpgradeRunProgressText = $"第 {update.Round}/{cycleRounds} 轮 · {(update.IsForward ? "正向" : "反向")}升级正在执行";
        });
        cycle.Waiting += (_, update) => RunOnUi(() =>
        {
            var task = update.NextIsForward ? forward : reverse;
            UpgradeRunModeText = $"循环 {update.NextRound}/{cycleRounds} 间隔 {update.DelaySeconds} s";
            UpgradeRunProgressText = $"等待 {update.DelaySeconds} s 后执行 {task.OldVersion} to {task.NewVersion}";
        });
        cycle.Updated += (_, update) =>
        {
            completedSteps++;
            if (update.Result.State == OtaTaskState.Succeeded) successfulSteps++;
            RunOnUi(() =>
            {
                TaskStatusMessage = $"第 {update.Round} 轮{(update.IsForward ? "正向" : "反向")}：{update.Result.Message}";
                UpgradeRunProgressText = $"第 {update.Round}/{cycleRounds} 轮 · {(update.IsForward ? "正向" : "反向")} · {update.Result.Message}";
                UpgradeRunModeText = update.Result.State != OtaTaskState.Succeeded
                    ? $"循环 {update.Round}/{cycleRounds} {(update.IsForward ? $"{forward.OldVersion} to {forward.NewVersion}" : $"{reverse.OldVersion} to {reverse.NewVersion}")}"
                    : update.IsForward
                        ? $"循环 {update.Round}/{cycleRounds} {reverse.OldVersion} to {reverse.NewVersion}"
                        : update.Round < cycleRounds
                            ? $"循环 {update.Round + 1}/{cycleRounds} {forward.OldVersion} to {forward.NewVersion}"
                            : $"循环 {cycleRounds}/{cycleRounds} 已完成";
            });
        };
        UpgradeRunModeText = $"循环 1/{cycleRounds} {forward.OldVersion} to {forward.NewVersion}";
        UpgradeRunModeForeground = "#7A4CC2";
        UpgradeRunModeBackground = "#F1EAFE";
        UpgradeRunProgressText = $"共 {cycleRounds} 轮 · 准备执行第 1 轮正向升级";
        _isCycleUpgradeRunning = true;
        _cycleCancellation = new CancellationTokenSource();
        NotifyUpgradeActionAvailability();
        try
        {
            var result = await cycle.RunAsync(
                new OtaCycleDefinition(forward, reverse, cycleRounds, cycleInterval),
                _runner,
                _cycleCancellation.Token);
            TaskStatusMessage = result.Message;
            UpgradeRunProgressText = $"循环升级结束 · {result.Message}";
            if (_activeReport is not null)
            {
                if (!IsTerminalState(_activeReport.FinalState))
                {
                    _activeReport.AddUpdate(new OtaExecutionUpdate(
                        forward.Id,
                        result.State,
                        result.Message,
                        result.OccurredAt));
                }
                _activeReport.Cycle = new OtaCycleSummary(cycleRounds, completedSteps, successfulSteps, DateTimeOffset.Now - startedAt, result.Message);
                try
                {
                    var exportedPaths = await SaveReportAsync(_activeReport, autoExport: true);
                    if (exportedPaths is { } paths)
                    {
                        await RefreshCurrentReportsAfterExportAsync();
                        NotifyReportExported(_activeReport, paths);
                    }
                }
                catch (Exception exception)
                {
                    NotifyReportExportFailed(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            TaskStatusMessage = "循环升级已取消。";
            UpgradeRunProgressText = "循环升级已取消，后续单次升级不会启动。";
        }
        finally
        {
            _isCycleUpgradeRunning = false;
            _cycleCancellation?.Dispose();
            _cycleCancellation = null;
            NotifyUpgradeActionAvailability();
        }
        }
        finally
        {
            _isUpgradeStartInProgress = false;
            NotifyUpgradeActionAvailability();
        }
    }

    private async Task<(string HtmlPath, string JsonPath)?> SaveReportAsync(OtaReport report, bool autoExport)
    {
        await _reportWriteLock.WaitAsync();
        try
        {
            await _reportStore.SaveAsync(report);
            if (!autoExport || !_autoExportedReportIds.Add(report.Id))
            {
                return null;
            }

            try
            {
                var outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OtaTool",
                    "reports");
                var baseName = $"ota-report-{report.Id:N}";
                var jsonPath = await OtaReportExporter.ExportJsonAsync(report, Path.Combine(outputDirectory, baseName + ".json"));
                var htmlPath = await OtaReportExporter.ExportHtmlAsync(report, Path.Combine(outputDirectory, baseName + ".html"));
                return (htmlPath, jsonPath);
            }
            catch
            {
                _autoExportedReportIds.Remove(report.Id);
                throw;
            }
        }
        finally
        {
            _reportWriteLock.Release();
        }
    }

    private void NotifyReportExported(OtaReport report, (string HtmlPath, string JsonPath) paths)
    {
        var outputDirectory = Path.GetDirectoryName(paths.HtmlPath) ?? paths.HtmlPath;
        TaskStatusMessage = $"测试完成，报告已导出到 {outputDirectory}";
        OpenPatchDialog(
            PatchDialogAction.Information,
            report.FinalState == OtaTaskState.Succeeded ? "测试完成" : "测试结束",
            $"测试完成，报告已自动导出到：{Environment.NewLine}{outputDirectory}{Environment.NewLine}{Environment.NewLine}" +
            $"HTML：{Path.GetFileName(paths.HtmlPath)}{Environment.NewLine}JSON：{Path.GetFileName(paths.JsonPath)}",
            "知道了",
            report.FinalState == OtaTaskState.Succeeded ? "通过" : "失败",
            report.FinalState == OtaTaskState.Succeeded ? "#159E68" : "#C73A3A");
    }

    private void NotifyReportExportFailed(Exception exception)
    {
        TaskStatusMessage = $"测试已结束，但报告自动导出失败：{exception.Message}";
        OpenPatchDialog(
            PatchDialogAction.Information,
            "报告导出失败",
            TaskStatusMessage,
            "知道了");
    }

    private async Task TestPublishConnectionAsync()
    {
        if (IsTestingPublishConnection || IsPublishing) return;
        IsTestingPublishConnection = true;
        try
        {
            PublishConnectionTestStatus = "正在测试 SFTP 和 HTTP 连接…";
            var options = new SftpPublishOptions(SftpHost, SftpPort, SftpUserName, SftpRemoteDirectory, PublicHttpBaseUrl,
                Password: SftpPassword, PrivateKeyPath: SftpPrivateKeyPath, PrivateKeyPassphrase: SftpPrivateKeyPassphrase,
                ExpectedHostKeySha256: SftpHostKeySha256);
            var publisher = new SshNetSftpPublisher();
            var sftpResult = await publisher.TestConnectionAsync(options);
            string httpResult;
            try
            {
                httpResult = await TestHttpEndpointAsync(PublicHttpBaseUrl);
            }
            catch (Exception exception)
            {
                httpResult = $"HTTP 连接失败：{CompactError(exception.Message)}";
            }
            PublishConnectionTestStatus = $"{sftpResult.Message}；{httpResult}";
        }
        catch (Exception exception)
        {
            PublishConnectionTestStatus = $"连接测试失败：{CompactError(exception.Message)}";
        }
        finally
        {
            IsTestingPublishConnection = false;
        }
    }

    private static async Task<string> TestHttpEndpointAsync(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "HTTP 地址格式无效。";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode
            ? $"HTTP 可连接（HEAD {(int)response.StatusCode}）。"
            : $"HTTP 可连接（HEAD {(int)response.StatusCode}；目录可能不提供列表，发布后会用具体 Patch 文件再次校验）。";
    }

    private Task PublishPatchAsync()
    {
        var existingPatches = _patchCatalog.Values
            .Where(item => File.Exists(item.FilePath))
            .ToArray();
        var markedPatches = existingPatches
            .Where(item => item.IsSelectedForPublish)
            .ToArray();
        if (markedPatches.Length == 0)
        {
            PublishStatus = existingPatches.Length == 0
                ? "Patch 输出目录中没有可发布的 Patch。"
                : "请先在 Patch 详情中勾选本次需要发布的 Patch。";
            return Task.CompletedTask;
        }
        var patches = markedPatches
            .Where(item => item.ManifestVerified && File.Exists(item.FilePath))
            .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (patches.Length == 0)
        {
            PublishStatus = "已勾选的 Patch 尚未通过还原验证，不能发布。请先导入对应 A/B 固件并执行“导入 Patch 验证”。";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(PublicHttpBaseUrl))
        {
            PublishStatus = "发布失败：请先在 PATCH 中心设置公网 HTTP 服务。";
            return Task.CompletedTask;
        }

        var unpublishedPatches = patches.Where(item => !_publishedPatchKeys.Contains(BuildPublishedPatchKey(item))).ToArray();
        if (unpublishedPatches.Length == 0)
        {
            foreach (var patch in patches) patch.IsSelectedForPublish = false;
            HasPublishedPatches = true;
            PublishStatus = "已勾选的 Patch 均已发布且未发生变化，无需重复上传。";
            TaskStatusMessage = "Patch 未变化，已跳过重复发布；可直接在升级任务中选择 Patch 启动升级。";
            return Task.CompletedTask;
        }

        var patchDetails = string.Join(
            Environment.NewLine,
            unpublishedPatches.Select(patch => $"• {patch.FileName}\n  大小：{patch.Length:N0} B\n  MD5：{patch.Md5}\n  来源：{patch.Source}"));
        _pendingPublication = unpublishedPatches;
        OpenPatchDialog(
            PatchDialogAction.Publish,
            "确认 Patch 发布",
            $"将发布以下 {unpublishedPatches.Length} 个 Patch：\n\n{patchDetails}\n\n发布位置：{SftpRemoteDirectory}\nHTTP 地址：{PublicHttpBaseUrl}\n\n确认后开始上传并校验。",
            "确认发布");
        return Task.CompletedTask;
    }

    private async Task PublishPatchesAsync(IReadOnlyList<PatchSelection> patches)
    {
        if (IsPublishing || IsTestingPublishConnection) return;
        try
        {
            HasPublishedPatches = false;
            IsPublishing = true;
            PublishStatus = "正在上传并验证公网文件…";
            var options = new SftpPublishOptions(SftpHost, SftpPort, SftpUserName, SftpRemoteDirectory, PublicHttpBaseUrl,
                Password: SftpPassword, PrivateKeyPath: SftpPrivateKeyPath, PrivateKeyPassphrase: SftpPrivateKeyPassphrase,
                ExpectedHostKeySha256: SftpHostKeySha256);
            var publisher = new SshNetSftpPublisher();
            var publishedFiles = new List<PublishedFile>(patches.Count);
            foreach (var patch in patches)
            {
                var published = await publisher.PublishAsync(patch.FilePath, options);
                PublishStatus = $"SFTP 上传成功：{published.RemotePath}；正在校验 HTTP：{published.PublicUri}";
                await publisher.VerifyHttpAsync(published);
                publishedFiles.Add(published);
                _publishedPatchKeys.Add(BuildPublishedPatchKey(patch));
                patch.IsSelectedForPublish = false;
            }
            HttpUsesLocalServer = false;
            var fileNames = string.Join("、", publishedFiles.Select(file => Path.GetFileName(file.RemotePath)));
            HasPublishedPatches = true;
            PublishStatus = $"{publishedFiles.Count} 个 Patch 发布并完整校验通过：{fileNames}";
            TaskStatusMessage = $"公网 Patch 发布完成：{fileNames}。请在升级任务中选择要使用的 Patch。";
        }
        catch (Exception exception)
        {
            PublishStatus = exception.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
                ? $"发布失败：{exception.Message}。请在 MobaXTerm 确认文件已在 {SftpRemoteDirectory}，再访问 {PublicHttpBaseUrl}文件名。"
                : $"发布失败：{exception.Message}";
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private async Task AnalyzeLogsAsync()
    {
        if (!IsEcoLink)
        {
            LogAnalysisStatus = "传统模式不支持日志解析。";
            return;
        }
        var selectedLogFiles = ImportedLogFiles.ToArray();
        if (selectedLogFiles.Length == 0)
        {
            LogAnalysisStatus = "请先导入至少一个 .log 文件。";
            return;
        }

        string? analysisInputDirectory = null;
        try
        {
            analysisInputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OtaTool",
                "log-analysis",
                "inputs",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(analysisInputDirectory);
            foreach (var item in selectedLogFiles)
            {
                if (!File.Exists(item.FilePath))
                {
                    throw new FileNotFoundException($"日志文件已不存在：{item.FileName}", item.FilePath);
                }
                File.Copy(item.FilePath, Path.Combine(analysisInputDirectory, item.FileName));
            }

            LogAnalysisStatus = $"正在分析列表中的 {selectedLogFiles.Length} 个日志文件…";
            LogAnalysisResultText = "正在分析日志，请稍候…";
            LogAnalysisQualityScore = "…";
            LogAnalysisQualityGrade = "分析中";
            LogAnalysisQualitySummary = "正在汇总升级闭环、完成度、可靠性和时延指标。";
            LogAnalysisQualityColor = "#2570E8";
            var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtaTool", "log-analysis");
            using var result = await new ExternalEcoLinkLogAnalyzer().AnalyzeAsync(
                new LogAnalysisRequest(OtaMode.EcoLink, LogAnalyzerExecutablePath, analysisInputDirectory, outputDirectory));
            LogAnalysisStatus = $"{result.Message}（已分析 {selectedLogFiles.Length} 个日志文件）";
            if (result.Data is not null)
            {
                var quality = OtaUpgradeQualityEvaluator.Evaluate(result.Data.RootElement);
                LogAnalysisQualityScore = quality.Score.ToString(System.Globalization.CultureInfo.InvariantCulture);
                LogAnalysisQualityGrade = quality.Grade;
                LogAnalysisQualitySummary = quality.Summary;
                LogAnalysisQualityColor = quality.Color;
                var analyzerSummary = !string.IsNullOrWhiteSpace(result.HumanReadableReport)
                    ? result.HumanReadableReport
                    : result.Message;
                var readableSummary = string.Join(
                    Environment.NewLine,
                    analyzerSummary.Split(
                            ["\r\n", "\n", "\r"],
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(line => line.StartsWith('•') ? line : $"• {line}"));
                LogAnalysisResultText = $"{quality.Details}{Environment.NewLine}{Environment.NewLine}日志分析摘要{Environment.NewLine}{Environment.NewLine}{readableSummary}";
            }
            else
            {
                LogAnalysisQualityScore = "--";
                LogAnalysisQualityGrade = "无法评估";
                LogAnalysisQualitySummary = result.Message;
                LogAnalysisQualityColor = "#C53333";
                LogAnalysisResultText = result.Message;
            }
            if (result.IsSuccess && _activeReport is not null)
            {
                _activeReport.LogAnalysisConclusion = LogAnalysisResultText;
                await _reportStore.SaveAsync(_activeReport);
            }
        }
        catch (Exception exception)
        {
            LogAnalysisStatus = $"日志分析失败：{exception.Message}";
            LogAnalysisQualityScore = "--";
            LogAnalysisQualityGrade = "分析失败";
            LogAnalysisQualitySummary = "未获得可用于评分的结构化日志结果。";
            LogAnalysisQualityColor = "#C53333";
            LogAnalysisResultText = LogAnalysisStatus;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(analysisInputDirectory))
            {
                try
                {
                    if (Directory.Exists(analysisInputDirectory))
                    {
                        Directory.Delete(analysisInputDirectory, recursive: true);
                    }
                }
                catch
                {
                    // 临时快照将在后续系统清理中移除，不影响分析结果。
                }
            }
        }
    }

    private async Task LoadReportsAsync(bool updateStatus = true)
    {
        var modeState = IsEcoLink ? _ecoLinkUpgradeUiState : _traditionalUpgradeUiState;
        var selectedId = modeState.SelectedReportId ?? SelectedReport?.Id;
        RecentReports.Clear();
        foreach (var report in (await _reportStore.LoadRecentAsync(200))
                     .Where(report => report.Task.Mode == (IsEcoLink ? OtaMode.EcoLink : OtaMode.Traditional))
                     .Where(report => report.IsArchived == _showArchivedReports)
                     .Take(100))
        {
            RecentReports.Add(new ReportListItem(report, GetReportOutputDirectory()));
        }
        SelectedReport = RecentReports.FirstOrDefault(item => item.Id == selectedId)
            ?? RecentReports.FirstOrDefault();
        modeState.SelectedReportId = SelectedReport?.Id;
        if (updateStatus)
        {
            TaskStatusMessage = RecentReports.Count == 0
                ? (_showArchivedReports ? "暂无归档报告。" : "暂无历史报告。")
                : $"已加载 {RecentReports.Count} 条{(_showArchivedReports ? "归档" : "历史")}报告。";
        }
    }

    private async Task RefreshCurrentReportsAfterExportAsync()
    {
        if (_showArchivedReports)
        {
            _showArchivedReports = false;
            NotifyReportScopeChanged();
        }
        await LoadReportsAsync(updateStatus: false);
    }

    private static string GetReportOutputDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OtaTool",
            "reports");

    private void ShowReportScope(bool showArchived)
    {
        if (_showArchivedReports == showArchived) return;
        _showArchivedReports = showArchived;
        NotifyReportScopeChanged();
        _ = LoadReportsAsync(updateStatus: false);
    }

    private void NotifyReportScopeChanged()
    {
        OnPropertyChanged(nameof(IsShowingActiveReports));
        OnPropertyChanged(nameof(IsShowingArchivedReports));
        OnPropertyChanged(nameof(ActiveReportsHeader));
        OnPropertyChanged(nameof(ReportScopeDescription));
    }

    private async Task OpenSelectedReportAsync()
    {
        if (SelectedReport is null)
        {
            TaskStatusMessage = "请先选择要查看的报告。";
            return;
        }

        var htmlPath = await OtaReportExporter.ExportHtmlAsync(SelectedReport.Report, SelectedReport.HtmlPath);
        await OtaReportExporter.ExportJsonAsync(SelectedReport.Report, SelectedReport.JsonPath);
        Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
        TaskStatusMessage = $"已打开报告：{Path.GetFileName(htmlPath)}";
    }

    private async Task ToggleSelectedReportArchiveAsync()
    {
        if (SelectedReport is null)
        {
            TaskStatusMessage = "请先选择要归档或恢复的报告。";
            return;
        }

        var report = SelectedReport.Report;
        var wasArchived = report.IsArchived;
        report.ArchivedAt = wasArchived ? null : DateTimeOffset.Now;
        await _reportStore.SaveAsync(report);
        TaskStatusMessage = wasArchived ? "报告已恢复到当前报告。" : "报告已归档。";
        await LoadReportsAsync(updateStatus: false);
    }

    private void RequestSelectedReportDeletion()
    {
        if (SelectedReport is null)
        {
            TaskStatusMessage = "请先选择要删除的报告。";
            return;
        }

        _pendingReportDeletion = SelectedReport;
        OpenPatchDialog(
            PatchDialogAction.DeleteReport,
            "删除历史报告",
            $"确认删除以下报告及其导出的 HTML、JSON 文件吗？\n\n{SelectedReport.StartedAt}\n{SelectedReport.Mode} · {SelectedReport.DeviceType} · {SelectedReport.State}\n\n此操作不可恢复。",
            "确认删除");
    }

    private async Task DeleteReportAsync(ReportListItem item)
    {
        await _reportStore.DeleteAsync(item.Id);
        if (File.Exists(item.HtmlPath)) File.Delete(item.HtmlPath);
        if (File.Exists(item.JsonPath)) File.Delete(item.JsonPath);
        TaskStatusMessage = $"已删除报告：{item.StartedAt}";
        await LoadReportsAsync(updateStatus: false);
    }

    private async Task LoadSettingsAsync(AppSettings settings)
    {
        try
        {
            _modeWorkspaces.Clear();
            if (settings.ModeWorkspaces is { Count: > 0 })
            {
                foreach (var pair in settings.ModeWorkspaces)
                {
                    var workspace = pair.Value.Copy();
                    workspace.SelectedTaskType = NormalizeTaskType(workspace.SelectedTaskType);
                    _modeWorkspaces[pair.Key] = workspace;
                }
            }
            else
            {
                var legacy = ModeWorkspaceSettings.FromLegacy(settings);
                legacy.SelectedTaskType = NormalizeTaskType(legacy.SelectedTaskType);
                _modeWorkspaces[EcoLinkModeKey] = legacy;
                var traditional = legacy.Copy();
                if (traditional.SelectedTaskType is AsyncTaskType or NodeTaskType)
                {
                    traditional.SelectedTaskType = GatewayTaskType;
                }
                _modeWorkspaces[TraditionalModeKey] = traditional;
            }
            _modeWorkspaces.TryAdd(EcoLinkModeKey, new ModeWorkspaceSettings());
            _modeWorkspaces.TryAdd(TraditionalModeKey, new ModeWorkspaceSettings());

            _isEcoLink = !string.Equals(settings.ActiveMode, TraditionalModeKey, StringComparison.OrdinalIgnoreCase);
            _ecoLinkSelectedTaskType = _modeWorkspaces[EcoLinkModeKey].SelectedTaskType;
            _traditionalSelectedTaskType = _modeWorkspaces[TraditionalModeKey].SelectedTaskType;
            ApplyMode(restoreSelectedPage: false);
            ApplyCurrentModeWorkspace();
            RestoreCurrentModeUpgradeUiState();
            await RestoreCurrentModePatchCatalogAsync();
            await LoadReportsAsync(updateStatus: false);
            SettingsStatus = "已按协议模式加载独立工作区和 Windows 凭据。";
        }
        catch (Exception exception)
        {
            SettingsStatus = $"加载设置失败：{exception.Message}";
        }
        finally
        {
            _settingsLoaded = true;
        }
    }

    private async Task SaveSettingsAsync()
    {
        await SaveSettingsCoreAsync(CancellationToken.None);
    }

    private void ScheduleSettingsAutoSave()
    {
        if (!_settingsLoaded || _restoringModeWorkspace) return;

        _settingsAutoSaveCancellation?.Cancel();
        _settingsAutoSaveCancellation?.Dispose();
        _settingsAutoSaveCancellation = new CancellationTokenSource();
        _settingsAutoSaveTask = SaveSettingsAfterDelayAsync(
            _settingsAutoSaveCancellation.Token);
    }

    private async Task SaveSettingsAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await SaveSettingsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 连续输入时只保存最后一次名称。
        }
    }

    private async Task SaveSettingsCoreAsync(
        CancellationToken cancellationToken)
    {
        await _settingsSaveLock.WaitAsync(cancellationToken);
        try
        {
            _modeWorkspaces[CurrentModeKey] = CaptureCurrentModeWorkspace();
            var settings = new AppSettings
            {
                ActiveMode = CurrentModeKey,
                ModeWorkspaces = _modeWorkspaces.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Copy(),
                    StringComparer.OrdinalIgnoreCase),
                MqttHost = MqttHost,
                MqttPort = MqttPort,
                MqttClientUsesLocalBroker = MqttClientUsesLocalBroker,
                LocalBrokerPort = LocalBrokerPort,
                LocalBrokerUserName = LocalBrokerUserName,
                HttpRootDirectory = GetPatchOutputDirectory(),
                HttpPort = HttpPort,
                HttpUsesLocalServer = HttpUsesLocalServer,
                PublicHttpBaseUrl = PublicHttpBaseUrl,
                MqttUseTls = MqttUseTls,
                MqttAcceptAnyServerCertificate = MqttAcceptAnyServerCertificate,
                MqttUserName = MqttUserName,
                SftpHost = SftpHost,
                SftpPort = SftpPort,
                SftpUserName = SftpUserName,
                SftpPrivateKeyPath = SftpPrivateKeyPath,
                SftpRemoteDirectory = SftpRemoteDirectory,
                SftpPublicBaseUrl = SftpPublicBaseUrl,
                SftpHostKeySha256 = SftpHostKeySha256,
                LogAnalyzerExecutablePath = LogAnalyzerExecutablePath,
                LogDirectory = LogDirectory,
                SelectedTaskType = SelectedTaskType,
                OldVersion = OldVersion,
                NewVersion = NewVersion,
                ForwardPatchName = ForwardPatchName,
                ReversePatchName = ReversePatchName,
                IsSpecifiedTarget = IsSpecifiedTarget,
                TargetIdList = TargetIdList,
                NodeType = NodeType,
                CustomNodeTypes = NodeTypeCatalog.CustomOptions
                    .Select(item => new NodeTypeDefinitionSettings(item.Value, item.Name))
                    .ToArray(),
                NodeTargetsText = NodeTargetsText,
                GatewayId = GatewayId,
                GatewayIdHistory = GatewayIdHistory.ToArray(),
                CycleRounds = CycleRounds,
                CycleIntervalMode = CycleIntervalMode,
                CycleFixedIntervalSeconds = CycleFixedIntervalSeconds,
                CycleRandomMinimumSeconds = CycleRandomMinimumSeconds,
                CycleRandomMaximumSeconds = CycleRandomMaximumSeconds,
                NodePatchLimit = NodePatchLimit,
                AsyncPatchLimit = AsyncPatchLimit,
                SyncPatchLimit = SyncPatchLimit,
                GatewayPatchLimit = GatewayPatchLimit,
                DiscoveryFreshnessMinutes = DiscoveryFreshnessMinutes,
                MinimumNodeRssi = MinimumNodeRssi,
                SelectedUpgradePatchPath = SelectedUpgradePatch?.FilePath ?? string.Empty,
                SelectedReverseUpgradePatchPath = SelectedReverseUpgradePatch?.FilePath ?? string.Empty,
                DiscoveredExtenders = DiscoveredExtenders
                    .Select(extender => new DiscoveredExtenderSettings(
                        extender.ExtenderId,
                        extender.Detail,
                        extender.DeviceType,
                        extender.SoftwareVersion,
                        extender.IsSelected,
                        extender.AsyncSoftwareVersion,
                        extender.AsyncAddress,
                        extender.SyncRssi,
                        extender.SyncSnr,
                        extender.OnlineCount,
                        extender.TotalCount))
                    .ToArray(),
                DiscoveredNodeGroups = DiscoveredNodeGroups
                    .Select(group => new DiscoveredNodeGroupSettings(
                        group.ExtenderId,
                        group.Nodes.Select(node => new DiscoveredNodeSettings(
                            node.NodeId,
                            node.NodeType,
                            node.SoftwareVersion,
                            node.Rssi,
                            node.IsSelected)).ToArray(),
                        group.Error,
                        group.ReportedCount))
                    .ToArray(),
                NodeDiscoveryCompletedAt = _nodeDiscoveryCompletedAt,
            };
            await _settingsStore.SaveAsync(settings, cancellationToken);
            SaveCurrentModeSecrets();
            SettingsStatus = "当前模式设置已保存到独立工作区；密码与私钥口令仅保存在 Windows Credential Manager。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SettingsStatus = $"保存设置失败：{exception.Message}";
        }
        finally
        {
            _settingsSaveLock.Release();
        }
    }

    private static string GetHttpRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtaTool", "http-root");

    private static string GetDefaultLogAnalyzerPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "analyze_ota_logs.py");
    }

    private static IReadOnlyList<LogAnalysisLineViewItem> BuildLogAnalysisResultLines(string text)
    {
        var inAnalyzerSummary = false;
        return text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line =>
            {
                var normalized = line.Trim().TrimStart('•').Trim();
                if (normalized == "日志分析摘要") inAnalyzerSummary = true;
                var isHeader = normalized is "评分明细" or "主要观察" or "改进建议" or "日志分析摘要";
                return new LogAnalysisLineViewItem(
                    line,
                    !isHeader && IsLogAnalysisProblemLine(normalized, inAnalyzerSummary),
                    isHeader);
            })
            .ToArray();
    }

    private static bool IsLogAnalysisProblemLine(string line, bool inAnalyzerSummary)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        if (!inAnalyzerSummary)
        {
            if (line.StartsWith("闭环完整性", StringComparison.Ordinal)
                || line.StartsWith("目标完成度", StringComparison.Ordinal)
                || line.StartsWith("传输可靠性", StringComparison.Ordinal)
                || line.StartsWith("时延表现", StringComparison.Ordinal)
                || line.StartsWith("目标完成度：", StringComparison.Ordinal))
            {
                return HasIncompleteRatio(line);
            }
            if (line.StartsWith("维护响应时延 P95", StringComparison.Ordinal))
            {
                return TryGetValueAfter(line, "P95 为", out var p95) && p95 > 300;
            }
            return line.Contains("未形成完整闭环", StringComparison.Ordinal)
                   || line.Contains("发送失败", StringComparison.Ordinal)
                   || line.Contains("推断漏帧", StringComparison.Ordinal)
                   || line.Contains("重试/重复", StringComparison.Ordinal)
                   || line.Contains("弱链路", StringComparison.Ordinal)
                   || line.StartsWith("先解决", StringComparison.Ordinal)
                   || line.StartsWith("检查 Sync", StringComparison.Ordinal)
                   || line.StartsWith("重点分析", StringComparison.Ordinal)
                   || line.StartsWith("复测弱链路", StringComparison.Ordinal);
        }

        if (line.StartsWith("OTA 日志判定：", StringComparison.Ordinal)) return !line.EndsWith("成功", StringComparison.Ordinal);
        if (line.StartsWith("计数：", StringComparison.Ordinal)) return HasIncompleteRatio(line);
        if (line.StartsWith("设备升级：", StringComparison.Ordinal))
            return line.Contains("未确认", StringComparison.Ordinal) || line.Contains("未完成", StringComparison.Ordinal);
        if (line.StartsWith("Stage：", StringComparison.Ordinal)) return HasPositiveValueAfter(line, "首轮缺");
        if (line.StartsWith("维护：", StringComparison.Ordinal))
        {
            var latencyMatch = System.Text.RegularExpressions.Regex.Match(
                line,
                @"P50/P95/MAX=[^/]+/(?<p95>\d+)/",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var latencyTooHigh = latencyMatch.Success
                                 && int.TryParse(latencyMatch.Groups["p95"].Value, out var p95)
                                 && p95 > 300;
            return line.Contains("None", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("null", StringComparison.OrdinalIgnoreCase)
                   || latencyTooHigh
                   || HasPositiveValueAfter(line, "重复");
        }
        if (line.StartsWith("分片诊断：", StringComparison.Ordinal))
            return HasAnyPositiveValueAfter(line, ["首片缺失", "尾片缺失", "两片全缺", "CRC 失败", "越窗前", "越窗后", "非法", "重复"]);
        if (line.StartsWith("接收路径首检：", StringComparison.Ordinal))
            return HasAnyPositiveValueAfter(line, ["底层无效", "帧头非法", "DATA 拒绝", "识别后未分发"]);
        if (line.StartsWith("Async 分片：", StringComparison.Ordinal))
            return HasTrailingFailureInTriplet(line, "首片提交/成功/失败")
                   || HasTrailingFailureInTriplet(line, "尾片")
                   || HasPositiveValueAfter(line, "入帧失败");
        if (line.StartsWith("逐帧对齐：", StringComparison.Ordinal))
            return HasFailureInSuccessFailurePair(line, "Async 成功/失败")
                   || HasAnyPositiveValueAfter(line, ["拒", "缺"]);
        if (line.StartsWith("发送保护：", StringComparison.Ordinal))
            return HasAnyPositiveValueAfter(line, ["双遍窗口", "额外块"]);
        if (line.StartsWith("同步节拍：", StringComparison.Ordinal))
            return HasAnyPositiveValueAfter(line, ["发送失败", "提交节拍异常", "帧头拒绝", "推断漏帧"]);
        if (line.StartsWith("分 Node：", StringComparison.Ordinal))
            return HasAnyPositiveValueAfter(line, ["缺", "瞬态", "SYNC_LOST"]);
        return line.StartsWith("弱链路提示：", StringComparison.Ordinal)
               || line.StartsWith("阻断原因：", StringComparison.Ordinal)
               || line.StartsWith("日志分析失败：", StringComparison.Ordinal);
    }

    private static bool HasIncompleteRatio(string line)
        => System.Text.RegularExpressions.Regex.Matches(line, @"(?<value>\d+)\s*/\s*(?<total>\d+)")
            .Cast<System.Text.RegularExpressions.Match>()
            .Any(match => int.Parse(match.Groups["value"].Value) < int.Parse(match.Groups["total"].Value));

    private static bool HasAnyPositiveValueAfter(string line, IReadOnlyList<string> labels)
        => labels.Any(label => HasPositiveValueAfter(line, label));

    private static bool HasPositiveValueAfter(string line, string label)
        => TryGetValueAfter(line, label, out var value) && value > 0;

    private static bool TryGetValueAfter(string line, string label, out int value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            $@"{System.Text.RegularExpressions.Regex.Escape(label)}\s*[=:]?\s*(?<value>\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return int.TryParse(match.Groups["value"].Value, out value);
    }

    private static bool HasTrailingFailureInTriplet(string line, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            $@"{System.Text.RegularExpressions.Regex.Escape(label)}\s*\d+/\d+/(?<failure>\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return int.TryParse(match.Groups["failure"].Value, out var failure) && failure > 0;
    }

    private static bool HasFailureInSuccessFailurePair(string line, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            $@"{System.Text.RegularExpressions.Regex.Escape(label)}\s*\d+/(?<failure>\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return int.TryParse(match.Groups["failure"].Value, out var failure) && failure > 0;
    }

    private string GetPatchOutputDirectory()
    {
        return string.IsNullOrWhiteSpace(PatchOutputDirectory)
            ? GetHttpRoot()
            : Path.GetFullPath(PatchOutputDirectory.Trim());
    }

    private async Task LoadPatchCatalogFromOutputDirectoryAsync()
    {
        try
        {
            var root = GetPatchOutputDirectory();
            if (!Directory.Exists(root))
            {
                RefreshUpgradePatchChoices();
                OnPropertyChanged(nameof(PatchCatalog));
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsUpgradeFile)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var metadata = await PatchMetadata.FromFileAsync(filePath);
                var isFullImage = string.Equals(Path.GetExtension(filePath), ".bin", StringComparison.OrdinalIgnoreCase);
                var manifestVerified = isFullImage;
                DeviceType? patchDeviceType = isFullImage ? DeviceType.Gateway : null;
                byte? patchOldVersion = null;
                byte? patchNewVersion = null;
                if (isFullImage)
                {
                    try
                    {
                        var identity = await FirmwareIdentityReader.ReadAsync(filePath);
                        if (identity.DeviceType != FirmwareDeviceType.Gateway) continue;
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }
                }
                try
                {
                    if (!isFullImage)
                    {
                        var manifest = await PackageManifestImporter.LoadAndValidateAsync(filePath);
                        manifestVerified = true;
                        patchDeviceType = manifest.OtaDeviceType;
                        patchOldVersion = manifest.OldVersion;
                        patchNewVersion = manifest.NewVersion;
                    }
                }
                catch
                {
                    // 裸 Patch 或元数据不完整时仍应出现在详情列表中，但不能直接用于升级或发布。
                }
                RegisterPatch(
                    isFullImage ? "目录完整镜像" : "目录 Patch",
                    filePath,
                    metadata.Length,
                    metadata.Md5,
                    metadata.Sha256,
                    isSelectedForPublish: false,
                    manifestVerified: manifestVerified,
                    isFullImage: isFullImage,
                    otaDeviceType: patchDeviceType,
                    oldVersion: patchOldVersion,
                    newVersion: patchNewVersion);
            }

            RefreshUpgradePatchChoices();
            OnPropertyChanged(nameof(PatchCatalog));
            OnPropertyChanged(nameof(PatchRestoreChoices));
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"读取 Patch 输出目录失败：{exception.Message}";
        }
    }

    private void DeletePatch(object? parameter)
    {
        if (parameter is not PatchSelection patch) return;
        _pendingDeletion = patch;
        OpenPatchDialog(
            PatchDialogAction.Delete,
            "删除 Patch",
            $"确认从 Patch 输出目录删除以下文件吗？\n\n{patch.FileName}\n\n此操作不可恢复。",
            "确认删除");
    }

    private void DeletePatchFile(PatchSelection patch)
    {
        try
        {
            File.Delete(patch.FilePath);
            File.Delete(patch.FilePath + ".json");
            _patchCatalog.Remove(patch.FilePath);
            if (string.Equals(_patchPath, patch.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _patchPath = string.Empty;
                _patchMd5 = string.Empty;
                _patchSha256 = string.Empty;
                _patchLength = 0;
                PatchUrl = string.Empty;
            }
            if (string.Equals(_reversePatchPath, patch.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _reversePatchPath = string.Empty;
                _reversePatchMd5 = string.Empty;
                _reversePatchSha256 = string.Empty;
                _reversePatchLength = 0;
                _reversePatchUrl = string.Empty;
            }
            if (string.Equals(_importedPatchPath, patch.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _importedPatchPath = string.Empty;
                _importedPatchMd5 = string.Empty;
                _importedPatchSha256 = string.Empty;
                _importedPatchLength = 0;
            }

            OnPropertyChanged(nameof(PatchFileName));
            OnPropertyChanged(nameof(PatchMetadataDetail));
            OnPropertyChanged(nameof(ReversePatchFileName));
            OnPropertyChanged(nameof(ReversePatchMetadataDetail));
            OnPropertyChanged(nameof(ImportedPatchFileName));
            OnPropertyChanged(nameof(ImportedPatchMetadataDetail));
            OnPropertyChanged(nameof(PatchCatalog));
            OnPropertyChanged(nameof(PatchRestoreChoices));
            RefreshUpgradePatchChoices();
            TaskStatusMessage = $"已删除 Patch：{patch.FileName}";
        }
        catch (Exception exception)
        {
            TaskStatusMessage = $"删除 Patch 失败：{exception.Message}";
        }
    }

    private void OpenPatchDialog(
        PatchDialogAction action,
        string title,
        string message,
        string confirmText,
        string resultStampText = "",
        string resultStampColor = "#159E68")
    {
        _patchDialogAction = action;
        OnPropertyChanged(nameof(PatchDialogVisibility));
        OnPropertyChanged(nameof(GlobalDialogVisibility));
        OnPropertyChanged(nameof(DialogCancelVisibility));
        PatchDialogTitle = title;
        PatchDialogMessage = message;
        PatchDialogConfirmText = confirmText;
        DialogResultStampText = resultStampText;
        DialogResultStampColor = resultStampColor;
        OnPropertyChanged(nameof(DialogResultStampVisibility));
        IsPatchDialogOpen = true;
    }

    public void ShowInformationDialog(string title, string message)
    {
        OpenPatchDialog(PatchDialogAction.Information, title, message, "确定");
    }

    private void ClosePatchDialog()
    {
        IsPatchDialogOpen = false;
        _patchDialogAction = PatchDialogAction.None;
        _pendingPublication = [];
        _pendingDeletion = null;
        _pendingReportDeletion = null;
        _pendingUpgradeTask = null;
        _pendingUpgradeProfile = null;
        _pendingCycleForwardTask = null;
        _pendingCycleReverseTask = null;
        _pendingCycleProfile = null;
        _pendingCycleInterval = null;
        _pendingCycleRounds = 0;
        DialogResultStampText = string.Empty;
        OnPropertyChanged(nameof(DialogResultStampVisibility));
        NotifyUpgradeActionAvailability();
    }

    private void CancelPatchDialog()
    {
        var wasPublishing = _patchDialogAction == PatchDialogAction.Publish;
        var wasStartingUpgrade = _patchDialogAction == PatchDialogAction.StartUpgrade;
        var wasStartingCycle = _patchDialogAction == PatchDialogAction.StartCycleUpgrade;
        ClosePatchDialog();
        if (wasPublishing)
        {
            PublishStatus = "已取消 Patch 发布。";
            TaskStatusMessage = "已取消 Patch 发布，可调整 Patch 详情列表后重新发布。";
        }
        else if (wasStartingUpgrade)
        {
            TaskStatusMessage = "已取消启动升级，任务尚未发送。";
        }
        else if (wasStartingCycle)
        {
            TaskStatusMessage = "已取消启动循环升级，任务尚未发送。";
        }
    }

    private async Task ConfirmPatchDialogAsync()
    {
        var action = _patchDialogAction;
        var publication = _pendingPublication;
        var deletion = _pendingDeletion;
        var reportDeletion = _pendingReportDeletion;
        var upgradeTask = _pendingUpgradeTask;
        var upgradeProfile = _pendingUpgradeProfile;
        var cycleForwardTask = _pendingCycleForwardTask;
        var cycleReverseTask = _pendingCycleReverseTask;
        var cycleProfile = _pendingCycleProfile;
        var cycleInterval = _pendingCycleInterval;
        var cycleRounds = _pendingCycleRounds;
        if (action is PatchDialogAction.StartUpgrade or PatchDialogAction.StartCycleUpgrade)
        {
            _isUpgradeStartInProgress = true;
            NotifyUpgradeActionAvailability();
        }
        ClosePatchDialog();

        if (action == PatchDialogAction.Delete && deletion is not null)
        {
            DeletePatchFile(deletion);
            return;
        }

        if (action == PatchDialogAction.DeleteReport && reportDeletion is not null)
        {
            await DeleteReportAsync(reportDeletion);
            return;
        }

        if (action == PatchDialogAction.Information)
        {
            return;
        }

        if (action == PatchDialogAction.CancelTask)
        {
            await CancelActiveTaskAsync();
            return;
        }

        if (action == PatchDialogAction.CloseApplication)
        {
            CloseApplicationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (action == PatchDialogAction.Publish && publication.Count > 0)
        {
            await PublishPatchesAsync(publication);
        }

        if (action == PatchDialogAction.StartUpgrade && upgradeTask is not null && upgradeProfile is not null)
        {
            await VerifyAndStartValidatedTaskAsync(upgradeTask, upgradeProfile);
        }

        if (action == PatchDialogAction.StartCycleUpgrade &&
            cycleForwardTask is not null &&
            cycleReverseTask is not null &&
            cycleProfile is not null &&
            cycleInterval is not null)
        {
            await VerifyAndStartCycleAsync(
                cycleForwardTask,
                cycleReverseTask,
                cycleProfile,
                cycleInterval,
                cycleRounds);
        }
    }

    private async Task<string> CopyPatchToOutputDirectoryAsync(PatchMetadata source)
    {
        var root = GetPatchOutputDirectory();
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, Path.GetFileName(source.FilePath));
        if (File.Exists(destination))
        {
            var existing = await PatchMetadata.FromFileAsync(destination);
            if (string.Equals(existing.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(source.FilePath + ".json"))
                {
                    File.Copy(source.FilePath + ".json", destination + ".json", overwrite: true);
                }
                return destination;
            }

            var name = Path.GetFileNameWithoutExtension(source.FilePath);
            var extension = Path.GetExtension(source.FilePath);
            destination = Path.Combine(root, $"{name}-{source.Sha256[..8]}{extension}");
        }

        File.Copy(source.FilePath, destination, overwrite: true);
        if (File.Exists(source.FilePath + ".json"))
        {
            File.Copy(source.FilePath + ".json", destination + ".json", overwrite: true);
        }
        return destination;
    }

    private void RegisterPatch(
        string source,
        string filePath,
        long length,
        string md5,
        string sha256,
        bool isSelectedForPublish = true,
        bool manifestVerified = false,
        bool isFullImage = false,
        DeviceType? otaDeviceType = null,
        byte? oldVersion = null,
        byte? newVersion = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        if (_patchCatalog.TryGetValue(filePath, out var existing) &&
            string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            isSelectedForPublish = existing.IsSelectedForPublish;
        }
        _patchCatalog[filePath] = new PatchSelection(
            source,
            filePath,
            length,
            md5,
            sha256,
            isSelectedForPublish,
            manifestVerified,
            isFullImage,
            otaDeviceType,
            oldVersion,
            newVersion);
        OnPropertyChanged(nameof(PatchCatalog));
        OnPropertyChanged(nameof(PatchRestoreChoices));
        OnPropertyChanged(nameof(CanTestSelectedPatchRestore));
    }

    private static bool IsUpgradeFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".patch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildPublishedPatchKey(PatchSelection patch)
    {
        return string.Join("|", SftpHost.Trim(), SftpPort, SftpRemoteDirectory.Trim(), PublicHttpBaseUrl.Trim(), patch.Sha256);
    }

    private void RefreshUpgradePatchChoices()
    {
        var previousPath = SelectedUpgradePatch?.FilePath;
        var previousReversePath = SelectedReverseUpgradePatch?.FilePath;
        var previousRestorePath = SelectedRestorePatch?.FilePath;
        var selectedDeviceType = GetSelectedTaskDeviceType();
        UpgradePatchChoices.Clear();
        foreach (var patch in _patchCatalog.Values
                     .Where(item => IsInCurrentPatchWorkspace(item.FilePath) &&
                                    File.Exists(item.FilePath) &&
                                    (item.IsFullImage
                                        ? selectedDeviceType == DeviceType.Gateway
                                        : (!IsEcoLink || item.ManifestVerified) &&
                                          (item.OtaDeviceType == selectedDeviceType || (!IsEcoLink && item.OtaDeviceType is null))))
                     .OrderBy(item => item.Source)
                     .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            UpgradePatchChoices.Add(patch);
        }
        var previousForwardPatch = UpgradePatchChoices.FirstOrDefault(item =>
            string.Equals(item.FilePath, previousPath, StringComparison.OrdinalIgnoreCase));
        SelectedUpgradePatch = (previousForwardPatch is not null && MatchesPatchDirection(previousForwardPatch, reverse: false)
                ? previousForwardPatch
                : null)
            ?? UpgradePatchChoices.FirstOrDefault(item => MatchesPatchDirection(item, reverse: false))
            ?? UpgradePatchChoices.FirstOrDefault(item => item.Source.StartsWith("正向 Patch", StringComparison.Ordinal))
            ?? previousForwardPatch
            ?? UpgradePatchChoices.FirstOrDefault();
        var restoreChoices = PatchRestoreChoices.Where(item => File.Exists(item.FilePath)).ToArray();
        SelectedRestorePatch = restoreChoices.FirstOrDefault(item => string.Equals(item.FilePath, previousRestorePath, StringComparison.OrdinalIgnoreCase))
            ?? restoreChoices.FirstOrDefault();
        var previousReversePatch = ReverseUpgradePatchChoices.FirstOrDefault(item =>
            string.Equals(item.FilePath, previousReversePath, StringComparison.OrdinalIgnoreCase));
        SelectedReverseUpgradePatch = (previousReversePatch is not null && MatchesPatchDirection(previousReversePatch, reverse: true)
                ? previousReversePatch
                : null)
            ?? ReverseUpgradePatchChoices.FirstOrDefault(item => MatchesPatchDirection(item, reverse: true))
            ?? ReverseUpgradePatchChoices.FirstOrDefault(item => item.Source.StartsWith("反向 Patch", StringComparison.Ordinal))
            ?? previousReversePatch;
        OnPropertyChanged(nameof(ReverseUpgradePatchChoices));
        OnPropertyChanged(nameof(SelectedUpgradePatchSummary));
        if (SelectedUpgradePatch is null)
        {
            TaskStatusMessage = $"当前没有适用于“{SelectedTaskType}”的 Patch，请先制作或导入对应类型的 Patch。";
        }
    }

    private DeviceType GetSelectedTaskDeviceType() => SelectedTaskType switch
    {
        GatewayTaskType => DeviceType.Gateway,
        SyncTaskType => DeviceType.Sync,
        AsyncTaskType => DeviceType.Async,
        NodeTaskType => DeviceType.Node,
        _ => throw new InvalidOperationException($"未知升级类型：{SelectedTaskType}"),
    };

    private bool MatchesPatchDirection(PatchSelection patch, bool reverse)
    {
        if (!byte.TryParse(OldVersion, out var oldVersion) ||
            !byte.TryParse(NewVersion, out var newVersion) ||
            patch.OldVersion is null ||
            patch.NewVersion is null)
        {
            return false;
        }

        return reverse
            ? patch.OldVersion == newVersion && patch.NewVersion == oldVersion
            : patch.OldVersion == oldVersion && patch.NewVersion == newVersion;
    }

    private bool IsPatchConfiguredForDirection(PatchSelection? patch, bool reverse)
    {
        if (patch is null || string.IsNullOrWhiteSpace(patch.FilePath) || !File.Exists(patch.FilePath))
        {
            return false;
        }

        var deviceType = GetSelectedTaskDeviceType();
        if (patch.IsFullImage)
        {
            return deviceType == DeviceType.Gateway;
        }
        if (!IsEcoLink)
        {
            return true;
        }
        return patch.ManifestVerified &&
               patch.OtaDeviceType == deviceType &&
               MatchesPatchDirection(patch, reverse);
    }

    private bool IsSelectedTargetAtDirectionStartVersion(bool reverse)
    {
        if (!IsEcoLink ||
            !byte.TryParse(reverse ? NewVersion : OldVersion, out var expectedVersion))
        {
            return !IsEcoLink;
        }

        var deviceType = GetSelectedTaskDeviceType();
        if (deviceType == DeviceType.Gateway)
        {
            return _gatewaySoftwareVersion == expectedVersion &&
                   string.Equals(_gatewayVersionGatewayId, GatewayId, StringComparison.Ordinal);
        }
        if (deviceType is DeviceType.Sync or DeviceType.Async)
        {
            var targetIds = ParsePositiveUIntLines(TargetIdList).ToHashSet();
            if (targetIds.Count == 0)
            {
                return !IsSpecifiedTarget;
            }
            var targets = DiscoveredExtenders
                .Where(item => targetIds.Contains(item.ExtenderId))
                .ToArray();
            if (targets.Length != targetIds.Count)
            {
                return false;
            }
            return targets.All(item =>
                item.GetSoftwareVersion(deviceType) == expectedVersion);
        }

        IReadOnlyList<OtaExtenderTarget> nodeTargets;
        try
        {
            nodeTargets = ParseNodeTargets(NodeTargetsText);
        }
        catch
        {
            return false;
        }
        var targetKeys = nodeTargets
            .SelectMany(target => target.NodeIds
                .Where(nodeId => ushort.TryParse(nodeId, out _))
                .Select(nodeId => (ExtenderId: uint.Parse(target.ExtenderId), NodeId: ushort.Parse(nodeId))))
            .ToHashSet();
        if (targetKeys.Count == 0)
        {
            return false;
        }
        var selectedNodes = DiscoveredNodeGroups
            .SelectMany(group => group.Nodes.Select(node => (group.ExtenderId, Node: node)))
            .Where(item => targetKeys.Contains((item.ExtenderId, item.Node.NodeId)))
            .Select(item => item.Node)
            .ToArray();
        return selectedNodes.Length == targetKeys.Count &&
               selectedNodes.All(node => node.SoftwareVersion == expectedVersion);
    }

    private void ApplySuccessfulUpgradeVersion(OtaTask task)
    {
        if (!byte.TryParse(task.NewVersion, out var newVersion))
        {
            return;
        }

        if (task.DeviceType == DeviceType.Gateway)
        {
            _gatewaySoftwareVersion = newVersion;
            _gatewayVersionGatewayId = task.GatewayId;
            OnPropertyChanged(nameof(GatewayIdTaskHint));
        }
        else if (task.DeviceType is DeviceType.Sync or DeviceType.Async)
        {
            var targetIds = task.Target.DeviceIds
                .Select(value => uint.TryParse(value, out var id) ? id : 0U)
                .Where(id => id > 0U)
                .ToHashSet();
            foreach (var extender in DiscoveredExtenders.Where(item =>
                         task.Target.Scope == TargetScope.Broadcast || targetIds.Contains(item.ExtenderId)))
            {
                extender.ApplySoftwareVersion(task.DeviceType, newVersion);
            }
        }
        else if (task.DeviceType == DeviceType.Node)
        {
            var targetKeys = task.ExtenderTargets
                .SelectMany(target => target.NodeIds
                    .Where(nodeId => ushort.TryParse(nodeId, out _))
                    .Select(nodeId => (ExtenderId: uint.Parse(target.ExtenderId), NodeId: ushort.Parse(nodeId))))
                .ToHashSet();
            foreach (var item in DiscoveredNodeGroups
                         .SelectMany(group => group.Nodes.Select(node => (group.ExtenderId, Node: node)))
                         .Where(item => targetKeys.Contains((item.ExtenderId, item.Node.NodeId))))
            {
                item.Node.ApplySoftwareVersion(newVersion);
            }
        }

        NotifyUpgradeActionAvailability();
        ScheduleSettingsAutoSave();
    }

    private string? ValidateGatewayVersionBeforeUpgrade(OtaTask task)
    {
        if (task.Mode != OtaMode.EcoLink || task.DeviceType != DeviceType.Gateway)
        {
            return null;
        }
        if (!byte.TryParse(task.OldVersion, out var expectedVersion))
        {
            return "Patch 旧版本不是 1～254 的有效软件版本。";
        }
        if (!_gatewaySoftwareVersion.HasValue ||
            !string.Equals(_gatewayVersionGatewayId, task.GatewayId, StringComparison.Ordinal))
        {
            return "请先点击“刷新 Gateway”查询当前软件版本。";
        }
        return _gatewaySoftwareVersion.Value == expectedVersion
            ? null
            : $"Gateway 当前版本 {ProtocolVersionFormatter.FormatWithPrefix(_gatewaySoftwareVersion.Value)}，所选 Patch 要求旧版本 {ProtocolVersionFormatter.FormatWithPrefix(expectedVersion)}。";
    }

    private bool IsInCurrentPatchWorkspace(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return string.Equals(
            fileDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            GetPatchOutputDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string InferPatchRestoreDirection(PatchSelection patch)
    {
        if (patch.Source.Contains("反向", StringComparison.Ordinal)
            || patch.FileName.Contains("b-to-a", StringComparison.OrdinalIgnoreCase))
        {
            return "B → A";
        }

        return "A → B";
    }

    private string GetPatchDownloadUrl(string patchPath)
    {
        return IsHttpServiceRunning ? GetLocalPatchUrl(patchPath) : GetPublicPatchUrl(patchPath);
    }

    private static string NormalizePatchFileName(string patchName)
    {
        var name = string.IsNullOrWhiteSpace(patchName) ? "patch" : patchName.Trim();
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidCharacter, '-');
        }
        return Path.HasExtension(name) ? name : name + ".patch";
    }

    private string GetLocalPatchUrl(string patchPath)
    {
        if (string.IsNullOrWhiteSpace(patchPath)) return string.Empty;
        return new Uri(new Uri(HttpServiceAddress, UriKind.Absolute), Uri.EscapeDataString(Path.GetFileName(patchPath))).ToString();
    }

    private string GetPublicPatchUrl(string? patchPath = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(PublicHttpBaseUrl) ? SftpPublicBaseUrl : PublicHttpBaseUrl;
        var sourcePath = patchPath ?? _patchPath;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(sourcePath) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return string.Empty;
        return new Uri(new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute), Uri.EscapeDataString(Path.GetFileName(sourcePath))).ToString();
    }

    private bool IsSelectedPage(string pageName) => SelectedPage?.Name == pageName;

    private string? ValidateDiscoveredNodeTypes(IReadOnlyList<OtaExtenderTarget> targets, int expectedNodeType)
    {
        var mismatches = new List<string>();
        foreach (var target in targets)
        {
            if (!uint.TryParse(target.ExtenderId, out var extenderId)) continue;

            var discoveredGroup = DiscoveredNodeGroups.FirstOrDefault(group => group.ExtenderId == extenderId);
            if (discoveredGroup is null) continue;

            var targetNodeIds = target.NodeIds
                .Select(nodeId => ushort.TryParse(nodeId, out var parsed) ? parsed : (ushort)0)
                .Where(nodeId => nodeId > 0)
                .ToHashSet();
            foreach (var node in discoveredGroup.Nodes.Where(node => targetNodeIds.Contains(node.NodeId) && node.NodeType != expectedNodeType))
            {
                mismatches.Add($"Extender {extenderId} / Node {node.NodeId} 上报类型 {NodeTypeCatalog.Format(node.NodeType)}");
            }
        }

        if (mismatches.Count == 0) return null;

        var preview = string.Join("；", mismatches.Take(3));
        var remainder = mismatches.Count > 3 ? $"；另有 {mismatches.Count - 3} 个" : string.Empty;
        return $"Node 类型 {NodeTypeCatalog.Format(expectedNodeType)} 与已发现目标不一致：{preview}{remainder}。请按刷新 Node 后显示的实际类型选择。";
    }

    private string? ValidateUpgradePreflight(
        DeviceType deviceType,
        IReadOnlyList<string> deviceIds,
        IReadOnlyList<OtaExtenderTarget> extenderTargets,
        PatchMetadata patch,
        PackageManifest manifest,
        string expectedOldVersion,
        string expectedNewVersion)
    {
        if (manifest.OtaDeviceType != deviceType)
        {
            return $"Patch 类型 {manifest.OtaDeviceType} 与当前升级类型 {deviceType} 不一致。";
        }
        if (!byte.TryParse(expectedOldVersion, out var oldVersion) ||
            !byte.TryParse(expectedNewVersion, out var newVersion) ||
            oldVersion != manifest.OldVersion ||
            newVersion != manifest.NewVersion)
        {
            return $"当前版本 {ProtocolVersionFormatter.FormatRaw(expectedOldVersion)} → {ProtocolVersionFormatter.FormatRaw(expectedNewVersion)} 与 Patch 元数据 {ProtocolVersionFormatter.Format(manifest.OldVersion)} → {ProtocolVersionFormatter.Format(manifest.NewVersion)} 不一致。";
        }
        var capacity = PatchCapacityPolicy.Check(deviceType, patch.Length, GetPatchCapacityLimits());
        if (!capacity.IsAllowed)
        {
            return capacity.Message;
        }
        if (deviceType is DeviceType.Sync or DeviceType.Async)
        {
            // 同步、异步 MCU 位于同一块 Extender 板上，发现接口上报的承载板类型统一为 ExtenderS（2）。
            var expectedType = (byte)FirmwareDeviceType.ExtenderS;
            var requestedIds = deviceIds
                .Select(value => uint.TryParse(value, out var id) ? id : 0U)
                .Where(id => id > 0U)
                .ToHashSet();
            var selectedExtenders = DiscoveredExtenders
                .Where(item => requestedIds.Contains(item.ExtenderId))
                .ToArray();
            if (selectedExtenders.Length != requestedIds.Count)
            {
                return "部分 Extender 目标不在最近一次发现结果中，请刷新后重新选择。";
            }
            var invalidExtender = selectedExtenders.FirstOrDefault(item =>
                item.DeviceType != expectedType ||
                item.GetSoftwareVersion(deviceType) != manifest.OldVersion);
            if (invalidExtender is not null)
            {
                var actualVersion = invalidExtender.GetSoftwareVersion(deviceType);
                var versionName = deviceType == DeviceType.Async ? "异步版本" : "同步版本";
                return $"Extender {invalidExtender.ExtenderId} 的类型或底版本不匹配：" +
                       $"设备类型 {invalidExtender.DeviceType}、{versionName} " +
                       $"{(actualVersion.HasValue ? ProtocolVersionFormatter.FormatWithPrefix(actualVersion.Value) : "未查询到")}，" +
                       $"Patch 要求类型 {expectedType}、{versionName} {ProtocolVersionFormatter.FormatWithPrefix(manifest.OldVersion)}。";
            }
            return null;
        }
        if (deviceType != DeviceType.Node)
        {
            return null;
        }
        if (manifest.DeviceTypeCode != NodeType)
        {
            return $"Patch Node 类型 {NodeTypeCatalog.Format(manifest.DeviceTypeCode)} 与当前选择 {NodeTypeCatalog.Format(NodeType)} 不一致。";
        }
        var selectedKeys = extenderTargets
            .SelectMany(target => target.NodeIds
                .Where(value => ushort.TryParse(value, out _))
                .Select(value => (ExtenderId: uint.Parse(target.ExtenderId), NodeId: ushort.Parse(value))))
            .ToHashSet();
        var selectedNodes = DiscoveredNodeGroups
            .SelectMany(group => group.Nodes.Select(node => (group.ExtenderId, Node: node)))
            .Where(item => selectedKeys.Contains((item.ExtenderId, item.Node.NodeId)))
            .Select(item => item.Node)
            .ToArray();
        if (selectedNodes.Length != selectedKeys.Count)
        {
            return "部分目标不在最近一次 Node 发现结果中，请重新刷新并按类型选择。";
        }
        var invalid = selectedNodes.FirstOrDefault(node =>
            node.NodeType != manifest.DeviceTypeCode ||
            node.SoftwareVersion != manifest.OldVersion);
        if (invalid is not null)
        {
            return $"Node {invalid.NodeId} 不满足升级条件：类型 {NodeTypeCatalog.Format(invalid.NodeType)}、版本 {ProtocolVersionFormatter.FormatWithPrefix(invalid.SoftwareVersion)}、RSSI {invalid.Rssi} dBm。";
        }
        return null;
    }

    private async Task AddNodeTypeAsync()
    {
        var name = NewNodeTypeName.Trim();
        if (!int.TryParse(NewNodeTypeValue, out var value) || value is < 2 or > 63)
        {
            TaskStatusMessage = "Node 类型未添加：编号必须是 2～63 的十进制整数。";
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            TaskStatusMessage = "Node 类型未添加：请填写类型名称。";
            return;
        }
        if (NodeTypeCatalog.IsBuiltIn(value))
        {
            TaskStatusMessage = $"Node 类型未添加：{NodeTypeCatalog.Format(value)} 是内置类型。";
            return;
        }

        NodeTypeCatalog.AddOrUpdateCustom(value, name);
        RefreshNodeTypeOptions();
        NodeType = value;
        foreach (var node in DiscoveredNodeGroups.SelectMany(group => group.Nodes))
        {
            node.RefreshNodeTypeDisplay();
        }
        NewNodeTypeName = string.Empty;
        NewNodeTypeValue = string.Empty;
        await SaveSettingsAsync();
        TaskStatusMessage = $"已保存 Node 类型：{NodeTypeCatalog.Format(value)}。";
    }

    private void RefreshNodeTypeOptions()
    {
        var previousType = _selectedNodeTypeValue;
        NodeTypeOptions.Clear();
        NodeTypeOptions.Add(new(0, "不选择"));
        var discoveredTypes = DiscoveredNodeGroups
            .SelectMany(group => group.Nodes)
            .Select(node => (int)node.NodeType)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var options = discoveredTypes
            .Select(value => new NodeTypeOption(value, NodeTypeCatalog.Format(value).Split('（')[0]))
            .ToArray();
        foreach (var option in options)
        {
            NodeTypeOptions.Add(option);
        }
        if (previousType != 0 && NodeTypeOptions.All(option => option.Value != previousType))
        {
            var firstDiscoveredType = NodeTypeOptions.FirstOrDefault(option => option.Value != 0);
            if (firstDiscoveredType is not null)
            {
                NodeType = firstDiscoveredType.Value;
            }
        }
        OnPropertyChanged(nameof(SelectedNodeTypeOption));
    }

    private static IReadOnlyList<OtaExtenderTarget> ParseNodeTargets(string text)
    {
        var targets = new List<OtaExtenderTarget>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (segments.Length != 2)
            {
                throw new InvalidOperationException("Node 目标格式应为“ExtenderID: NodeID,NodeID”。");
            }
            var nodes = segments[1].Split([',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            targets.Add(new OtaExtenderTarget(segments[0], nodes));
        }
        return targets;
    }

    private void OnMqttMessagePublished(object? sender, MqttApplicationMessage message) => AddMqttMessage("TX", message);

    private void OnMqttMessageReceived(object? sender, MqttApplicationMessage message)
    {
        RunOnUi(() =>
        {
            var segments = message.Topic.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length >= 4 && segments[0] == "ucchip" && segments[1] == "up" && segments[2] == "sgw")
            {
                _observedGatewayIds.Add(segments[3]);
                OnPropertyChanged(nameof(GatewayOnlineStatus));
            }

        });
        AddMqttMessage("RX", message);
    }

    private void AddMqttMessage(string direction, MqttApplicationMessage message)
    {
        RunOnUi(() =>
        {
            var payload = FormatMqttPayload(message.Payload);
            MqttMessages.Add(new MqttMessageListItem(DateTimeOffset.Now.ToString("HH:mm:ss"), direction, message.Topic, payload.Content, payload.IsBinary));
            while (MqttMessages.Count > 500) MqttMessages.RemoveAt(0);
            OnPropertyChanged(nameof(VisibleMqttMessages));
        });
    }

    private void ClearMqttMessages()
    {
        MqttMessages.Clear();
        OnPropertyChanged(nameof(VisibleMqttMessages));
        TaskStatusMessage = "MQTT 收发记录已清理。";
    }

    private static (string Content, bool IsBinary) FormatMqttPayload(ReadOnlyMemory<byte> payload)
        => payload.IsEmpty
            ? ("（空载荷）", false)
            : (System.Text.Encoding.UTF8.GetString(payload.Span), false);

    private async Task<string> ApplySidecarManifestAsync(string sourcePatchPath)
    {
        var basePath = Path.GetFullPath(sourcePatchPath);
        var manifest = await PackageManifestImporter.LoadAndValidateAsync(basePath);
        ApplyManifestDetails(manifest, updateTaskType: true);
        return " · 已加载并校验强制 Package Manifest";
    }

    private void ApplyManifestDetails(PackageManifest manifest, bool updateTaskType)
    {
        _selectedPatchManifest = manifest;
        var deviceType = manifest.OtaDeviceType;
        if (updateTaskType)
        {
            SelectedTaskType = deviceType switch
            {
                DeviceType.Gateway => GatewayTaskType,
                DeviceType.Sync => SyncTaskType,
                DeviceType.Async => AsyncTaskType,
                DeviceType.Node => NodeTaskType,
                _ => SelectedTaskType,
            };
        }
        if (deviceType == DeviceType.Node)
        {
            NodeType = manifest.DeviceTypeCode;
        }
        OldVersion = manifest.OldVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NewVersion = manifest.NewVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _patchManifestVerified = true;
        RefreshNodeEligibility();
    }

    private void RefreshNodeEligibility()
    {
        var filterType = _selectedNodeTypeValue > 0
            ? _selectedNodeTypeValue
            : (int?)null;
        foreach (var group in DiscoveredNodeGroups)
        {
            group.SetFilter(NodeIdSearch, filterType);
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _ = dispatcher.InvokeAsync(action);
    }
}

public sealed record NavigationItem(string Index, string Name);

public enum PatchDialogAction
{
    None,
    Delete,
    DeleteReport,
    Information,
    Publish,
    StartUpgrade,
    StartCycleUpgrade,
    CancelTask,
    CloseApplication,
}

public sealed class PatchSelection : ObservableObject
{
    private bool _isSelectedForPublish;

    public PatchSelection(
        string source,
        string filePath,
        long length,
        string md5,
        string sha256,
        bool isSelectedForPublish,
        bool manifestVerified = false,
        bool isFullImage = false,
        DeviceType? otaDeviceType = null,
        byte? oldVersion = null,
        byte? newVersion = null)
    {
        Source = source;
        FilePath = filePath;
        Length = length;
        Md5 = md5;
        Sha256 = sha256;
        _isSelectedForPublish = isSelectedForPublish;
        ManifestVerified = manifestVerified;
        IsFullImage = isFullImage;
        OtaDeviceType = otaDeviceType;
        OldVersion = oldVersion;
        NewVersion = newVersion;
    }

    public string Source { get; }

    public string FilePath { get; }

    public long Length { get; }

    public string Md5 { get; }

    public string Sha256 { get; }

    public bool IsSelectedForPublish
    {
        get => _isSelectedForPublish;
        set => SetProperty(ref _isSelectedForPublish, value);
    }

    public bool ManifestVerified { get; }

    public bool IsFullImage { get; }

    public DeviceType? OtaDeviceType { get; }

    public byte? OldVersion { get; }

    public byte? NewVersion { get; }

    public string FileName => Path.GetFileName(FilePath);

    public string DisplayName => $"{Source}：{FileName}（{Length:N0} B）";

    public string PublishState => IsFullImage
        ? "Gateway 完整镜像（EcoLink / 传统模式）"
        : ManifestVerified
        ? "已通过还原验证，可用于升级和发布"
        : "待还原验证：暂不可用于升级和发布";

    public string ValidationColor => ManifestVerified ? "#159E68" : "#B87500";

    public string Md5Display => $"MD5：{Md5}";
}

public sealed class ReportListItem
{
    public ReportListItem(OtaReport report, string outputDirectory)
    {
        Report = report;
        HtmlPath = Path.Combine(outputDirectory, $"ota-report-{report.Id:N}.html");
        JsonPath = Path.Combine(outputDirectory, $"ota-report-{report.Id:N}.json");
        StageTimeline = BuildStageTimeline(report);
    }

    public OtaReport Report { get; }

    public Guid Id => Report.Id;

    public string StartedAt => Report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string Mode => Report.Task.Mode == OtaMode.EcoLink ? "EcoLink" : "传统模式";

    public string DeviceType => Report.Task.DeviceType switch
    {
        OtaTool.Core.Models.DeviceType.Gateway => "Gateway",
        OtaTool.Core.Models.DeviceType.Sync => "Sync",
        OtaTool.Core.Models.DeviceType.Async => "Async",
        OtaTool.Core.Models.DeviceType.Node => "Node",
        _ => Report.Task.DeviceType.ToString(),
    };

    public string State => OtaStatusDisplay.State(Report.FinalState.ToString());

    public string Version => $"{Report.Task.OldVersion} → {Report.Task.NewVersion}";

    public string Duration => Report.FinishedAt.HasValue
        ? $"{Math.Max(0, (Report.FinishedAt.Value - Report.StartedAt).TotalSeconds):N1} 秒"
        : "未结束";

    public string ArchiveButtonText => Report.IsArchived ? "恢复报告" : "归档报告";

    public string HtmlPath { get; }

    public string JsonPath { get; }

    public IReadOnlyList<ReportStageSummaryItem> StageTimeline { get; }

    public Visibility StageTimelineEmptyVisibility => StageTimeline.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static IReadOnlyList<ReportStageSummaryItem> BuildStageTimeline(OtaReport report)
    {
        var status = report.Timeline
            .LastOrDefault(update => update.GatewayStatus?.Stages.Count > 0)
            ?.GatewayStatus;
        if (status is null) return [];

        return status.Stages
            .Where(stage => OtaStagePolicy.IsApplicable(report.Task.DeviceType, stage.Stage))
            .Select(stage =>
            {
                var isCacheReuseTransfer = status.UsesCachedPackage &&
                    stage.Stage.Equals("TRANSFER", StringComparison.OrdinalIgnoreCase);
                var state = isCacheReuseTransfer ? "SKIPPED" : stage.State;
                var reason = isCacheReuseTransfer ? "CACHE_REUSED" : stage.Reason;
                var startTime = isCacheReuseTransfer
                    ? "—"
                    : !state.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
                    ? report.StartedAt.AddMilliseconds(stage.StartOffsetMs).ToString("HH:mm:ss.fff")
                    : "未开始";
                var duration = DurationDisplay.Format(isCacheReuseTransfer ? 0 : stage.DurationMs);
                var direction = OtaStagePresentation.Direction(
                    stage.Stage,
                    report.Task.DeviceType,
                    status.UsesCachedPackage);
                var displayReason = OtaStatusDisplay.Reason(reason);
                var directionOrReason = string.IsNullOrWhiteSpace(displayReason)
                    ? direction
                    : $"{direction} · {displayReason}";
                return new ReportStageSummaryItem(
                    OtaStagePresentation.Name(stage.Stage, report.Task.DeviceType, status.UsesCachedPackage),
                    OtaStatusDisplay.State(state),
                    startTime,
                    duration,
                    directionOrReason,
                    StatusColor.For(state));
            }).ToArray();
    }
}

public sealed record ReportStageSummaryItem(
    string Stage,
    string State,
    string StartTime,
    string Duration,
    string DirectionOrReason,
    string StateColor);

public sealed record MqttMessageListItem(string Time, string Direction, string Topic, string Payload, bool IsBinary)
{
    public bool IsOutgoing => Direction == "TX";

    public string PayloadKind => IsBinary ? "二进制" : "文本";
}

public sealed class SelectableExtenderItem : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public SelectableExtenderItem(
        uint extenderId,
        string detail,
        byte deviceType,
        byte softwareVersion,
        byte? asyncSoftwareVersion,
        ushort? asyncAddress,
        int? syncRssi,
        sbyte? syncSnr,
        byte? onlineCount,
        byte? totalCount,
        bool isSelected,
        Action selectionChanged)
    {
        ExtenderId = extenderId;
        Detail = detail;
        DeviceType = deviceType;
        SoftwareVersion = softwareVersion;
        AsyncSoftwareVersion = asyncSoftwareVersion;
        AsyncAddress = asyncAddress;
        SyncRssi = syncRssi;
        SyncSnr = syncSnr;
        OnlineCount = onlineCount;
        TotalCount = totalCount;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public uint ExtenderId { get; }

    public string Detail { get; }

    public byte DeviceType { get; }

    public byte SoftwareVersion { get; private set; }

    public byte? AsyncSoftwareVersion { get; private set; }

    public ushort? AsyncAddress { get; }

    public int? SyncRssi { get; }

    public sbyte? SyncSnr { get; }

    public byte? OnlineCount { get; }

    public byte? TotalCount { get; }

    public string SyncVersionDisplay => ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion);

    public string AsyncVersionDisplay => AsyncSoftwareVersion.HasValue
        ? ProtocolVersionFormatter.FormatWithPrefix(AsyncSoftwareVersion.Value)
        : "--";

    public string AsyncAddressDisplay => AsyncAddress.HasValue
        ? $"0x{AsyncAddress.Value:X4}"
        : "--";

    public string SyncSignalDisplay => SyncRssi.HasValue && SyncSnr.HasValue
        ? $"{SyncRssi} dBm / {SyncSnr} dB"
        : "--";

    public string NodeCountDisplay => OnlineCount.HasValue && TotalCount.HasValue
        ? $"Node {OnlineCount}/{TotalCount} 在线"
        : "Node 状态未知";

    public string IdentityDisplay => AsyncSoftwareVersion.HasValue
        ? $"扩展器 · 同步 {ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion)} · 异步 {ProtocolVersionFormatter.FormatWithPrefix(AsyncSoftwareVersion.Value)}"
        : DeviceType switch
        {
            1 => $"扩展器-异步 · {ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion)}",
            2 => $"扩展器-同步 · {ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion)}",
            _ => $"未知类型 {DeviceType} · {ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion)}",
        };

    public string StatusDisplay => AsyncAddress.HasValue
        ? $"Async 0x{AsyncAddress.Value:X4} · Sync RSSI {SyncRssi} dBm / SNR {SyncSnr} dB · 在线 {OnlineCount}/{TotalCount}"
        : string.Empty;

    public byte? GetSoftwareVersion(DeviceType deviceType) => deviceType switch
    {
        OtaTool.Core.Models.DeviceType.Sync => SoftwareVersion is >= 1 and <= 254 ? SoftwareVersion : null,
        OtaTool.Core.Models.DeviceType.Async => AsyncSoftwareVersion,
        _ => null,
    };

    public void ApplySoftwareVersion(byte softwareVersion)
    {
        if (SoftwareVersion == softwareVersion) return;
        SoftwareVersion = softwareVersion;
        OnPropertyChanged(nameof(SoftwareVersion));
        OnPropertyChanged(nameof(SyncVersionDisplay));
        OnPropertyChanged(nameof(IdentityDisplay));
    }

    public void ApplySoftwareVersion(DeviceType deviceType, byte softwareVersion)
    {
        if (deviceType == OtaTool.Core.Models.DeviceType.Sync)
        {
            ApplySoftwareVersion(softwareVersion);
            return;
        }
        if (deviceType != OtaTool.Core.Models.DeviceType.Async || AsyncSoftwareVersion == softwareVersion)
        {
            return;
        }
        AsyncSoftwareVersion = softwareVersion;
        OnPropertyChanged(nameof(AsyncSoftwareVersion));
        OnPropertyChanged(nameof(AsyncVersionDisplay));
        OnPropertyChanged(nameof(IdentityDisplay));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _selectionChanged();
        }
    }
}

public sealed record LogAnalysisLineViewItem(string Text, bool IsProblem, bool IsHeader);

public sealed record ImportedLogFileItem(string FilePath, long Length, DateTime LastWriteTime)
{
    public string FileName => Path.GetFileName(FilePath);

    public string Detail => Length >= 1024 * 1024
        ? $"{Length / 1024d / 1024d:N1} MB · {LastWriteTime:yyyy-MM-dd HH:mm:ss}"
        : $"{Math.Max(1, Length / 1024d):N1} KB · {LastWriteTime:yyyy-MM-dd HH:mm:ss}";
}

public sealed class SelectableNodeItem : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private bool _canSelect = true;
    private string _selectionHint = string.Empty;

    public SelectableNodeItem(
        GatewayNodeInfo node,
        int sequenceNumber,
        bool isSelected,
        Action selectionChanged)
    {
        NodeId = node.NodeId;
        NodeType = node.NodeType;
        SoftwareVersion = node.SoftwareVersion;
        // 兼容旧设置中的 RSSI 绝对值；新协议解析结果已经统一为 -200～0 dBm。
        Rssi = node.Rssi > 0 ? -Math.Min(node.Rssi, 200) : Math.Max(node.Rssi, -200);
        SequenceNumber = sequenceNumber;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public int SequenceNumber { get; }

    public string SequenceDisplay => SequenceNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    public ushort NodeId { get; }

    public byte NodeType { get; }

    public string NodeTypeDisplay => NodeTypeCatalog.Format(NodeType);

    public byte SoftwareVersion { get; private set; }

    public string SoftwareVersionDisplay => ProtocolVersionFormatter.FormatWithPrefix(SoftwareVersion);

    public void ApplySoftwareVersion(byte softwareVersion)
    {
        if (SoftwareVersion == softwareVersion) return;
        SoftwareVersion = softwareVersion;
        OnPropertyChanged(nameof(SoftwareVersion));
        OnPropertyChanged(nameof(SoftwareVersionDisplay));
    }

    public void RefreshNodeTypeDisplay() => OnPropertyChanged(nameof(NodeTypeDisplay));

    public int Rssi { get; }

    public bool CanSelect
    {
        get => _canSelect;
        private set => SetProperty(ref _canSelect, value);
    }

    public string SelectionHint
    {
        get => _selectionHint;
        private set => SetProperty(ref _selectionHint, value);
    }

    public void ApplyEligibility(int? requiredType, byte? requiredVersion)
    {
        var hasKnownVersion = ProtocolVersionFormatter.IsKnown(SoftwareVersion);
        CanSelect = hasKnownVersion &&
                    (!requiredType.HasValue || NodeType == requiredType.Value) &&
                    (!requiredVersion.HasValue || SoftwareVersion == requiredVersion.Value);
        SelectionHint = CanSelect
            ? string.Empty
            : !hasKnownVersion
                ? "协议返回未知版本，不能升级"
                : $"Patch 要求类型 {NodeTypeCatalog.Format(requiredType ?? NodeType)}、底版本 {ProtocolVersionFormatter.FormatWithPrefix(requiredVersion ?? SoftwareVersion)}";
        if (!CanSelect && IsSelected) IsSelected = false;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !CanSelect) return;
            if (SetProperty(ref _isSelected, value)) _selectionChanged();
        }
    }
}

public sealed class NodeGroupItem : ObservableObject
{
    private readonly Action _selectionChanged;

    public NodeGroupItem(
        uint extenderId,
        IReadOnlyList<GatewayNodeInfo> nodes,
        IReadOnlySet<ushort> selectedNodeIds,
        string? error,
        int reportedCount,
        Action selectionChanged)
    {
        ExtenderId = extenderId;
        Error = error ?? string.Empty;
        ReportedCount = Math.Max(nodes.Count, reportedCount);
        _selectionChanged = selectionChanged;
        Nodes = new ObservableCollection<SelectableNodeItem>(
            nodes.Select((node, index) => new SelectableNodeItem(
                node,
                index + 1,
                selectedNodeIds.Contains(node.NodeId),
                selectionChanged)));
        VisibleNodes = Nodes.ToArray();
    }

    public uint ExtenderId { get; }

    public string Error { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public Visibility NodeListVisibility => Visibility.Visible;

    public ObservableCollection<SelectableNodeItem> Nodes { get; }

    public IReadOnlyList<SelectableNodeItem> VisibleNodes { get; private set; }

    public int TotalNodeCount => Nodes.Count;

    public int ReportedCount { get; }

    public int VisibleNodeCount => VisibleNodes.Count;

    public string NodeCountSummary => VisibleNodeCount == TotalNodeCount && TotalNodeCount == ReportedCount
        ? $"共 {TotalNodeCount} 个在线 Node"
        : $"在线显示 {VisibleNodeCount} / 协议返回 {ReportedCount}";

    public void SetFilter(string searchText, int? filterType)
    {
        foreach (var node in Nodes) node.ApplyEligibility(null, null);
        var query = Nodes.AsEnumerable();
        if (filterType.HasValue)
        {
            query = query.Where(node => node.NodeType == filterType.Value);
        }
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(node => node.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                .Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        VisibleNodes = query.ToArray();
        OnPropertyChanged(nameof(VisibleNodes));
        OnPropertyChanged(nameof(VisibleNodeCount));
        OnPropertyChanged(nameof(NodeCountSummary));
    }

    public void SetAll(bool selected)
    {
        foreach (var node in Nodes) node.IsSelected = selected && node.CanSelect;
        _selectionChanged();
    }
}

public sealed record NodeTypeOption(int Value, string Name)
{
    public string DisplayName => $"{Name}（{Value}）";
}

public static class NodeTypeCatalog
{
    private static readonly IReadOnlyList<NodeTypeOption> BuiltInOptions =
    [
        new(3, "室内灯控"),
        new(4, "开关"),
        new(5, "电源插座"),
        new(6, "DTU"),
        new(7, "路灯控制器"),
    ];

    private static readonly Dictionary<int, string> CustomNames = [];

    public static IReadOnlyList<NodeTypeOption> Options => BuiltInOptions
        .Concat(CustomOptions)
        .OrderBy(item => item.Value)
        .ToArray();

    public static IReadOnlyList<NodeTypeOption> CustomOptions => CustomNames
        .Select(item => new NodeTypeOption(item.Key, item.Value))
        .OrderBy(item => item.Value)
        .ToArray();

    public static bool IsBuiltIn(int value) => BuiltInOptions.Any(item => item.Value == value);

    public static bool IsSelectable(int value) => Options.Any(item => item.Value == value);

    public static void ReplaceCustom(IEnumerable<NodeTypeDefinitionSettings> definitions)
    {
        CustomNames.Clear();
        foreach (var definition in definitions)
        {
            if (definition.Value is >= 2 and <= 63 &&
                !IsBuiltIn(definition.Value) &&
                !string.IsNullOrWhiteSpace(definition.Name))
            {
                CustomNames[definition.Value] = definition.Name.Trim();
            }
        }
    }

    public static void AddOrUpdateCustom(int value, string name)
        => CustomNames[value] = name.Trim();

    public static string Format(int? value)
    {
        if (value is null)
        {
            return "未指定";
        }

        var option = Options.FirstOrDefault(item => item.Value == value.Value);
        return option?.DisplayName ?? $"未知类型（{value.Value}）";
    }
}

internal sealed class UpgradeModeUiState
{
    public string TaskStatusMessage { get; set; } = "当前任务：空闲  · 请选择 Patch 后启动升级";
    public string GlobalLogText { get; set; } = string.Empty;
    public string GatewayStageSummary { get; set; } = "尚未收到 Gateway 阶段状态。";
    public string GatewayStageColor { get; set; } = "#65758B";
    public GatewayOtaStatus? LastGatewayStatus { get; set; }
    public int? GatewayTaskSequence { get; set; }
    public DateTimeOffset? GatewayTaskStartedAt { get; set; }
    public DeviceType GatewayStatusDeviceType { get; set; } = DeviceType.Gateway;
    public string UpgradeRunModeText { get; set; } = "尚未启动";
    public string UpgradeRunModeForeground { get; set; } = "#65758B";
    public string UpgradeRunModeBackground { get; set; } = "#EEF2F7";
    public string UpgradeRunProgressText { get; set; } = "启动任务后显示执行方式和进度。";
    public string DeviceDiscoveryStatus { get; set; } = "尚未刷新在线 Extender。";
    public string NodeDiscoveryStatus { get; set; } = "尚未刷新 Node。";
    public string LogAnalysisStatus { get; set; } = "未导入日志";
    public string LogAnalysisResultText { get; set; } = "尚未执行日志分析。";
    public string LogAnalysisQualityScore { get; set; } = "--";
    public string LogAnalysisQualityGrade { get; set; } = "尚未评估";
    public string LogAnalysisQualitySummary { get; set; } = "分析日志后生成 100 分制质量评估。";
    public string LogAnalysisQualityColor { get; set; } = "#65758B";
    public string SettingsStatus { get; set; } = "设置尚未保存";
    public IReadOnlyList<MqttMessageListItem> MqttMessages { get; set; } = [];
    public string MqttMessageFilter { get; set; } = string.Empty;
    public string GatewaySubscriptionStatus { get; set; } = "填写 Gateway ID 后订阅固定上行主题。";
    public string SubscribedGatewayTopic { get; set; } = string.Empty;
    public IReadOnlyList<string> ObservedGatewayIds { get; set; } = [];
    public Guid? SelectedReportId { get; set; }
    public string ImportedPatchPath { get; set; } = string.Empty;
    public long ImportedPatchLength { get; set; }
    public string ImportedPatchMd5 { get; set; } = string.Empty;
    public string ImportedPatchSha256 { get; set; } = string.Empty;
    public string OldImagePath { get; set; } = string.Empty;
    public string NewImagePath { get; set; } = string.Empty;
    public string OldImageSha256 { get; set; } = string.Empty;
    public string NewImageSha256 { get; set; } = string.Empty;
    public FirmwareIdentity? OldFirmwareIdentity { get; set; }
    public FirmwareIdentity? NewFirmwareIdentity { get; set; }
    public bool AreFirmwareImagesCompatible { get; set; }
    public string PatchPath { get; set; } = string.Empty;
    public string PatchUrl { get; set; } = string.Empty;
    public string PatchMd5 { get; set; } = string.Empty;
    public string PatchSha256 { get; set; } = string.Empty;
    public long PatchLength { get; set; }
    public bool? PatchManifestVerified { get; set; }
    public string ReversePatchPath { get; set; } = string.Empty;
    public string ReversePatchUrl { get; set; } = string.Empty;
    public string ReversePatchMd5 { get; set; } = string.Empty;
    public string ReversePatchSha256 { get; set; } = string.Empty;
    public long ReversePatchLength { get; set; }
    public PackageManifest? SelectedPatchManifest { get; set; }
    public string SelectedRestorePatchPath { get; set; } = string.Empty;
    public string SelectedPatchRestoreDirection { get; set; } = "A → B";
    public string PatchStatus { get; set; } = "请先导入 A 版本和 B 版本固件。";
    public string PatchRestoreTestStatus { get; set; } = "请选择尚未验证的外部 Patch。工具自产 Patch 已自动完成双向还原验证。";
    public string PublishStatus { get; set; } = "未发布";
    public string PublishConnectionTestStatus { get; set; } = "尚未测试 SFTP 和 HTTP 连接。";
    public bool HasPublishedPatches { get; set; }

    public static UpgradeModeUiState CreateEcoLink() => new();

    public static UpgradeModeUiState CreateTraditional() => new()
    {
        GatewayStageSummary = "尚未收到 Gateway 最终升级结果。",
        DeviceDiscoveryStatus = "传统模式不使用 Extender 发现。",
        NodeDiscoveryStatus = "传统模式不使用 Node 发现。",
    };
}

internal static class DurationDisplay
{
    public static string Format(long milliseconds)
    {
        var totalMilliseconds = Math.Max(0, milliseconds);
        var minutes = totalMilliseconds / 60_000;
        var seconds = totalMilliseconds % 60_000 / 1_000;
        var remainderMilliseconds = totalMilliseconds % 1_000;
        return $"{minutes}分{seconds}秒{remainderMilliseconds}毫秒";
    }
}

public sealed record GatewayStageViewItem(
    string Stage,
    DeviceType DeviceType,
    string State,
    long StartOffsetMs,
    long DurationMs,
    string Reason,
    string LocalStartTime,
    double? ProgressPercent,
    bool FreezeRunningAnimation,
    bool UsesCachedPackage)
{
    public string StateColor => StatusColor.For(State);

    public string DisplayDuration => DurationDisplay.Format(DurationMs);

    public string DisplayStage => OtaStagePresentation.Name(Stage, DeviceType, UsesCachedPackage);

    public string Direction => OtaStagePresentation.Direction(Stage, DeviceType, UsesCachedPackage);

    public string DisplayState => OtaStatusDisplay.State(State);

    public string DisplayReason => OtaStatusDisplay.Reason(Reason);

    public string ProgressText => ProgressPercent.HasValue ? $"{ProgressPercent.Value:0.0}%" : string.Empty;

    public Visibility ProgressVisibility => State.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) && ProgressPercent.HasValue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility IndeterminateProgressVisibility => State.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) &&
        !ProgressPercent.HasValue &&
        !FreezeRunningAnimation
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PausedProgressVisibility => State.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) &&
        !ProgressPercent.HasValue &&
        FreezeRunningAnimation
        ? Visibility.Visible
        : Visibility.Collapsed;
}

public sealed record GatewaySubtaskViewItem(
    uint ExtenderId,
    string Stage,
    string Result,
    long ElapsedMs,
    int TargetCount,
    int PreparedCount,
    int SuccessCount,
    int FailedCount,
    string Reason,
    string CacheResult)
{
    public string StateColor => StatusColor.For(Result);

    public string DisplayElapsed => DurationDisplay.Format(ElapsedMs);

    public string DisplayStage => OtaStatusDisplay.IsNodePrepareTimeout(
        Result,
        Reason,
        PreparedCount,
        TargetCount)
            ? $"Node 准备超时（{PreparedCount}/{TargetCount}）"
            : OtaStatusDisplay.Stage(Stage);

    public string DisplayResult => OtaStatusDisplay.State(Result);

    public string DisplayReason => OtaStatusDisplay.Reason(Reason);

    public string CountSummary
    {
        get
        {
            var countSummary = OtaStatusDisplay.IsNodePrepareTimeout(
                Result,
                Reason,
                PreparedCount,
                TargetCount)
                    ? $"已准备 {PreparedCount}/{TargetCount}，未响应 {TargetCount - PreparedCount}"
                    : $"目标 {TargetCount} / 成功 {SuccessCount} / 失败 {FailedCount}";
            var cacheSummary = OtaStatusDisplay.CacheResult(CacheResult);
            return string.IsNullOrWhiteSpace(cacheSummary)
                ? countSummary
                : $"缓存 {cacheSummary} · {countSummary}";
        }
    }
}

public static class OtaStagePresentation
{
    public static string Name(string stage, DeviceType deviceType, bool usesCachedPackage = false)
        => stage.ToUpperInvariant() switch
        {
            "TRANSFER" when usesCachedPackage => "缓存复用",
            "TRANSFER" => "数据传输",
            "REPAIR" when deviceType == DeviceType.Sync => "同步拓展器升级",
            "REPAIR" when deviceType == DeviceType.Async => "异步拓展器升级",
            "REPAIR" => "Node 下游升级",
            _ => OtaStatusDisplay.Stage(stage),
        };

    public static string Direction(string stage, DeviceType deviceType, bool usesCachedPackage = false)
        => stage.ToUpperInvariant() switch
        {
            "REQUEST_ACCEPTED" => "MQTT to 网关",
            "PATCH_DOWNLOAD" => "HTTP to 网关",
            "PATCH_VERIFY" => "网关本地",
            "TRANSFER" when usesCachedPackage => "Sync 本地缓存",
            "PREPARE" or "TRANSFER" => "网关 to Sync",
            "REPAIR" when deviceType == DeviceType.Sync => "Sync 本地",
            "REPAIR" when deviceType == DeviceType.Async => "Sync to Async",
            "REPAIR" => "Async to Node",
            "VERIFY" or "PROGRAM" when deviceType == DeviceType.Async => "Async 本地",
            "VERIFY" or "PROGRAM" => "Node 本地",
            "COMMIT" => "网关 to Sync",
            "BOOT_VERIFY" when deviceType == DeviceType.Async => "Async to 网关",
            "BOOT_VERIFY" => "Node to 网关",
            "FINISHED" when deviceType == DeviceType.Sync => "Sync to 网关",
            "FINISHED" when deviceType == DeviceType.Async => "Async to 网关",
            "FINISHED" when deviceType == DeviceType.Node => "Node to 网关",
            "FINISHED" => "网关本地",
            _ => "—",
        };
}

public static class OtaStatusDisplay
{
    public static string Stage(string code) => StageDescription(code);

    public static string State(string code) => StateDescription(code);

    public static string Reason(string code) => ReasonDescription(code);

    public static string CacheResult(string code) => (code ?? string.Empty).ToUpperInvariant() switch
    {
        "" => string.Empty,
        "HIT" => "命中",
        "MISS" => "未命中",
        "BUSY" => "设备忙",
        "ERROR" => "查询错误",
        "TIMEOUT" => "查询超时",
        "UNKNOWN" => "未知",
        _ => code ?? string.Empty,
    };

    public static string PackageSourceSummary(GatewayOtaStatus status)
    {
        var source = status.UsesCachedPackage
            ? "CACHE"
            : (status.PackageSource ?? string.Empty).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var parts = new List<string>
        {
            source switch
            {
                "CACHE" => "包来源：Sync 缓存",
                "TRANSFER" => "包来源：Gateway 完整传输",
                _ => $"包来源：{source}",
            },
        };
        if (status.CacheHitCount.HasValue && status.CacheTargetTotal.HasValue)
        {
            parts.Add($"缓存命中 {status.CacheHitCount.Value}/{status.CacheTargetTotal.Value}");
        }
        if (status.CacheQueryElapsedMs.HasValue)
        {
            parts.Add($"查询耗时 {DurationDisplay.Format(status.CacheQueryElapsedMs.Value)}");
        }
        return string.Join(" · ", parts);
    }

    public static string StageSummary(
        string stage,
        GatewayOtaSubtask? subtask,
        DeviceType deviceType,
        bool usesCachedPackage = false)
    {
        if (deviceType == DeviceType.Node &&
            subtask is not null && IsNodePrepareTimeout(
                subtask.Result,
                subtask.Reason,
                subtask.PreparedCount,
                subtask.TargetCount))
        {
            return $"Node 准备超时（{subtask.PreparedCount}/{subtask.TargetCount}）";
        }
        if (usesCachedPackage && stage.Equals("TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            return subtask is null
                ? "缓存复用"
                : $"缓存复用完成 · {Stage(subtask.Stage)}";
        }
        return OtaStagePresentation.Name(stage, deviceType, usesCachedPackage);
    }

    public static bool IsNodePrepareTimeout(
        string result,
        string reason,
        int preparedCount,
        int targetCount)
        => result.Equals("FAILED", StringComparison.OrdinalIgnoreCase) &&
           reason.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase) &&
           preparedCount >= 0 &&
           preparedCount < targetCount;

    private static string StageDescription(string code) => code.ToUpperInvariant() switch
    {
        "REQUEST_ACCEPTED" => "请求已受理",
        "PATCH_DOWNLOAD" => "补丁下载",
        "PATCH_VERIFY" => "补丁校验",
        "PREPARE" => "下游准备",
        "TRANSFER" => "分片传输",
        "REPAIR" => "Node 下游升级",
        "VERIFY" => "固件校验",
        "PROGRAM" => "固件写入",
        "COMMIT" => "提交升级",
        "BOOT_VERIFY" => "启动验证",
        "FINISHED" => "升级完成",
        "UNKNOWN" => "未知阶段",
        _ => "未识别阶段",
    };

    private static string StateDescription(string code) => code.ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCEEDED" or "COMPLETED" => "成功",
        "PASSED" => "已通过",
        "SKIPPED" => "已跳过",
        "FAILED" => "失败",
        "CANCELLED" => "已取消",
        "TIMEDOUT" => "已超时",
        "RUNNING" or "ACTIVE" => "进行中",
        "PENDING" => "等待中",
        "UNKNOWN" => "未知状态",
        _ => "未识别状态",
    };

    private static string ReasonDescription(string code) => code.ToUpperInvariant() switch
    {
        "" => string.Empty,
        "TIMEOUT" => "超时",
        "CANCELLED" => "已取消",
        "CACHE_REUSED" => "已复用缓存，跳过 Gateway to Sync 数据传输",
        "DOWNSTREAM_FAILED" => "下游升级失败",
        "OFFLINE" => "目标离线或未注册",
        "VERSION_MISMATCH" => "版本不匹配",
        "SESSION_CONFLICT" => "会话冲突",
        "DEVICE_TYPE_MISMATCH" => "设备类型不匹配",
        "MAINTENANCE_TIMEOUT" => "Flash 维护超时",
        "RESOURCE_UNAVAILABLE" => "运行资源不可用",
        _ => code,
    };
}

public static class StatusColor
{
    public static string For(string state) => state.ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCEEDED" or "COMPLETED" or "PASSED" or "SKIPPED" => "#168A55",
        "FAILED" or "CANCELLED" or "TIMEDOUT" => "#C73A3A",
        "RUNNING" or "ACTIVE" => "#2C68D8",
        "PENDING" => "#8A96A8",
        _ => "#65758B",
    };
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception>? onException = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (onException is not null)
            {
                onException(exception);
            }
            else
            {
                if (Application.Current?.MainWindow?.DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.ShowInformationDialog("操作失败", exception.Message);
                }
                else
                {
                    OtaTool.App.AppMessageDialog.Show("操作失败", exception.Message);
                }
            }
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
