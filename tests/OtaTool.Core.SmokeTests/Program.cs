using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OtaTool.Core.Analysis;
using OtaTool.Core.Execution;
using OtaTool.Core.Diff;
using OtaTool.Core.Discovery;
using OtaTool.Core.Http;
using OtaTool.Core.Mqtt;
using OtaTool.Core.Models;
using OtaTool.Core.Protocols;
using OtaTool.Core.Reports;
using OtaTool.Core.Settings;

var workspace = Path.Combine(Path.GetTempPath(), "ota-tool-smoke", Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(workspace);
    VerifyDefaultExternalMqttSettings();
    VerifyStaticResourceReferences();
    VerifyReadOnlyRunBindings();
    VerifyPatchCenterWorkflow();
    VerifyWindowChromeWorkAreaBounds();
    VerifyStatusPanelLayout();
    VerifyUpdateWindowBindings();
    VerifyUpgradeQualityAssessment();
    VerifyNodeTypePresentation();
    VerifyStageApplicability();
    VerifyGeneratedMetadataPresentation();
    VerifyPatchCenterTitleCapitalization();
    VerifyMqttConfigurationTabs();
    await VerifyPatchAndTaskRulesAsync(workspace);
    await VerifySettingsPersistenceAsync(workspace);
    await VerifyHttpRangeServerAsync(workspace);
    await VerifyEmbeddedBrokerAndMqttClientAsync();
    await VerifyProtocolCodecAndRunnerAsync(workspace);
    await VerifyDeviceDiscoveryAsync();
    await VerifyCycleRunnerAsync(workspace);
    await VerifyReportsAsync(workspace);
    await VerifyDiffManifestGateAsync(workspace);
    Console.WriteLine("全部核心冒烟测试通过。");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"核心冒烟测试失败：{exception}");
    Environment.ExitCode = 1;
}
finally
{
    try
    {
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"清理冒烟测试临时目录失败：{exception.Message}");
        Environment.ExitCode = 1;
    }
}

static void VerifyStageApplicability()
{
    Assert(OtaStagePolicy.IsApplicable(DeviceType.Gateway, "PROGRAM")
        && !OtaStagePolicy.IsApplicable(DeviceType.Gateway, "PREPARE")
        && !OtaStagePolicy.IsApplicable(DeviceType.Gateway, "COMMIT"),
        "网关升级只应显示网关本地执行的有效阶段。");
    Assert(OtaStagePolicy.IsApplicable(DeviceType.Sync, "REPAIR")
        && !OtaStagePolicy.IsApplicable(DeviceType.Sync, "VERIFY"),
        "同步拓展器升级阶段筛选错误。");
    Assert(OtaStagePolicy.IsApplicable(DeviceType.Async, "BOOT_VERIFY")
        && !OtaStagePolicy.IsApplicable(DeviceType.Async, "COMMIT"),
        "异步拓展器升级阶段筛选错误。");
    Assert(OtaStagePolicy.IsApplicable(DeviceType.Node, "COMMIT")
        && OtaStagePolicy.IsApplicable(DeviceType.Node, "BOOT_VERIFY"),
        "Node 升级应显示完整下游闭环阶段。");
    Assert(OtaStagePolicy.IsApplicable(DeviceType.Gateway, "FUTURE_STAGE"),
        "未知的新协议阶段不应被静默隐藏。");
}

static void VerifyStaticResourceReferences()
{
    var assetDirectory = Path.Combine(AppContext.BaseDirectory, "TestAssets");
    var xamlFiles = new[]
    {
        Path.Combine(assetDirectory, "App.xaml"),
        Path.Combine(assetDirectory, "MainWindow.xaml"),
        Path.Combine(assetDirectory, "UpdateWindow.xaml"),
    };
    var xaml = string.Join(Environment.NewLine, xamlFiles.Select(File.ReadAllText));
    var definedKeys = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            "x:Key=\"(?<key>[^\"]+)\"")
        .Select(match => match.Groups["key"].Value)
        .ToHashSet(StringComparer.Ordinal);
    var referencedKeys = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            "\\{StaticResource\\s+(?<key>[^}\\s,]+)")
        .Select(match => match.Groups["key"].Value)
        .ToHashSet(StringComparer.Ordinal);
    var missingKeys = referencedKeys.Where(key => !definedKeys.Contains(key)).Order().ToArray();

    Assert(
        missingKeys.Length == 0,
        $"XAML references undefined StaticResource keys: {string.Join(", ", missingKeys)}");
}

static void VerifyPatchCenterWorkflow()
{
    var assetDirectory = Path.Combine(AppContext.BaseDirectory, "TestAssets");
    var xaml = File.ReadAllText(Path.Combine(assetDirectory, "PatchPage.xaml"));
    var viewModel = File.ReadAllText(Path.Combine(assetDirectory, "MainWindowViewModel.cs"));
    var restoreScript = File.ReadAllText(Path.Combine(assetDirectory, "TestPatchWithOtaTool.ps1"));

    Assert(
        xaml.Contains("ItemsSource=\"{Binding PatchRestoreChoices}\"", StringComparison.Ordinal)
        && xaml.Contains("ItemsControl ItemsSource=\"{Binding PatchCatalog}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding PublishState}\"", StringComparison.Ordinal),
        "Patch details must show all upgrade files while restore selection excludes full images.");
    Assert(
        xaml.Contains("IsChecked=\"{Binding IsSelectedForPublish, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && xaml.Contains("取消勾选不会删除源文件", StringComparison.Ordinal)
        && viewModel.Contains(".Where(item => item.IsSelectedForPublish)", StringComparison.Ordinal)
        && viewModel.Contains("patch.IsSelectedForPublish = false;", StringComparison.Ordinal)
        && viewModel.Contains("public sealed class PatchSelection : ObservableObject", StringComparison.Ordinal),
        "Patch publication must use an independently mutable per-file mark and clear successful marks without deleting source files.");
    Assert(
        System.Text.RegularExpressions.Regex.Matches(xaml, "VerticalScrollBarVisibility=\"Visible\"").Count >= 3
        && xaml.Contains("Margin=\"0,7,0,12\" MaxHeight=\"240\"", StringComparison.Ordinal)
        && xaml.Contains("Padding=\"0,0,12,8\"", StringComparison.Ordinal),
        "Patch columns must reserve scrollbar gutters and keep the final catalog item fully visible.");
    Assert(
        xaml.Contains("Content=\"Patch 制作\"", StringComparison.Ordinal)
        && xaml.Contains("Command=\"{Binding GeneratePatchCommand}\"", StringComparison.Ordinal)
        && xaml.Contains("IsEnabled=\"{Binding CanGeneratePatch}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding PatchStatus}\"", StringComparison.Ordinal)
        && viewModel.Contains("public bool CanGeneratePatch", StringComparison.Ordinal)
        && viewModel.Contains("OnPropertyChanged(nameof(CanGeneratePatch));", StringComparison.Ordinal),
        "Patch generation must stay disabled until valid A/B firmware is imported and must expose operation feedback.");
    Assert(
        viewModel.Contains("Gateway 完整镜像|*.bin", StringComparison.Ordinal)
        && viewModel.Contains("完整 .bin 镜像仅支持网关升级", StringComparison.Ordinal),
        "Existing upgrade-file import must accept Gateway full images in both protocol modes.");
    Assert(
        viewModel.Contains("裸 Patch 或元数据不完整时仍应出现在详情列表中", StringComparison.Ordinal)
        && viewModel.Contains("item.IsFullImage", StringComparison.Ordinal)
        && viewModel.Contains("selectedDeviceType == DeviceType.Gateway", StringComparison.Ordinal)
        && viewModel.Contains("!IsEcoLink || item.ManifestVerified", StringComparison.Ordinal)
        && viewModel.Contains("item.OtaDeviceType == selectedDeviceType", StringComparison.Ordinal),
        "EcoLink choices must accept Gateway full images while differential patches remain verified and match the selected device type.");
    Assert(
        restoreScript.Contains("[int]$SkippedBootloaderBytes = 28672", StringComparison.Ordinal)
        && restoreScript.Contains("[System.Array]::Copy($oldBytes, 0, $expectedBytes, 0, $SkippedBootloaderBytes)", StringComparison.Ordinal)
        && restoreScript.Contains("expected_restored_sha256", StringComparison.Ordinal),
        "Patch restore verification must preserve and exclude the 28 KiB bootloader partition.");
    Assert(
        restoreScript.Contains("function Get-FileDigest", StringComparison.Ordinal)
        && !restoreScript.Contains("Get-FileHash", StringComparison.Ordinal),
        "Patch restore verification must calculate hashes without relying on the optional Get-FileHash cmdlet.");
    Assert(
        restoreScript.Contains("Tools\\OTA_TOOL\\OTA_TOOL.exe", StringComparison.Ordinal)
        && !restoreScript.Contains("D:\\tools\\OTA_TOOL", StringComparison.Ordinal)
        && viewModel.Contains("Path.Combine(AppContext.BaseDirectory, \"Tools\", \"OTA_TOOL\", \"OTA_TOOL.exe\")", StringComparison.Ordinal),
        "Patch restore verification must use the tool bundled with the desktop application instead of a machine-local installation.");
}

static void VerifyWindowChromeWorkAreaBounds()
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml.cs"));
    var viewModel = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindowViewModel.cs"));
    Assert(
        source.Contains("WmGetMinMaxInfo", StringComparison.Ordinal)
        && source.Contains("MonitorFromWindow", StringComparison.Ordinal)
        && source.Contains("monitorInfo.WorkArea", StringComparison.Ordinal),
        "Custom window maximize handling must constrain the window to the current monitor work area.");
    Assert(source.Contains("protected override void OnClosing(CancelEventArgs eventArgs)", StringComparison.Ordinal)
        && source.Contains("viewModel.RequestCloseApplicationConfirmation()", StringComparison.Ordinal)
        && source.Contains("viewModel.CloseApplicationRequested += OnCloseApplicationRequested;", StringComparison.Ordinal)
        && viewModel.Contains("PatchDialogAction.CloseApplication", StringComparison.Ordinal)
        && viewModel.Contains("\"升级任务仍在进行\"", StringComparison.Ordinal)
        && viewModel.Contains("\"仍然关闭\"", StringComparison.Ordinal),
        "存在活动升级或循环等待时，关闭窗口必须使用应用内统一样式确认弹框。 ");
}

static void VerifyDefaultExternalMqttSettings()
{
    var settings = new AppSettings();
    Assert(settings.MqttHost == "117.172.29.2" && settings.MqttPort == 36106, "公网 MQTT 默认地址或端口错误。");
}

static void VerifyReadOnlyRunBindings()
{
    var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml");
    var xaml = File.ReadAllText(xamlPath);
    var bindings = System.Text.RegularExpressions.Regex.Matches(
        xaml,
        "<Run\\s+Text=\"\\{Binding(?<binding>[^}\"]*)}\"");
    Assert(bindings.Count > 0, "未找到需要检查的 Run.Text 数据绑定。");
    foreach (System.Text.RegularExpressions.Match match in bindings)
    {
        Assert(
            match.Groups["binding"].Value.Contains("Mode=OneWay", StringComparison.Ordinal),
            $"Run.Text 数据绑定必须显式使用 OneWay，当前绑定：{match.Value}");
    }
}

static void VerifyStatusPanelLayout()
{
    var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml");
    var xaml = File.ReadAllText(xamlPath);
    var patchPage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "PatchPage.xaml"));
    var viewModel = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindowViewModel.cs"));
    var codeBehind = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml.cs"));
    var appCodeBehind = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml.cs"));
    var appDialog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "AppMessageDialog.xaml"));
    Assert(!xaml.Contains("Opacity=\"0\"", StringComparison.Ordinal)
        && codeBehind.Contains("await viewModel.Initialization;", StringComparison.Ordinal)
        && !codeBehind.Contains("Opacity = 1;", StringComparison.Ordinal)
        && viewModel.Contains("ApplyMode(restoreSelectedPage: false);", StringComparison.Ordinal)
        && viewModel.Contains("private void ApplyMode(bool restoreSelectedPage = true)", StringComparison.Ordinal)
        && viewModel.Contains("_startupSettings = LoadStartupSettings();", StringComparison.Ordinal)
        && viewModel.Contains("ApplyStartupShellSettings(_startupSettings);", StringComparison.Ordinal)
        && viewModel.Contains("await LoadSettingsAsync(_startupSettings);", StringComparison.Ordinal),
        "主窗口应立即显示，并在每次启动时固定进入 MQTT 配置页面。");
    Assert(xaml.Contains("Header=\"阶段时间线\" IsExpanded=\"True\"", StringComparison.Ordinal),
        "阶段时间线应默认展开。");
    Assert(xaml.Contains("Header=\"Extender 子任务\" IsExpanded=\"True\"", StringComparison.Ordinal),
        "Extender 子任务应默认展开。");
    Assert(!xaml.Contains("<ItemsControl Margin=\"0,10,0,0\" ItemsSource=\"{Binding GatewayStages}\"", StringComparison.Ordinal),
        "重复的阶段概览轴应移除。");
    Assert(xaml.Contains("Text=\"{Binding DisplayStage}\"", StringComparison.Ordinal)
        && xaml.Contains("Value=\"{Binding ProgressPercent, Mode=OneWay}\"", StringComparison.Ordinal),
        "阶段时间线应显示中文阶段名称和确定进度。");
    Assert(xaml.Contains("Text=\"升级状态机\"", StringComparison.Ordinal)
        && !xaml.Contains("Text=\"升级状态确认\"", StringComparison.Ordinal),
        "右侧状态区域应统一命名为升级状态机。");
    Assert(xaml.Contains("Text=\"正向升级 Patch\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"反向升级 Patch\"", StringComparison.Ordinal)
        && xaml.Contains("Content=\"正向升级\"", StringComparison.Ordinal)
        && xaml.Contains("Command=\"{Binding StartForwardTaskCommand}\"", StringComparison.Ordinal)
        && xaml.Contains("IsEnabled=\"{Binding CanStartForwardUpgrade}\"", StringComparison.Ordinal)
        && xaml.Contains("Content=\"反向升级\"", StringComparison.Ordinal)
        && xaml.Contains("Command=\"{Binding StartReverseTaskCommand}\"", StringComparison.Ordinal)
        && xaml.Contains("IsEnabled=\"{Binding CanStartReverseUpgrade}\"", StringComparison.Ordinal)
        && !xaml.Contains("Content=\"启动单次升级\"", StringComparison.Ordinal)
        && xaml.Contains("Content=\"启动循环升级\"", StringComparison.Ordinal),
        "单次升级应拆分为正向和反向入口，并与循环升级位于同一任务区域。");
    Assert(viewModel.Contains("StartForwardTaskCommand = new AsyncRelayCommand(() => StartSingleTaskAsync(reverse: false));", StringComparison.Ordinal)
        && viewModel.Contains("StartReverseTaskCommand = new AsyncRelayCommand(() => StartSingleTaskAsync(reverse: true));", StringComparison.Ordinal)
        && viewModel.Contains("IsPatchConfiguredForDirection(SelectedUpgradePatch, reverse: false)", StringComparison.Ordinal)
        && viewModel.Contains("IsPatchConfiguredForDirection(SelectedReverseUpgradePatch, reverse: true)", StringComparison.Ordinal)
        && viewModel.Contains("IsSelectedTargetAtDirectionStartVersion(reverse: false)", StringComparison.Ordinal)
        && viewModel.Contains("IsSelectedTargetAtDirectionStartVersion(reverse: true)", StringComparison.Ordinal)
        && viewModel.Contains("OldVersion = reverse ? NewVersion : OldVersion", StringComparison.Ordinal)
        && viewModel.Contains("NewVersion = reverse ? OldVersion : NewVersion", StringComparison.Ordinal)
        && viewModel.Contains("ApplySuccessfulUpgradeVersion(completedTask);", StringComparison.Ordinal),
        "正反向按钮只能在 Patch 方向和目标底版本匹配时启用，成功后应更新本次发现版本以便直接执行反向升级。");
    Assert(xaml.Contains("<Grid Margin=\"0,20,0,0\">", StringComparison.Ordinal)
        && xaml.Contains("<ColumnDefinition Width=\"1.25*\" />", StringComparison.Ordinal)
        && xaml.Contains("Text=\"升级类型\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"旧版本\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"新版本\"", StringComparison.Ordinal),
        "升级类型、旧版本和新版本应位于同一行。");
    Assert(xaml.Contains("Text=\"阶段\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"状态\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"开始时间\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"方向\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"耗时\"", StringComparison.Ordinal),
        "阶段时间线应显示列标题。");
    Assert(xaml.Contains("<Grid Grid.Column=\"5\" VerticalAlignment=\"Center\">", StringComparison.Ordinal)
        && xaml.Contains("<ProgressBar Height=\"6\" VerticalAlignment=\"Center\"", StringComparison.Ordinal),
        "阶段进度条应与所在阶段行垂直居中对齐。");
    Assert(xaml.Contains("Content=\"取消任务\"", StringComparison.Ordinal)
        && xaml.Contains("Visibility=\"{Binding PausedProgressVisibility}\"", StringComparison.Ordinal),
        "状态机应提供取消任务按钮，并在暂停查询时显示静态提示。");
    Assert(xaml.Contains("Text=\"{Binding UpgradeRunModeText}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding UpgradeRunProgressText}\"", StringComparison.Ordinal)
        && viewModel.Contains("UpgradeRunModeText = $\"单次 {task.OldVersion} to {task.NewVersion}\"", StringComparison.Ordinal)
        && viewModel.Contains("UpgradeRunModeText = $\"循环 1/{cycleRounds} {forward.OldVersion} to {forward.NewVersion}\"", StringComparison.Ordinal),
        "状态机必须明确显示单次/循环升级方式和当前轮次进度。");
    Assert(xaml.Contains("Text=\"{Binding DisplayDuration}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding DisplayElapsed, Mode=OneWay}\"", StringComparison.Ordinal)
        && viewModel.Contains("$\"{minutes}分{seconds}秒{remainderMilliseconds}毫秒\"", StringComparison.Ordinal)
        && !viewModel.Contains(" min ", StringComparison.Ordinal),
        "阶段和子任务耗时应使用无空格的中文分、秒、毫秒组合格式。");
    Assert(viewModel.Contains("PatchDialogAction.CancelTask", StringComparison.Ordinal)
        && viewModel.Contains("\"确认取消任务\"", StringComparison.Ordinal)
        && viewModel.Contains("await CancelActiveTaskAsync();", StringComparison.Ordinal)
        && viewModel.Contains("public bool CanCancelTask => _runner?.HasActiveTask == true || _isCycleUpgradeRunning;", StringComparison.Ordinal),
        "取消升级任务前必须使用应用内统一样式确认弹框。");
    Assert(!xaml.Contains("Command=\"{Binding SelectAllCommand}\"", StringComparison.Ordinal)
        && !xaml.Contains("Command=\"{Binding ClearCommand}\"", StringComparison.Ordinal),
        "Node 分组内不应保留重复的全选和取消按钮。");
    var taskStart = xaml.IndexOf("<!-- 升级任务 -->", StringComparison.Ordinal);
    var taskEnd = xaml.IndexOf("<!-- 日志分析 -->", taskStart, StringComparison.Ordinal);
    Assert(taskStart >= 0 && taskEnd > taskStart, "未找到升级任务页面布局。");
    var taskLayout = xaml[taskStart..taskEnd];
    Assert(taskLayout.Contains("<Grid Visibility=\"{Binding TaskPageVisibility}\">", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Matches(
            taskLayout,
            "<ScrollViewer[^>]*VerticalScrollBarVisibility=\"Visible\"").Count == 2,
        "升级配置和升级状态机应分别使用独立且稳定占位的滚动条。");
    Assert(taskLayout.Contains("<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"16\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>", StringComparison.Ordinal),
        "升级配置和升级状态机应使用等宽双栏布局。");
    Assert(taskLayout.Contains("IsEnabled=\"{Binding CanStartCycleUpgrade}\"", StringComparison.Ordinal),
        "循环升级按钮应根据反向 Patch 前置条件更新可用状态。");
    Assert(taskLayout.Contains("Text=\"循环升级设置\"", StringComparison.Ordinal)
        && taskLayout.Contains("ItemsSource=\"{Binding CycleIntervalModes}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"循环次数\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"{Binding CycleRounds, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Matches(taskLayout, "<Grid Margin=\"0,9,0,0\">").Count >= 2
        && taskLayout.Contains("Text=\"{Binding CycleFixedIntervalSeconds, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"{Binding CycleRandomMinimumSeconds, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"{Binding CycleRandomMaximumSeconds, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && viewModel.Contains("new OtaCycleDefinition(forward, reverse, cycleRounds, cycleInterval)", StringComparison.Ordinal)
        && viewModel.Contains("cycle.Waiting +=", StringComparison.Ordinal),
        "循环升级应支持固定或自定义范围的随机秒级间隔，并在等待时更新状态机。");
    Assert(!taskLayout.Contains("Content=\"导出报告\"", StringComparison.Ordinal)
        && !taskLayout.Contains("ExportReportCommand", StringComparison.Ordinal),
        "升级任务页不应保留手动导出报告入口。");
    Assert(taskLayout.Contains("Content=\"{Binding DeviceDiscoveryButtonText}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Command=\"{Binding RefreshExtendersCommand}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Content=\"{Binding NodeDiscoveryButtonText}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Command=\"{Binding RefreshNodesCommand}\"", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Matches(taskLayout, "IsEnabled=\"\\{Binding CanRefreshDiscovery\\}\"").Count == 2
        && taskLayout.Contains("MinWidth=\"112\"", StringComparison.Ordinal)
        && taskLayout.Contains("Content=\"{Binding ExtenderSelectionToggleText}\" Command=\"{Binding ToggleExtenderSelectionCommand}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Visibility=\"{Binding ExtenderTargetListVisibility}\"", StringComparison.Ordinal)
        && !taskLayout.Contains("Text=\"在线 Extender\"", StringComparison.Ordinal),
        "Gateway、Extender 与 Node 应使用宽度稳定的手动刷新入口，Gateway 查询时隐藏 Extender 选择控件。");
    Assert(viewModel.Contains("public bool CanRefreshDiscovery => IsEcoLink && IsMqttConnected && !IsDiscoveringDevices && !IsUpgradeInProgress;", StringComparison.Ordinal)
        && viewModel.Contains("OnPropertyChanged(nameof(CanRefreshDiscovery));", StringComparison.Ordinal)
        && viewModel.Contains("升级过程中不能刷新 Extender。", StringComparison.Ordinal)
        && viewModel.Contains("升级过程中不能刷新 Node。", StringComparison.Ordinal)
        && viewModel.Contains("if (!IsEcoLink || IsDiscoveringDevices)", StringComparison.Ordinal)
        && !viewModel.Contains("SelectedTaskType != NodeTaskType || IsDiscoveringDevices", StringComparison.Ordinal)
        && viewModel.Contains(": (int?)null;", StringComparison.Ordinal),
        "设备刷新应只依赖 EcoLink、MQTT 连接和无活动升级；Node 查询不得依赖 Patch 或升级类型。");
    Assert(taskLayout.Contains("Margin=\"0,8,0,0\" Text=\"{Binding GatewayIdTaskHint}\"", StringComparison.Ordinal)
        && taskLayout.Contains("<Border Margin=\"0,10,0,0\" Padding=\"10\"", StringComparison.Ordinal),
        "反向 Patch 下方留白应保持紧凑。");
    var nodeDiscoveryStart = taskLayout.IndexOf("Visibility=\"{Binding NodeDiscoveryVisibility}\"", StringComparison.Ordinal);
    Assert(nodeDiscoveryStart >= 0
        && taskLayout.IndexOf("Text=\"批量选择 Node 类型\"", nodeDiscoveryStart, StringComparison.Ordinal) > nodeDiscoveryStart
        && taskLayout.IndexOf("Text=\"筛选 Node ID\"", nodeDiscoveryStart, StringComparison.Ordinal) > nodeDiscoveryStart
        && !taskLayout.Contains("Text=\"Node ID 筛选（可选）\"", StringComparison.Ordinal),
        "Node 类型批量选择和 Node ID 筛选应位于 Node 列表工具栏中。");
    Assert(!taskLayout.Contains("Content=\"{Binding NodeSelectionToggleText}\" Command=\"{Binding ToggleNodeSelectionCommand}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"{Binding NodeDiscoveryStatus}\"", StringComparison.Ordinal)
        && taskLayout.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal),
        "Node 应按类型自动选择，并使用虚拟化列表支持 256 个节点。");
    Assert(taskLayout.Contains("<Trigger Property=\"HasItems\" Value=\"False\">", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"无满足筛选条件的 Node\"", StringComparison.Ordinal)
        && taskLayout.Contains("MinHeight=\"54\"", StringComparison.Ordinal),
        "Node 筛选无结果时应保持稳定的列表高度并显示明确空状态。");
    Assert(taskLayout.Contains("Text=\"{Binding SequenceDisplay, Mode=OneWay}\"", StringComparison.Ordinal)
        && taskLayout.Contains("Text=\"{Binding NodeCountSummary, Mode=OneWay}\"", StringComparison.Ordinal)
        && taskLayout.Contains("MaxHeight=\"250\"", StringComparison.Ordinal)
        && viewModel.Contains("public string SequenceDisplay", StringComparison.Ordinal)
        && viewModel.Contains("public string NodeCountSummary", StringComparison.Ordinal)
        && viewModel.Contains("OnPropertyChanged(nameof(NodeCountSummary));", StringComparison.Ordinal),
        "Node 长列表应保持内部滚动，为节点显示稳定序号，并动态展示筛选数/总数。");
    Assert(taskLayout.Contains("x:Name=\"TaskConfigurationScrollViewer\"", StringComparison.Ordinal)
        && taskLayout.Contains("PreviewMouseWheel=\"OnNodeListPreviewMouseWheel\"", StringComparison.Ordinal)
        && codeBehind.Contains("nodeListScrollViewer.ScrollableHeight > 0", StringComparison.Ordinal)
        && codeBehind.Contains("TaskConfigurationScrollViewer.ScrollToVerticalOffset(", StringComparison.Ordinal),
        "Node 列表无法继续内部滚动时，鼠标滚轮应继续驱动升级配置页面滚动。");
    Assert(viewModel.Contains("public bool IsRefreshingExtenders", StringComparison.Ordinal)
        && viewModel.Contains("public bool IsRefreshingNodes", StringComparison.Ordinal)
        && viewModel.Contains("SelectEligibleNodesAfterRefresh();", StringComparison.Ordinal),
        "Extender/Node 刷新状态必须相互独立，Node 刷新后应自动选择当前类型的节点。");
    Assert(viewModel.Contains("group.SetFilter(NodeIdSearch, filterType);", StringComparison.Ordinal)
        && viewModel.Contains("query = query.Where(node => node.NodeType == filterType.Value);", StringComparison.Ordinal)
        && viewModel.Contains("node.ApplyEligibility(null, null);", StringComparison.Ordinal)
        && !viewModel.Contains("group.SetFilter(NodeIdSearch, filterType, requiredPatchType", StringComparison.Ordinal),
        "Node 列表和勾选应只按用户选择类型处理，不得被 Patch 类型或版本禁用。");
    Assert(viewModel.Contains("ClearDiscoveredExtenderResults();", StringComparison.Ordinal)
        && viewModel.Contains("ClearDiscoveredNodeResults();", StringComparison.Ordinal)
        && viewModel.Contains("results.Where(result => result.IsSuccess)", StringComparison.Ordinal),
        "设备刷新开始时应清理旧结果，失败结果不得继续显示为有效 Extender 或 Node。");
    var mqttNavigation = viewModel.IndexOf("AddNavigation(\"01\", \"MQTT 配置\")", StringComparison.Ordinal);
    var patchNavigation = viewModel.IndexOf("AddNavigation(\"02\", \"PATCH 中心\")", StringComparison.Ordinal);
    var taskNavigation = viewModel.IndexOf("AddNavigation(\"03\", \"升级任务\")", StringComparison.Ordinal);
    var reportNavigation = viewModel.IndexOf("AddNavigation(\"04\", \"历史报告\")", StringComparison.Ordinal);
    var logNavigation = viewModel.IndexOf("AddNavigation(\"05\", \"日志分析\")", StringComparison.Ordinal);
    var settingsNavigation = viewModel.IndexOf("AddNavigation(IsEcoLink ? \"06\" : \"05\", \"系统设置\")", StringComparison.Ordinal);
    Assert(mqttNavigation >= 0
        && mqttNavigation < patchNavigation
        && patchNavigation < taskNavigation
        && taskNavigation < reportNavigation
        && reportNavigation < logNavigation
        && logNavigation < settingsNavigation
        && viewModel.Contains("item.Name == \"MQTT 配置\"", StringComparison.Ordinal),
        "EcoLink 导航顺序应为 MQTT、PATCH、升级任务、历史报告、日志分析、系统设置。");
    Assert(viewModel.Contains("Dictionary<string, ModeWorkspaceSettings> _modeWorkspaces", StringComparison.Ordinal)
        && viewModel.Contains("SaveCurrentModeUiState();", StringComparison.Ordinal)
        && viewModel.Contains("ApplyCurrentModeWorkspace();", StringComparison.Ordinal)
        && viewModel.Contains("RestoreCurrentModeUpgradeUiState();", StringComparison.Ordinal)
        && viewModel.Contains("report.Task.Mode == (IsEcoLink ? OtaMode.EcoLink : OtaMode.Traditional)", StringComparison.Ordinal)
        && viewModel.Contains("OtaTool/{CurrentModeKey}/{suffix}", StringComparison.Ordinal),
        "两种协议模式必须使用独立工作区、运行状态、报告视图和 Credential Manager 凭据键。");
    Assert(viewModel.Contains("autoExport: IsTerminalState(update.State) && !_isCycleUpgradeRunning", StringComparison.Ordinal)
        && viewModel.Contains("测试完成，报告已导出到", StringComparison.Ordinal)
        && viewModel.Contains("report.FinalState == OtaTaskState.Succeeded ? \"通过\" : \"失败\"", StringComparison.Ordinal)
        && xaml.Contains("Visibility=\"{Binding DialogResultStampVisibility}\"", StringComparison.Ordinal)
        && xaml.Contains("<RotateTransform Angle=\"-12\" />", StringComparison.Ordinal)
        && !viewModel.Contains("ExportActiveReportAsync", StringComparison.Ordinal),
        "成功或失败终态应自动导出报告、提示完整路径，并显示倾斜的通过/失败印章。");
    Assert(viewModel.Contains("PatchDialogAction.StartCycleUpgrade", StringComparison.Ordinal)
        && viewModel.Contains("BuildCycleUpgradeConfirmationMessage(forward, reverse, cycleInterval, CycleRounds)", StringComparison.Ordinal)
        && viewModel.Contains("循环轮数：{rounds} 轮（共 {rounds * 2} 次单次升级）", StringComparison.Ordinal)
        && viewModel.Contains("单次间隔：{intervalText}", StringComparison.Ordinal)
        && viewModel.Contains("正向 Patch：{Path.GetFileName(forward.PatchPath)}", StringComparison.Ordinal)
        && viewModel.Contains("反向 Patch：{Path.GetFileName(reverse.PatchPath)}", StringComparison.Ordinal),
        "循环升级启动前应使用统一弹框确认轮数、间隔和正反向 Patch。 ");
    Assert(viewModel.Contains("\"TRANSFER\" => \"分片传输\"", StringComparison.Ordinal),
        "TRANSFER 阶段应显示为分片传输。");
    Assert(viewModel.Contains("\"TRANSFER\" => \"数据传输\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REPAIR\" => \"Node 下游升级\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REPAIR\" when deviceType == DeviceType.Async => \"异步拓展器升级\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REPAIR\" when deviceType == DeviceType.Sync => \"同步拓展器升级\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REQUEST_ACCEPTED\" => \"MQTT to 网关\"", StringComparison.Ordinal)
        && viewModel.Contains("\"PATCH_DOWNLOAD\" => \"HTTP to 网关\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REPAIR\" when deviceType == DeviceType.Async => \"Sync to Async\"", StringComparison.Ordinal)
        && viewModel.Contains("\"REPAIR\" => \"Async to Node\"", StringComparison.Ordinal)
        && viewModel.Contains("deviceType == DeviceType.Node &&", StringComparison.Ordinal)
        && viewModel.Contains("OtaStagePresentation.Name(stage.Stage, report.Task.DeviceType)", StringComparison.Ordinal)
        && viewModel.Contains("Node 准备超时（{subtask.PreparedCount}/{subtask.TargetCount}）", StringComparison.Ordinal)
        && viewModel.Contains("public string DisplayReason => OtaStatusDisplay.Reason(Reason);", StringComparison.Ordinal)
        && viewModel.Contains("public static string Stage(string code) => StageDescription(code);", StringComparison.Ordinal)
        && viewModel.Contains("public static string State(string code) => StateDescription(code);", StringComparison.Ordinal),
        "阶段和状态应仅显示中文名称，传输方向统一使用 to，并准确提示 Node 准备超时进度。");
    Assert(viewModel.Contains("var displayStage = status.Stage;", StringComparison.Ordinal)
        && viewModel.Contains("_gatewayTaskSequence != status.TaskSequence", StringComparison.Ordinal)
        && viewModel.Contains("_gatewayTaskStartedAt?.AddMilliseconds(stage.StartOffsetMs)", StringComparison.Ordinal)
        && viewModel.Contains("var hasTerminalGatewayFact = _lastGatewayStatus is not null", StringComparison.Ordinal)
        && viewModel.Contains("GatewayStageColor = StatusColor.For(stateCode);", StringComparison.Ordinal),
        "总体阶段应采用 Gateway 阶段，阶段开始时间必须基于稳定任务起点，终态不得覆盖具体失败阶段。");
    Assert(xaml.Contains("Content=\"查看详细报告\"", StringComparison.Ordinal)
        && xaml.Contains("Content=\"归档报告\"", StringComparison.Ordinal)
        && xaml.Contains("GroupName=\"ReportScopeTabs\" Style=\"{StaticResource ModeRadio}\"", StringComparison.Ordinal)
        && xaml.Contains("IsChecked=\"{Binding IsShowingActiveReports, Mode=OneWay}\"", StringComparison.Ordinal)
        && xaml.Contains("IsChecked=\"{Binding IsShowingArchivedReports, Mode=OneWay}\"", StringComparison.Ordinal)
        && viewModel.Contains("public bool IsShowingActiveReports => !_showArchivedReports;", StringComparison.Ordinal)
        && viewModel.Contains("public bool IsShowingArchivedReports => _showArchivedReports;", StringComparison.Ordinal)
        && xaml.Contains("Key=\"Delete\" Command=\"{Binding DeleteReportCommand}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"报告总结\"", StringComparison.Ordinal)
        && xaml.Contains("ItemsSource=\"{Binding SelectedReport.StageTimeline, Mode=OneWay}\"", StringComparison.Ordinal)
        && !xaml.Contains("Text=\"最近事件\"", StringComparison.Ordinal)
        && xaml.Contains("Visibility=\"{Binding GlobalDialogVisibility}\"", StringComparison.Ordinal),
        "历史报告应显示去重后的阶段时间线，并支持查看、归档、删除与应用内确认弹框。");
    Assert(xaml.Contains("Command=\"{Binding ConfirmPatchDialogCommand}\" IsDefault=\"{Binding IsGlobalDialogConfirmDefault}\"", StringComparison.Ordinal)
        && patchPage.Contains("Command=\"{Binding ConfirmPatchDialogCommand}\" IsDefault=\"{Binding IsPatchDialogConfirmDefault}\"", StringComparison.Ordinal)
        && viewModel.Contains("_patchDialogAction is PatchDialogAction.Delete or PatchDialogAction.Publish;", StringComparison.Ordinal)
        && viewModel.Contains("_patchDialogAction is not (PatchDialogAction.Delete or PatchDialogAction.Publish);", StringComparison.Ordinal),
        "所有应用内弹框都应支持按 Enter 确认，并且只能激活当前可见弹框的默认按钮。");
    Assert(!viewModel.Contains("MessageBox.Show", StringComparison.Ordinal)
        && !codeBehind.Contains("MessageBox.Show", StringComparison.Ordinal)
        && !appCodeBehind.Contains("MessageBox.Show", StringComparison.Ordinal)
        && appDialog.Contains("IsDefault=\"True\"", StringComparison.Ordinal)
        && appDialog.Contains("Background=\"#276EF1\"", StringComparison.Ordinal)
        && xaml.Contains("ItemsSource=\"{Binding GatewayIdHistory}\"", StringComparison.Ordinal)
        && xaml.Contains("IsEditable=\"True\"", StringComparison.Ordinal)
        && viewModel.Contains("await _mqtt.UnsubscribeAsync(previousTopic);", StringComparison.Ordinal)
        && viewModel.Contains("await ReplaceGatewaySubscriptionAsync(GatewaySubscriptionTopic);", StringComparison.Ordinal)
        && viewModel.Contains("MqttMessages.Clear();", StringComparison.Ordinal)
        && viewModel.Contains("if (existing is not null)", StringComparison.Ordinal)
        && !viewModel.Contains("GatewayIdHistory.Remove(existing);", StringComparison.Ordinal),
        "应用提示应使用统一样式；Gateway ID 历史选择不得被集合重排清空，切换主题时应取消旧订阅并清空收发记录。");
    Assert(viewModel.Contains("return IsHttpServiceRunning ? GetLocalPatchUrl(patchPath) : GetPublicPatchUrl(patchPath);", StringComparison.Ordinal)
        && !viewModel.Contains("HttpUsesLocalServer && !_httpRangeServer.IsRunning", StringComparison.Ordinal)
        && viewModel.Contains("本地 HTTP Range 服务未运行，且未配置可用的公网 HTTP 地址", StringComparison.Ordinal),
        "Patch 下载地址应在本地服务运行时优先使用本地，否则自动回退公网地址。");
    Assert(xaml.Contains("Text=\"{Binding LogAnalysisQualityScore}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding LogAnalysisQualityGrade}\"", StringComparison.Ordinal)
        && xaml.Contains("ItemsSource=\"{Binding LogAnalysisResultLines, Mode=OneWay}\"", StringComparison.Ordinal)
        && xaml.Contains("Value=\"#C53333\"", StringComparison.Ordinal)
        && xaml.Contains("<Grid Visibility=\"{Binding LogPageVisibility}\">", StringComparison.Ordinal)
        && xaml.Contains("<Border Grid.Column=\"0\" Style=\"{StaticResource Card}\">", StringComparison.Ordinal)
        && xaml.Contains("<Border Grid.Row=\"2\" MinHeight=\"58\"", StringComparison.Ordinal)
        && xaml.Contains("<StackPanel Grid.Row=\"3\" Margin=\"0,10,0,0\">", StringComparison.Ordinal)
        && !xaml.Contains("MaxHeight=\"270\"", StringComparison.Ordinal)
        && xaml.Contains("<ScrollViewer Grid.Column=\"2\" VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal)
        && !xaml.Contains("<ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">\n                                        <TextBlock Padding=\"18\"", StringComparison.Ordinal),
        "日志分析页应显示 100 分制质量评估、使用舒展排版，并仅在内容溢出时允许左右面板独立滚动。");
    Assert(xaml.Contains("ItemsSource=\"{Binding ImportedLogFiles}\"", StringComparison.Ordinal)
        && xaml.Contains("Content=\"删除\" Command=\"{Binding DataContext.RemoveImportedLogFileCommand", StringComparison.Ordinal)
        && xaml.Contains("Content=\"分析列表日志\"", StringComparison.Ordinal)
        && xaml.Contains("IsEnabled=\"{Binding HasImportedLogFiles}\"", StringComparison.Ordinal)
        && xaml.Contains("循环日志会按全部 SID 分轮解析", StringComparison.Ordinal)
        && viewModel.Contains("Directory.EnumerateFiles(LogDirectory, \"*.log\", SearchOption.TopDirectoryOnly)", StringComparison.Ordinal)
        && viewModel.Contains("File.Copy(item.FilePath, Path.Combine(analysisInputDirectory, item.FileName));", StringComparison.Ordinal)
        && viewModel.Contains("new LogAnalysisRequest(OtaMode.EcoLink, LogAnalyzerExecutablePath, analysisInputDirectory, outputDirectory)", StringComparison.Ordinal),
        "日志目录导入后应显示可删除清单，分析器必须仅处理清单快照，并支持按全部 SID 解析循环日志。");
    Assert(viewModel.Contains("Rssi = node.Rssi > 0 ? (sbyte)-node.Rssi : node.Rssi;", StringComparison.Ordinal),
        "Node RSSI 应统一规范为 dBm 负值后再显示和参与门限判断。");
    Assert(xaml.Contains("Content=\"清空\" Command=\"{Binding ClearGlobalLogCommand}\"", StringComparison.Ordinal)
        && xaml.Contains("仅保留本次运行最近 300 行", StringComparison.Ordinal)
        && viewModel.Contains("ClearGlobalLogCommand = new RelayCommand(_ => GlobalLogText = string.Empty);", StringComparison.Ordinal),
        "全局日志应明确仅为运行时缓存，并提供紧凑的清空入口。");
    Assert(viewModel.Contains("if (SetProperty(ref _forwardPatchName, value)) ScheduleSettingsAutoSave();", StringComparison.Ordinal)
        && viewModel.Contains("if (SetProperty(ref _reversePatchName, value)) ScheduleSettingsAutoSave();", StringComparison.Ordinal),
        "正反向 Patch 名称修改后应自动持久化。");
    Assert(viewModel.Contains("public DeviceType? OtaDeviceType { get; }", StringComparison.Ordinal)
        && viewModel.Contains("item.OtaDeviceType == selectedDeviceType", StringComparison.Ordinal)
        && viewModel.Contains("ApplyManifestDetails(manifest, updateTaskType: false);", StringComparison.Ordinal)
        && viewModel.Contains("当前没有适用于“{SelectedTaskType}”的 Patch", StringComparison.Ordinal),
        "升级类型应保留用户选择，只展示设备类型匹配的 Patch，并在没有匹配项时明确提示。");
    Assert(viewModel.Contains("MatchesPatchDirection(previousForwardPatch, reverse: false)", StringComparison.Ordinal)
        && viewModel.Contains("MatchesPatchDirection(item, reverse: true)", StringComparison.Ordinal)
        && viewModel.Contains("public byte? OldVersion { get; }", StringComparison.Ordinal)
        && viewModel.Contains("public byte? NewVersion { get; }", StringComparison.Ordinal),
        "正向和反向 Patch 的默认选择必须按照 Manifest 版本方向匹配，不能依赖文件名或集合顺序。");
    Assert(viewModel.Contains("var expectedType = (byte)FirmwareDeviceType.ExtenderS;", StringComparison.Ordinal)
        && !viewModel.Contains("deviceType == DeviceType.Sync ? (byte)2 : (byte)1", StringComparison.Ordinal)
        && viewModel.Contains("DiscoverAsyncVersionsAsync", StringComparison.Ordinal)
        && viewModel.Contains("item.GetSoftwareVersion(deviceType) != manifest.OldVersion", StringComparison.Ordinal)
        && viewModel.Contains("task.DeviceType is DeviceType.Sync or DeviceType.Async", StringComparison.Ordinal),
        "同步和异步升级必须共用 ExtenderS 承载板，但分别查询、校验并更新各自 MCU 的软件版本。");
    Assert(viewModel.Contains("SelectedTaskType == GatewayTaskType", StringComparison.Ordinal)
        && viewModel.Contains("QueryGatewayBasicInfoAsync(gatewayId.ToString())", StringComparison.Ordinal)
        && viewModel.Contains("ValidateGatewayVersionBeforeUpgrade(task)", StringComparison.Ordinal)
        && viewModel.Contains("请先点击“刷新 Gateway”查询当前软件版本。", StringComparison.Ordinal)
        && !viewModel.Contains("ValidateGatewayVersionBeforeUpgradeAsync", StringComparison.Ordinal),
        "Gateway 应与其他设备一样手动刷新版本，启动时只校验缓存结果，不能自动查询。");
}

static void VerifyUpdateWindowBindings()
{
    var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "UpdateWindow.xaml"));
    Assert(xaml.Contains("Value=\"{Binding ProgressPercent, Mode=OneWay}\"", StringComparison.Ordinal)
        && !xaml.Contains("Value=\"{Binding ProgressPercent}\"", StringComparison.Ordinal),
        "更新窗口的只读进度属性必须使用 OneWay 绑定，避免打开更新界面时触发 TwoWay 写入异常。");
}

static void VerifyUpgradeQualityAssessment()
{
    using var document = JsonDocument.Parse("""
        {
          "conclusions": {
            "device_upgrade_success": true,
            "parent_task_success": true,
            "overall_success": true
          },
          "counts": {
            "target": 5,
            "ready": 5,
            "boot_report": 5,
            "node_finished": 5,
            "aggregated_finished": 5
          },
          "maintenance": {
            "completed_count": 55,
            "latency_ms": { "p95": 705 }
          },
          "retries": { "maintenance_repeat": 47 },
          "sync_frame_timing": {
            "tx_failure_events": 1,
            "inferred_missed_frames": 14
          },
          "node_link_summary": { "weak_link_node_ids": ["0x03D5"] }
        }
        """);
    var assessment = OtaUpgradeQualityEvaluator.Evaluate(document.RootElement);
    Assert(assessment.Score == 86 && assessment.Grade == "良好",
        "升级质量评分未按闭环、完成度、可靠性和时延规则计算。");
    Assert(assessment.Details.Contains("闭环完整性  50/50", StringComparison.Ordinal)
        && assessment.Details.Contains("传输可靠性  9/20", StringComparison.Ordinal),
        "升级质量评估缺少可解释的评分明细。");

    using var cycleDocument = JsonDocument.Parse("""
        {
          "cycle": {
            "session_count": 2,
            "successful_session_count": 1
          },
          "sessions": [
            { "conclusions": { "device_upgrade_success": true, "parent_task_success": true } },
            { "conclusions": { "device_upgrade_success": false, "parent_task_success": false } }
          ],
          "conclusions": {
            "device_upgrade_success": false,
            "parent_task_success": false,
            "overall_success": false
          },
          "counts": {
            "target": 4,
            "ready": 3,
            "boot_report": 3,
            "node_finished": 3,
            "aggregated_finished": 3
          },
          "maintenance": {
            "completed_count": 2,
            "latency_ms": { "p95": 800 }
          },
          "retries": { "maintenance_repeat": 1 },
          "sync_frame_timing": {
            "tx_failure_events": 1,
            "inferred_missed_frames": 0
          },
          "node_link_summary": { "weak_link_node_ids": ["0x0002"] }
        }
        """);
    var cycleAssessment = OtaUpgradeQualityEvaluator.Evaluate(cycleDocument.RootElement);
    Assert(cycleAssessment.ClosedLoopScore == 26
        && cycleAssessment.Summary.Contains("2 次中有 1 次未闭环", StringComparison.Ordinal)
        && cycleAssessment.Details.Contains("循环日志共识别 2 次单次升级", StringComparison.Ordinal),
        "循环日志质量评估应按每个 SID 的闭环结果计分并显示轮次汇总。");
}

static void VerifyPatchCenterTitleCapitalization()
{
    var mainWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
    var patchPage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "PatchPage.xaml"));
    Assert(mainWindow.Contains("CommandParameter=\"PATCH 中心\"", StringComparison.Ordinal)
        && patchPage.Contains("Text=\"Patch 制作\"", StringComparison.Ordinal)
        && patchPage.Contains("Text=\"Patch 发布\"", StringComparison.Ordinal)
        && !patchPage.Contains("Text=\"PATCH ", StringComparison.Ordinal)
        && !patchPage.Contains("Content=\"PATCH ", StringComparison.Ordinal),
        "仅 PATCH 中心标题应全大写，页面内 Patch 文案应保留首字母大写。");
    Assert(System.Text.RegularExpressions.Regex.Matches(
            mainWindow,
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"\\*\" /><ColumnDefinition Width=\"16\" /><ColumnDefinition Width=\"\\*\" /></Grid.ColumnDefinitions>").Count >= 5
        && patchPage.Contains("<ColumnDefinition Width=\"16\" />", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Matches(patchPage, "<ColumnDefinition Width=\"\\*\" />").Count >= 2,
        "所有双栏页面应使用相同的等宽列和 16 像素间距。");
}

static void VerifyMqttConfigurationTabs()
{
    var assetDirectory = Path.Combine(AppContext.BaseDirectory, "TestAssets");
    var mainWindow = File.ReadAllText(Path.Combine(assetDirectory, "MainWindow.xaml"));
    var mainViewModel = File.ReadAllText(Path.Combine(assetDirectory, "MainWindowViewModel.cs"));
    Assert(mainWindow.Contains("GroupName=\"MqttConfigurationTabs\"", StringComparison.Ordinal)
        && mainWindow.Contains("IsChecked=\"{Binding MqttClientUsesExternalBroker, Mode=OneWay}\"", StringComparison.Ordinal)
        && mainWindow.Contains("IsChecked=\"{Binding MqttClientUsesLocalBroker, Mode=OneWay}\"", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Matches(mainWindow, "Command=\"{Binding SelectMqttConfigurationCommand}\"").Count == 2
        && mainWindow.Contains("Visibility=\"{Binding MqttExternalConfigurationVisibility}\"", StringComparison.Ordinal)
        && mainWindow.Contains("Visibility=\"{Binding MqttLocalConfigurationVisibility}\"", StringComparison.Ordinal)
        && mainWindow.Contains("<Grid MinHeight=\"440\">", StringComparison.Ordinal)
        && mainWindow.Contains("Margin=\"16,8,16,16\" Padding=\"12\"", StringComparison.Ordinal)
        && !mainWindow.Contains("<TabItem Header=\"公网 MQTT 配置\">", StringComparison.Ordinal),
        "MQTT 本地/公网配置应使用统一的分段 Tab 样式。");
    Assert(mainViewModel.Contains("SelectMqttConfigurationCommand = new RelayCommand(SelectMqttConfiguration);", StringComparison.Ordinal)
        && mainViewModel.Contains("MqttClientUsesLocalBroker = string.Equals(selection, \"Local\", StringComparison.Ordinal);", StringComparison.Ordinal)
        && mainViewModel.Contains("public bool MqttClientUsesExternalBroker => !_mqttClientUsesLocalBroker;", StringComparison.Ordinal)
        && !mainViewModel.Contains("else if (!_mqttClientUsesLocalBroker)", StringComparison.Ordinal)
        && !mainViewModel.Contains("else if (!_httpUsesLocalServer)", StringComparison.Ordinal)
        && !mainViewModel.Contains("else if (!_isEcoLink)", StringComparison.Ordinal)
        && !mainViewModel.Contains("else if (!_isSpecifiedTarget)", StringComparison.Ordinal),
        "MQTT 分段 RadioButton 必须使用单向状态绑定和显式命令，避免启动时反向写入并覆盖持久化状态。");
    Assert(mainViewModel.Contains("private const string GatewayTaskType = \"网关升级\";", StringComparison.Ordinal)
        && mainViewModel.Contains("private const string SyncTaskType = \"拓展器-同步升级\";", StringComparison.Ordinal)
        && mainViewModel.Contains("private const string AsyncTaskType = \"拓展器-异步升级\";", StringComparison.Ordinal)
        && mainViewModel.Contains("private const string NodeTaskType = \"节点升级\";", StringComparison.Ordinal)
        && mainViewModel.Contains("NormalizeTaskType", StringComparison.Ordinal),
        "升级类型应显示统一中文名称，并兼容旧设置中的类型名称。");
}

static void VerifyNodeTypePresentation()
{
    var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
    var viewModel = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindowViewModel.cs"));
    Assert(xaml.Contains("ItemsSource=\"{Binding NodeTypeOptions}\"", StringComparison.Ordinal)
        && xaml.Contains("DisplayMemberPath=\"DisplayName\"", StringComparison.Ordinal)
        && xaml.Contains("SelectedItem=\"{Binding SelectedNodeTypeOption, Mode=TwoWay}\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"筛选 Node ID\"", StringComparison.Ordinal)
        && xaml.Contains("Text=\"{Binding NodeTypeDisplay, Mode=OneWay}\"", StringComparison.Ordinal)
        && xaml.Contains("Header=\"添加 Node 类型\"", StringComparison.Ordinal)
        && xaml.Contains("Command=\"{Binding AddNodeTypeCommand}\"", StringComparison.Ordinal)
        && !xaml.Contains("Node 类型（十进制", StringComparison.Ordinal),
        "Node 类型应支持添加名称（数字）选项，并在发现列表显示具体名称。");
    Assert(xaml.Contains("Text=\"批量选择 Node 类型\"", StringComparison.Ordinal)
        && viewModel.Contains("new(0, \"不选择\")", StringComparison.Ordinal)
        && viewModel.Contains("ClearNodeSelection();", StringComparison.Ordinal)
        && viewModel.Contains("node.IsSelected = node.NodeType == nodeType && node.CanSelect;", StringComparison.Ordinal),
        "Node 类型选择应批量标记同类型节点，并提供不选择项用于取消全部选择。");
    var restoredNodeGroups = viewModel.IndexOf("foreach (var group in workspace.DiscoveredNodeGroups ?? [])", StringComparison.Ordinal);
    var restoredNodeTypeOptions = viewModel.IndexOf("RefreshNodeTypeOptions();", restoredNodeGroups, StringComparison.Ordinal);
    var restoredNodeEligibility = viewModel.IndexOf("RefreshNodeEligibility();", restoredNodeGroups, StringComparison.Ordinal);
    var restoredNodeSelection = viewModel.IndexOf("SelectNodesByType(_selectedNodeTypeValue);", restoredNodeEligibility, StringComparison.Ordinal);
    Assert(restoredNodeGroups >= 0
        && restoredNodeTypeOptions > restoredNodeGroups
        && restoredNodeEligibility > restoredNodeTypeOptions
        && restoredNodeSelection > restoredNodeEligibility,
        "恢复持久化 Node 列表后必须先重建类型筛选项、刷新节点资格，再恢复所选类型的勾选状态。");
}

static void VerifyGeneratedMetadataPresentation()
{
    var assetDirectory = Path.Combine(AppContext.BaseDirectory, "TestAssets");
    var mainWindow = File.ReadAllText(Path.Combine(assetDirectory, "MainWindow.xaml"));
    var patchPage = File.ReadAllText(Path.Combine(assetDirectory, "PatchPage.xaml"));

    Assert(patchPage.Contains("Text=\"{Binding ForwardPatchName, Mode=OneWay}\"", StringComparison.Ordinal)
        && patchPage.Contains("Text=\"{Binding ReversePatchName, Mode=OneWay}\"", StringComparison.Ordinal)
        && !patchPage.Contains("IsReadOnly=\"True\" Text=\"{Binding ForwardPatchName}", StringComparison.Ordinal)
        && !patchPage.Contains("IsReadOnly=\"True\" Text=\"{Binding ReversePatchName}", StringComparison.Ordinal),
        "自动生成的 Patch 名称应使用只读文本展示，不应继续使用编辑框控件。");
    Assert(mainWindow.Contains("ToolTip=\"由所选 Patch 自动识别\"", StringComparison.Ordinal)
        && mainWindow.Contains("Text=\"{Binding OldVersion, Mode=OneWay}\"", StringComparison.Ordinal)
        && mainWindow.Contains("Text=\"{Binding NewVersion, Mode=OneWay}\"", StringComparison.Ordinal)
        && !mainWindow.Contains("Text=\"{Binding OldVersion, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal)
        && !mainWindow.Contains("Text=\"{Binding NewVersion, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal),
        "升级任务的新旧版本应由所选 Patch 自动识别并以只读文本展示。");
}

static async Task VerifyPatchAndTaskRulesAsync(string workspace)
{
    var patchPath = Path.Combine(workspace, "sample.patch");
    var bytes = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
    await File.WriteAllBytesAsync(patchPath, bytes);

    var metadata = await PatchMetadata.FromFileAsync(patchPath);
    Assert(metadata.Length == bytes.Length, "差分包长度读取错误。");
    Assert(metadata.Md5.Length == 32 && metadata.Sha256.Length == 64, "差分包哈希读取错误。");
    Assert(PatchCapacityPolicy.Check(DeviceType.Node, 0xD000).IsAllowed, "Node 容量上限应允许。");
    Assert(!PatchCapacityPolicy.Check(DeviceType.Node, 0xD001).IsAllowed, "Node 超出容量上限应拒绝。");
    Assert(PatchCapacityPolicy.Check(DeviceType.Sync, 0xD000).IsAllowed, "Sync 容量上限应为 52 KiB。");
    Assert(!PatchCapacityPolicy.Check(DeviceType.Sync, 0xD001).IsAllowed, "Sync 超出 52 KiB 应拒绝。");
    Assert(PatchCapacityPolicy.Check(DeviceType.Gateway, 0x200000).IsAllowed, "Gateway 容量上限应为 2 MiB。");
    Assert(!PatchCapacityPolicy.Check(DeviceType.Gateway, 0x200001).IsAllowed, "Gateway 超出 2 MiB 应拒绝。");

    var profile = new TraditionalProtocolProfile();
    var validTask = new OtaTask
    {
        Mode = OtaMode.Traditional,
        DeviceType = DeviceType.Gateway,
        Target = OtaTaskTarget.Specified("10010001"),
        OldVersion = "1",
        NewVersion = "2",
        PatchPath = patchPath,
    };
    Assert(OtaTaskValidator.Validate(validTask, profile).IsValid, "传统 Gateway 定向任务应合法。");

    var invalidTask = new OtaTask
    {
        Mode = validTask.Mode,
        DeviceType = DeviceType.Node,
        Target = validTask.Target,
        OldVersion = validTask.OldVersion,
        NewVersion = validTask.NewVersion,
        PatchPath = validTask.PatchPath,
    };
    Assert(!OtaTaskValidator.Validate(invalidTask, profile).IsValid, "传统模式必须拒绝 Node 任务。");

    OtaTask CreateEcoNodeTask(IReadOnlyList<OtaExtenderTarget> targets) => new()
    {
        Mode = OtaMode.EcoLink,
        DeviceType = DeviceType.Node,
        GatewayId = "704027",
        Target = OtaTaskTarget.Specified(targets.SelectMany(target => target.NodeIds).ToArray()),
        ExtenderTargets = targets,
        NodeType = 2,
        OldVersion = "1",
        NewVersion = "2",
        PatchPath = patchPath,
        PatchUrl = "http://127.0.0.1:8080/sample.patch",
        PatchMd5 = metadata.Md5,
    };

    var nodeTask = CreateEcoNodeTask([new OtaExtenderTarget("10010001", ["1", "2", "3"])]);
    Assert(OtaTaskValidator.Validate(nodeTask, new EcoLinkProtocolProfile()).IsValid, "EcoLink Node 任务应合法。");
    var repeatedAcrossExtenders = CreateEcoNodeTask([
        new OtaExtenderTarget("10010001", ["1"]),
        new OtaExtenderTarget("10010002", ["1"]),
    ]);
    Assert(OtaTaskValidator.Validate(repeatedAcrossExtenders, new EcoLinkProtocolProfile()).IsValid,
        "相同 Node ID 位于不同 Extender 时应允许。");
    var repeatedWithinExtender = CreateEcoNodeTask([
        new OtaExtenderTarget("10010001", ["1", "1"]),
    ]);
    Assert(!OtaTaskValidator.Validate(repeatedWithinExtender, new EcoLinkProtocolProfile()).IsValid,
        "同一 Extender 内重复 Node ID 应拒绝。");
    var nodes256 = Enumerable.Range(1, 256).Select(value => value.ToString()).ToArray();
    var nodes257 = Enumerable.Range(1, 257).Select(value => value.ToString()).ToArray();
    Assert(OtaTaskValidator.Validate(
        CreateEcoNodeTask([new OtaExtenderTarget("10010001", nodes256)]),
        new EcoLinkProtocolProfile()).IsValid,
        "Node OTA 应允许 256 个目标。");
    Assert(!OtaTaskValidator.Validate(
        CreateEcoNodeTask([new OtaExtenderTarget("10010001", nodes257)]),
        new EcoLinkProtocolProfile()).IsValid,
        "Node OTA 应拒绝超过 256 个目标。");
    var nodeRequest = OtaMessageCodec.CreateUpgradeRequest(nodeTask, 2);
    Assert(nodeRequest.JsonPayload.Contains("\"node_type\":2", StringComparison.Ordinal) && nodeRequest.JsonPayload.Contains("\"nodes\":[1,2,3]", StringComparison.Ordinal), "Node 任务编码错误。");
    Assert(OtaMessageCodec.ToProtocolDeviceType(DeviceType.Sync) == "iote" && OtaMessageCodec.ToProtocolDeviceType(DeviceType.Async) == "ex_mcu", "Gateway OTA 设备类型映射错误。");
}

static async Task VerifySettingsPersistenceAsync(string workspace)
{
    var settingsPath = Path.Combine(workspace, "settings.json");
    var store = new JsonSettingsStore(settingsPath);
    var expected = new AppSettings
    {
        MqttHost = "broker.example", MqttPort = 1884, HttpRootDirectory = workspace, HttpPort = 9080,
        MqttUseTls = true, MqttUserName = "tester", MqttClientUsesLocalBroker = false, LocalBrokerPort = 1885, LocalBrokerUserName = "local-user",
        HttpUsesLocalServer = false, PublicHttpBaseUrl = "https://files.example/ota/", SftpHost = "sftp.example", SftpPort = 2222,
        SftpPrivateKeyPath = "D:\\keys\\ota", LogDirectory = "D:\\logs",
        ForwardPatchName = "node-v1-to-v2.patch", ReversePatchName = "node-v2-to-v1.patch",
        CycleIntervalMode = "随机间隔", CycleRandomMinimumSeconds = 3, CycleRandomMaximumSeconds = 9,
        GatewayIdHistory = ["704027", "704065"],
        CustomNodeTypes = [new NodeTypeDefinitionSettings(9, "烟感")],
        DiscoveredExtenders = [new DiscoveredExtenderSettings(1821385, "0x8000011c", 2, 1, true)],
        DiscoveredNodeGroups =
        [
            new DiscoveredNodeGroupSettings(
                1821385,
                [new DiscoveredNodeSettings(53936, 4, 1, -58, true)],
                string.Empty),
        ],
        ActiveMode = "Traditional",
        ModeWorkspaces = new Dictionary<string, ModeWorkspaceSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["EcoLink"] = new()
            {
                SelectedPageName = "升级任务",
                SelectedTaskType = "节点升级",
                MqttClientUsesLocalBroker = false,
                GatewayId = "eco-gateway",
                GatewayIdHistory = ["704027", "704065"],
                NodeType = 9,
            },
            ["Traditional"] = new()
            {
                SelectedPageName = "PATCH 中心",
                SelectedTaskType = "拓展器-同步升级",
                GatewayId = "traditional-gateway",
                GatewayIdHistory = ["800001"],
            },
        },
    };
    await store.SaveAsync(expected);
    var actual = await store.LoadAsync();
    var synchronous = store.Load();
    Assert(actual.MqttHost == expected.MqttHost && actual.MqttPort == expected.MqttPort && actual.HttpPort == expected.HttpPort
        && actual.MqttUseTls && actual.MqttUserName == expected.MqttUserName && !actual.MqttClientUsesLocalBroker
        && actual.LocalBrokerPort == expected.LocalBrokerPort && actual.LocalBrokerUserName == expected.LocalBrokerUserName
        && !actual.HttpUsesLocalServer && actual.PublicHttpBaseUrl == expected.PublicHttpBaseUrl && actual.SftpPort == expected.SftpPort
        && actual.SftpPrivateKeyPath == expected.SftpPrivateKeyPath && actual.LogDirectory == expected.LogDirectory
        && actual.ForwardPatchName == expected.ForwardPatchName && actual.ReversePatchName == expected.ReversePatchName
        && actual.CycleIntervalMode == expected.CycleIntervalMode
        && actual.CycleRandomMinimumSeconds == expected.CycleRandomMinimumSeconds
        && actual.CycleRandomMaximumSeconds == expected.CycleRandomMaximumSeconds
        && actual.GatewayIdHistory.SequenceEqual(expected.GatewayIdHistory)
        && actual.CustomNodeTypes.Count == 1 && actual.CustomNodeTypes[0] == new NodeTypeDefinitionSettings(9, "烟感")
        && actual.DiscoveredExtenders.Count == 1 && actual.DiscoveredExtenders[0].ExtenderId == 1821385
        && actual.DiscoveredExtenders[0].IsSelected
        && actual.DiscoveredNodeGroups.Count == 1 && actual.DiscoveredNodeGroups[0].Nodes.Count == 1
        && actual.DiscoveredNodeGroups[0].Nodes[0].NodeId == 53936
        && actual.DiscoveredNodeGroups[0].Nodes[0].NodeType == 4
        && actual.DiscoveredNodeGroups[0].Nodes[0].IsSelected
        && actual.ActiveMode == "Traditional"
        && actual.ModeWorkspaces.Count == 2
        && actual.ModeWorkspaces["EcoLink"].SelectedPageName == "升级任务"
        && actual.ModeWorkspaces["EcoLink"].SelectedTaskType == "节点升级"
        && actual.ModeWorkspaces["EcoLink"].NodeType == 9
        && actual.ModeWorkspaces["EcoLink"].GatewayIdHistory.SequenceEqual(["704027", "704065"])
        && actual.ModeWorkspaces["Traditional"].GatewayId == "traditional-gateway",
        "JSON 设置或设备发现结果持久化错误。");
    Assert(synchronous.ActiveMode == expected.ActiveMode
        && !synchronous.ModeWorkspaces["EcoLink"].MqttClientUsesLocalBroker,
        "启动窗口同步预载设置失败。");
    var migrated = ModeWorkspaceSettings.FromLegacy(expected);
    Assert(migrated.MqttHost == expected.MqttHost
        && migrated.SelectedTaskType == expected.SelectedTaskType
        && migrated.GatewayIdHistory.SequenceEqual(expected.GatewayIdHistory)
        && migrated.CustomNodeTypes.Count == 1
        && migrated.DiscoveredNodeGroups.Count == 1,
        "旧版顶层设置迁移到模式工作区失败。");
}

static async Task VerifyHttpRangeServerAsync(string workspace)
{
    var root = Path.Combine(workspace, "http-root");
    Directory.CreateDirectory(root);
    var payload = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
    await File.WriteAllBytesAsync(Path.Combine(root, "firmware.bin"), payload);

    await using var server = new HttpRangeServer();
    var port = GetFreeTcpPort();
    await server.StartAsync(new HttpRangeServerOptions(root, port));
    using var client = new HttpClient { BaseAddress = server.BaseAddress };

    var fullResponse = await client.GetAsync("firmware.bin");
    Assert(fullResponse.StatusCode == HttpStatusCode.OK, "完整 GET 应返回 200。");
    Assert((await fullResponse.Content.ReadAsByteArrayAsync()).SequenceEqual(payload), "完整 GET 内容错误。");

    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "firmware.bin");
    rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
    var rangeResponse = await client.SendAsync(rangeRequest);
    Assert(rangeResponse.StatusCode == HttpStatusCode.PartialContent, "Range GET 应返回 206。");
    Assert(rangeResponse.Content.Headers.ContentRange?.ToString() == "bytes 2-5/10", "Content-Range 错误。");
    Assert((await rangeResponse.Content.ReadAsByteArrayAsync()).SequenceEqual(payload[2..6]), "Range GET 内容错误。");

    using var headRequest = new HttpRequestMessage(HttpMethod.Head, "firmware.bin");
    var headResponse = await client.SendAsync(headRequest);
    Assert(headResponse.StatusCode == HttpStatusCode.OK && headResponse.Content.Headers.ContentLength == payload.Length, "HEAD 响应错误。");
    var md5 = (await PatchMetadata.FromFileAsync(Path.Combine(root, "firmware.bin"))).Md5;
    Assert((await HttpFileVerifier.VerifyAsync(new Uri(server.BaseAddress!, "firmware.bin"), payload.Length, md5, verifyFullMd5: true)).IsSuccess, "HTTP 文件完整性验证失败。");

    using var transientClient = new HttpClient(new TransientPatchHttpHandler(payload));
    var transientResult = await HttpFileVerifier.VerifyAsync(
        transientClient,
        new Uri("http://ota.test/firmware.bin"),
        payload.Length,
        md5,
        verifyFullMd5: true);
    Assert(transientResult.IsSuccess, "HTTP 文件校验应自动重试临时 503 响应。");
}

static async Task VerifyProtocolCodecAndRunnerAsync(string workspace)
{
    var patchPath = Path.Combine(workspace, "runner.patch");
    await File.WriteAllBytesAsync(patchPath, Enumerable.Repeat((byte)0x5A, 64).ToArray());
    var metadata = await PatchMetadata.FromFileAsync(patchPath);
    var task = new OtaTask
    {
        Mode = OtaMode.Traditional,
        DeviceType = DeviceType.Gateway,
        GatewayId = "704027",
        Target = OtaTaskTarget.Broadcast(),
        OldVersion = "1",
        NewVersion = "2",
        PatchPath = patchPath,
        PatchUrl = "http://127.0.0.1:8080/runner.patch",
        PatchMd5 = metadata.Md5,
    };
    var request = OtaMessageCodec.CreateUpgradeRequest(task, 1001);
    var cancelRequest = OtaMessageCodec.CreateCancelRequest(task, 1004);
    var basicInfoQuery = OtaMessageCodec.CreateGatewayBasicInfoQuery(task.GatewayId, 1002);
    Assert(basicInfoQuery.Topic == "ucchip/down/sgw/704027/1002", "Basic info query topic is invalid.");
    Assert(basicInfoQuery.JsonPayload.Contains("\"cmd\":3", StringComparison.Ordinal)
        && basicInfoQuery.JsonPayload.Contains("\"query\":\"base\"", StringComparison.Ordinal), "Basic info query payload is invalid.");
    Assert(request.Topic == "ucchip/down/sgw/704027/1001", "升级请求 Topic 错误。");
    Assert(request.JsonPayload.Contains("\"cmd\":5", StringComparison.Ordinal), "升级请求 cmd 错误。");
    Assert(cancelRequest.JsonPayload.Contains("\"cmd\":5", StringComparison.Ordinal)
        && cancelRequest.JsonPayload.Contains("\"active\":0", StringComparison.Ordinal),
        "取消请求应使用 cmd=5 active=0。");
    Assert(!request.JsonPayload.Contains("targets", StringComparison.Ordinal), "广播升级不得发送目标 ID。 ");
    var statusQuery = OtaMessageCodec.CreateStatusQuery(task.GatewayId, 1003, 1001, 0);
    Assert(statusQuery.JsonPayload.Contains("\"query_seq\":1003", StringComparison.Ordinal), "状态查询未携带 query_seq。");
    Assert(OtaMessageCodec.TryParseGatewayFinalResult("{\"cmd\":6,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"gateway\",\"prompt\":\"upgrade process has end!\"}", out var final) && final!.IsSuccess, "最终结果消息解析错误。");
    const string statusJson = "{\"cmd\":9,\"ota_status\":{\"query_seq\":2,\"task_seq\":1001,\"session_id\":4000000001,\"result\":\"OK\",\"status\":\"RUNNING\",\"stage\":\"TRANSFER\",\"task_elapsed_ms\":1234,\"stages\":[{\"stage\":\"TRANSFER\",\"state\":\"RUNNING\",\"start_offset_ms\":100,\"duration_ms\":1134,\"reason\":\"\"}],\"subtasks\":[{\"extender_id\":704028,\"stage\":\"TRANSFER\",\"result\":\"RUNNING\",\"elapsed_ms\":1234,\"target_count\":2,\"prepared_count\":1,\"success_count\":1,\"failed_count\":0,\"reason\":\"\"}]}}";
    Assert(OtaMessageCodec.TryParseGatewayStatus(statusJson, out var status)
        && status!.TaskSequence == 1001
        && status.SessionId == 4000000001U
        && status.TaskElapsedMs == 1234
        && status.Stages.Count == 1
        && status.Subtasks.Count == 1
        && status.Subtasks[0].PreparedCount == 1, "状态响应阶段、准备计数或子任务解析错误。");

    await using var fakeMqtt = new FakeMqttTransport();
    await using var runner = new OtaTaskRunner(fakeMqtt, new TraditionalProtocolProfile(), new InMemoryTaskSequenceStore());
    OtaExecutionUpdate? completion = null;
    runner.Updated += (_, update) => { if (update.State == OtaTaskState.Succeeded) completion = update; };
    var start = await runner.StartAsync(task);
    Assert(start.State == OtaTaskState.Running && fakeMqtt.Published.Count == 1, "任务运行器未发送升级请求。");
    fakeMqtt.Inject("ucchip/up/sgw/704027/1", "{\"cmd\":6,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"gateway\",\"prompt\":\"upgrade process has end!\"}");
    await Task.Delay(10);
    Assert(completion is not null && !runner.HasActiveTask, "传统模式未按最终结果结束任务。");

    var ecoTask = new OtaTask
    {
        Mode = OtaMode.EcoLink,
        DeviceType = DeviceType.Gateway,
        GatewayId = task.GatewayId,
        Target = task.Target,
        OldVersion = task.OldVersion,
        NewVersion = task.NewVersion,
        PatchPath = task.PatchPath,
        PatchUrl = task.PatchUrl,
        PatchMd5 = task.PatchMd5,
    };
    await using var ecoMqtt = new FakeMqttTransport();
    await using var ecoRunner = new OtaTaskRunner(ecoMqtt, new EcoLinkProtocolProfile(), new InMemoryTaskSequenceStore(), pollingOptions: new OtaPollingOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1)));
    var ecoStart = await ecoRunner.StartAsync(ecoTask);
    Assert(ecoStart.State == OtaTaskState.Running, "EcoLink 任务未启动。");
    Assert(ecoRunner.PausePolling() && ecoRunner.IsPollingPaused, "EcoLink 状态轮询应可暂停。");
    Assert(ecoRunner.ResumePolling() && !ecoRunner.IsPollingPaused, "EcoLink 状态轮询应可恢复。");
    await WaitUntilAsync(() => ecoMqtt.Published.Count >= 2, TimeSpan.FromSeconds(1));
    var firstStatusQuery = ecoMqtt.Published[1];
    var firstStatusSequence = int.Parse(firstStatusQuery.Topic.Split('/')[^1]);
    ecoMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{firstStatusSequence},\"task_seq\":1,\"session_id\":7,\"result\":\"OK\",\"status\":\"RUNNING\",\"stage\":\"TRANSFER\"}}}}");
    await WaitUntilAsync(() => ecoMqtt.Published.Count >= 3, TimeSpan.FromSeconds(1));
    var boundStatusQuery = ecoMqtt.Published[2];
    var boundStatusSequence = int.Parse(boundStatusQuery.Topic.Split('/')[^1]);
    Assert(boundStatusQuery.GetPayloadAsUtf8().Contains("\"session_id\":7", StringComparison.Ordinal),
        "首次状态响应的真实 Session ID 未绑定到后续查询。");
    ecoMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{boundStatusSequence},\"task_seq\":1,\"session_id\":8,\"result\":\"OK\",\"status\":\"SUCCESS\",\"stage\":\"FINISHED\"}}}}");
    await Task.Delay(20);
    Assert(ecoRunner.HasActiveTask, "错误 Session ID 的状态响应不应结束任务。");
    ecoMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{boundStatusSequence},\"task_seq\":1,\"session_id\":7,\"result\":\"OK\",\"status\":\"SUCCESS\",\"stage\":\"FINISHED\"}}}}");
    await WaitUntilAsync(() => !ecoRunner.HasActiveTask, TimeSpan.FromSeconds(1));
    Assert(!ecoRunner.HasActiveTask, "EcoLink 模式未按状态轮询终态结束任务。");

    await using var cancelMqtt = new FakeMqttTransport();
    await using var cancelRunner = new OtaTaskRunner(
        cancelMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));
    Assert((await cancelRunner.StartAsync(ecoTask)).State == OtaTaskState.Running,
        "取消测试任务未启动。");
    Assert(cancelRunner.PausePolling(), "取消测试任务未能暂停轮询。");
    await cancelRunner.CancelAndNotifyGatewayAsync();
    Assert(!cancelRunner.HasActiveTask && cancelMqtt.Published.Count == 2,
        "取消任务后应立即释放活动任务并发送 Gateway 取消请求。");
    Assert(cancelMqtt.Published[^1].GetPayloadAsUtf8().Contains("\"active\":0", StringComparison.Ordinal),
        "Gateway 取消请求未携带 active=0。");

    await using var failedMqtt = new FakeMqttTransport();
    await using var failedRunner = new OtaTaskRunner(
        failedMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(1)));
    var failedUpdateObservedInactive = false;
    failedRunner.Updated += (_, update) =>
    {
        if (update.State == OtaTaskState.Failed)
        {
            failedUpdateObservedInactive = !failedRunner.HasActiveTask;
        }
    };
    Assert((await failedRunner.StartAsync(ecoTask)).State == OtaTaskState.Running, "失败终态测试任务未启动。");
    await WaitUntilAsync(() => failedMqtt.Published.Count >= 2, TimeSpan.FromSeconds(1));
    var failedStatusQuery = failedMqtt.Published[1];
    var failedStatusSequence = int.Parse(failedStatusQuery.Topic.Split('/')[^1]);
    failedMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{failedStatusSequence},\"task_seq\":1,\"session_id\":9,\"result\":\"OK\",\"status\":\"FAILED\",\"stage\":\"REPAIR\"}}}}");
    await WaitUntilAsync(() => !failedRunner.HasActiveTask, TimeSpan.FromSeconds(1));
    await WaitUntilAsync(() => failedMqtt.Published.Count >= 3, TimeSpan.FromSeconds(1));
    var publishedAfterFailure = failedMqtt.Published.Count;
    await Task.Delay(80);
    Assert(failedUpdateObservedInactive, "失败终态通知界面前应先释放活动任务。");
    Assert(failedMqtt.Published.Count == publishedAfterFailure, "失败终态后仍继续发送状态查询。");
    Assert(failedMqtt.Published[^1].GetPayloadAsUtf8().Contains("\"active\":0", StringComparison.Ordinal),
        "失败终态后应向 Gateway 发送 active=0 取消请求。");

    await using var subtaskFailedMqtt = new FakeMqttTransport();
    await using var subtaskFailedRunner = new OtaTaskRunner(
        subtaskFailedMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(1)));
    OtaExecutionUpdate? subtaskFailure = null;
    subtaskFailedRunner.Updated += (_, update) =>
    {
        if (update.State == OtaTaskState.Failed) subtaskFailure = update;
    };
    Assert((await subtaskFailedRunner.StartAsync(ecoTask)).State == OtaTaskState.Running,
        "子任务失败测试任务未启动。");
    await WaitUntilAsync(() => subtaskFailedMqtt.Published.Count >= 2, TimeSpan.FromSeconds(1));
    var subtaskStatusSequence = int.Parse(subtaskFailedMqtt.Published[1].Topic.Split('/')[^1]);
    subtaskFailedMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{subtaskStatusSequence},\"task_seq\":1,\"session_id\":10,\"result\":\"OK\",\"status\":\"RUNNING\",\"stage\":\"REPAIR\",\"subtasks\":[{{\"extender_id\":1821373,\"stage\":\"REPAIR\",\"result\":\"FAILED\",\"reason\":\"DOWNSTREAM_FAILED\"}}]}}}}");
    await WaitUntilAsync(() => !subtaskFailedRunner.HasActiveTask && subtaskFailedMqtt.Published.Count >= 3, TimeSpan.FromSeconds(1));
    Assert(subtaskFailure?.Message.Contains("已停止状态轮询", StringComparison.Ordinal) == true
        && subtaskFailedMqtt.Published[^1].GetPayloadAsUtf8().Contains("\"active\":0", StringComparison.Ordinal),
        "顶层仍为 RUNNING 时，明确的子任务失败也应停止轮询并取消 Gateway 任务。");

    await using var partialFailedMqtt = new FakeMqttTransport();
    await using var partialFailedRunner = new OtaTaskRunner(
        partialFailedMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(1)));
    var partialFailureStillRunning = false;
    OtaExecutionUpdate? aggregateFailure = null;
    partialFailedRunner.Updated += (_, update) =>
    {
        if (update.State == OtaTaskState.Running &&
            update.Message.Contains("等待全部 Extender 完成", StringComparison.Ordinal))
        {
            partialFailureStillRunning = true;
        }
        if (update.State == OtaTaskState.Failed) aggregateFailure = update;
    };
    Assert((await partialFailedRunner.StartAsync(ecoTask)).State == OtaTaskState.Running,
        "多 Extender 部分失败测试任务未启动。");
    await WaitUntilAsync(() => partialFailedMqtt.Published.Count >= 2, TimeSpan.FromSeconds(1));
    var partialStatusSequence = int.Parse(partialFailedMqtt.Published[1].Topic.Split('/')[^1]);
    partialFailedMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{partialStatusSequence},\"task_seq\":1,\"session_id\":11,\"result\":\"OK\",\"status\":\"RUNNING\",\"stage\":\"PROGRAM\",\"subtasks\":[{{\"extender_id\":1821373,\"stage\":\"PROGRAM\",\"result\":\"FAILED\",\"reason\":\"FLASH_WRITE_FAILED\"}},{{\"extender_id\":1821362,\"stage\":\"PROGRAM\",\"result\":\"RUNNING\",\"reason\":\"\"}}]}}}}");
    await WaitUntilAsync(() => partialFailureStillRunning && partialFailedMqtt.Published.Count >= 3, TimeSpan.FromSeconds(1));
    Assert(partialFailedRunner.HasActiveTask && aggregateFailure is null,
        "一个 Extender 失败但另一个仍运行时，不应提前结束整个升级任务。");
    var aggregateStatusSequence = int.Parse(partialFailedMqtt.Published[^1].Topic.Split('/')[^1]);
    partialFailedMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{aggregateStatusSequence},\"task_seq\":1,\"session_id\":11,\"result\":\"OK\",\"status\":\"RUNNING\",\"stage\":\"FINISHED\",\"subtasks\":[{{\"extender_id\":1821373,\"stage\":\"PROGRAM\",\"result\":\"FAILED\",\"reason\":\"FLASH_WRITE_FAILED\"}},{{\"extender_id\":1821362,\"stage\":\"FINISHED\",\"result\":\"SUCCESS\",\"reason\":\"\"}}]}}}}");
    await WaitUntilAsync(() => !partialFailedRunner.HasActiveTask && aggregateFailure is not null, TimeSpan.FromSeconds(1));
    Assert(aggregateFailure?.Message.Contains("Extender 1821373 子任务失败", StringComparison.Ordinal) == true,
        "所有 Extender 进入终态后，应汇总失败 Extender 并结束任务。");

    await using var finalFailedMqtt = new FakeMqttTransport();
    await using var finalFailedRunner = new OtaTaskRunner(
        finalFailedMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));
    Assert((await finalFailedRunner.StartAsync(ecoTask)).State == OtaTaskState.Running,
        "最终失败上报测试任务未启动。");
    finalFailedMqtt.Inject("ucchip/up/sgw/704027/1",
        "{\"cmd\":6,\"seq\":1,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"gateway\",\"prompt\":\"upgrade failed\"}");
    await WaitUntilAsync(() => !finalFailedRunner.HasActiveTask && finalFailedMqtt.Published.Count >= 2, TimeSpan.FromSeconds(1));
    Assert(finalFailedMqtt.Published[^1].GetPayloadAsUtf8().Contains("\"active\":0", StringComparison.Ordinal),
        "EcoLink 明确失败终态上报后不应继续轮询，应立即发送 Gateway 取消请求。");

    await using var delayedMqtt = new FakeMqttTransport();
    await using var delayedRunner = new OtaTaskRunner(
        delayedMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(10)));
    OtaExecutionUpdate? delayedTerminal = null;
    delayedRunner.Updated += (_, update) =>
    {
        if (update.State is OtaTaskState.Succeeded or OtaTaskState.Failed)
        {
            delayedTerminal = update;
        }
    };
    Assert((await delayedRunner.StartAsync(ecoTask)).State == OtaTaskState.Running, "延迟状态响应测试任务未启动。");
    await WaitUntilAsync(() => delayedMqtt.Published.Count >= 3, TimeSpan.FromSeconds(1));
    var delayedStatusQuery = delayedMqtt.Published[1];
    var delayedStatusSequence = int.Parse(delayedStatusQuery.Topic.Split('/')[^1]);
    delayedMqtt.Inject("ucchip/up/sgw/704027/1",
        $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{delayedStatusSequence},\"task_seq\":1,\"session_id\":11,\"result\":\"OK\",\"status\":\"SUCCESS\",\"stage\":\"FINISHED\"}}}}");
    await WaitUntilAsync(() => !delayedRunner.HasActiveTask, TimeSpan.FromSeconds(1));
    Assert(delayedTerminal?.State == OtaTaskState.Succeeded,
        "已超时查询的延迟状态响应应恢复任务并识别终态。");

    await using var silentMqtt = new FakeMqttTransport();
    await using var silentRunner = new OtaTaskRunner(
        silentMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(10)));
    OtaExecutionUpdate? silentFailure = null;
    OtaExecutionUpdate? silentWarning = null;
    silentRunner.Updated += (_, update) =>
    {
        if (update.State == OtaTaskState.Failed)
        {
            silentFailure = update;
        }
        if (update.Message.Contains("下游任务可能仍在运行", StringComparison.Ordinal))
        {
            silentWarning = update;
        }
    };
    Assert((await silentRunner.StartAsync(ecoTask)).State == OtaTaskState.Running, "无响应状态查询测试任务未启动。");
    await WaitUntilAsync(() => silentMqtt.Published.Count >= 5, TimeSpan.FromSeconds(1));
    Assert(silentRunner.HasActiveTask && silentFailure is null,
        "状态查询无响应不应直接判定下游升级失败。");
    Assert(silentWarning is not null,
        "连续三次状态查询无响应后应提示查询异常并继续轮询。");
    await silentRunner.CancelAsync();

    await using var missingMqtt = new FakeMqttTransport();
    await using var missingRunner = new OtaTaskRunner(
        missingMqtt,
        new EcoLinkProtocolProfile(),
        new InMemoryTaskSequenceStore(),
        pollingOptions: new OtaPollingOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1)));
    Assert((await missingRunner.StartAsync(ecoTask)).State == OtaTaskState.Running, "TASK_NOT_FOUND 测试任务未启动。");
    for (var attempt = 0; attempt < 3; attempt++)
    {
        await WaitUntilAsync(() => missingMqtt.Published.Count >= attempt + 2, TimeSpan.FromSeconds(1));
        var queryMessage = missingMqtt.Published[attempt + 1];
        var querySequence = int.Parse(queryMessage.Topic.Split('/')[^1]);
        missingMqtt.Inject("ucchip/up/sgw/704027/1",
            $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{querySequence},\"task_seq\":1,\"session_id\":0,\"result\":\"TASK_NOT_FOUND\",\"status\":\"UNKNOWN\",\"stage\":\"UNKNOWN\"}}}}");
    }
    await WaitUntilAsync(() => !missingRunner.HasActiveTask, TimeSpan.FromSeconds(1));
    Assert(!missingRunner.HasActiveTask, "连续三次 TASK_NOT_FOUND 后应停止轮询。");

    var timeoutTask = new OtaTask
    {
        Mode = OtaMode.Traditional,
        DeviceType = DeviceType.Gateway,
        GatewayId = task.GatewayId,
        Target = task.Target,
        OldVersion = task.OldVersion,
        NewVersion = task.NewVersion,
        PatchPath = task.PatchPath,
        PatchUrl = task.PatchUrl,
        PatchMd5 = task.PatchMd5,
        Timeout = TimeSpan.FromMilliseconds(50),
    };
    await using var timeoutMqtt = new FakeMqttTransport();
    await using var timeoutRunner = new OtaTaskRunner(timeoutMqtt, new TraditionalProtocolProfile(), new InMemoryTaskSequenceStore());
    OtaExecutionUpdate? timeout = null;
    timeoutRunner.Updated += (_, update) => { if (update.State == OtaTaskState.TimedOut) timeout = update; };
    Assert((await timeoutRunner.StartAsync(timeoutTask)).State == OtaTaskState.Running, "超时任务未启动。");
    await Task.Delay(120);
    Assert(timeout is not null && !timeoutRunner.HasActiveTask, "未收到最终结果时任务应自动超时。 ");
}

static async Task VerifyDeviceDiscoveryAsync()
{
    Assert(OtaMessageCodec.TryParseGatewayAuthListPage(
        "{\"cmd\":3,\"auth_num\":0}",
        out var emptyExtenders) && emptyExtenders.Count == 0,
        "Extender 空列表解析错误。");
    Assert(OtaMessageCodec.TryParseGatewayAuthListPage(
            "{\"cmd\":3,\"auth_num\":1,\"101\":{\"detail\":\"legacy\"}}",
            out var legacyExtenders) && legacyExtenders.Count == 1 &&
            legacyExtenders[0].DeviceType == 0 && legacyExtenders[0].SoftwareVersion == 0,
        "缺少智能化字段的鉴权列表应继续用于发现，类型和版本按未知处理。");
    Assert(OtaMessageCodec.TryParseGatewayBasicInfo(
            "{\"cmd\":3,\"src\":704027,\"base\":{\"dev_id\":704027,\"ota_software_version\":2,\"sw_ver\":\"v1.3.1\"}}",
            out var gatewayInfo) && gatewayInfo is not null &&
            gatewayInfo.GatewayId == 704027 && gatewayInfo.SoftwareVersion == 2,
        "Gateway 基础信息中的 OTA 软件版本解析错误。");
    Assert(OtaMessageCodec.TryParseGatewayBasicInfo(
            "{\"cmd\":3,\"base\":{\"dev_id\":704027,\"sw_ver\":\"v2\"}}",
            out gatewayInfo) && gatewayInfo?.SoftwareVersion == 2,
        "Gateway 基础信息应兼容无小数点的 vN 版本字符串。");
    Assert(!OtaMessageCodec.TryParseGatewayBasicInfo(
            "{\"cmd\":3,\"base\":{\"dev_id\":704027,\"sw_ver\":\"v1.3.1\"}}",
            out _),
        "产品语义版本不能被误当成 OTA 单字节软件版本。");
    Assert(!OtaMessageCodec.TryParseGatewayAuthListPage(
            "{\"cmd\":\"legacy\",\"auth_num\":0}",
            out _),
        "旧 Gateway 的异常鉴权载荷不应抛出异常。");
    Assert(!OtaMessageCodec.TryParseNodeListPage(
            "{\"cmd\":\"legacy\",\"node_list\":{}}",
            out _),
        "旧 Gateway 的异常 Node 载荷不应抛出异常。");
    Assert(!OtaMessageCodec.TryParseNodeListPage(
            "{\"cmd\":11,\"node_list\":\"unsupported\"}",
            out _),
        "旧 Gateway 的非对象 Node 载荷不应抛出异常。");
    Assert(OtaMessageCodec.TryParseNodeListPage(
            "{\"cmd\":11,\"node_list\":{\"query_seq\":7,\"extender_id\":101,\"page_index\":0,\"page_count\":1,\"total_count\":1,\"item_count\":1,\"result\":\"OK\",\"reason\":\"NONE\",\"nodes\":[{\"node_id\":53936,\"node_type\":5,\"software_version\":0,\"rssi\":0}]}}",
            out var unknownVersionPage) && unknownVersionPage is not null &&
            unknownVersionPage.Nodes.Count == 1 && unknownVersionPage.Nodes[0].SoftwareVersion == 0,
        "Node 列表中的未知软件版本 0 应保留并继续完成列表解析。");
    Assert(OtaMessageCodec.TryParseAsyncVersionResponse(
            "{\"cmd\":13,\"async_version\":{\"query_seq\":7,\"extender_id\":101,\"result\":\"OK\",\"reason\":\"NONE\",\"software_version\":2}}",
            out var asyncVersion) && asyncVersion is not null &&
            asyncVersion.ExtenderId == 101 && asyncVersion.SoftwareVersion == 2,
        "异步板版本响应解析错误。");
    Assert(!OtaMessageCodec.TryParseAsyncVersionResponse(
            "{\"cmd\":13,\"async_version\":{\"query_seq\":7,\"extender_id\":101,\"result\":\"OK\",\"software_version\":0}}",
            out _),
        "异步板成功响应必须携带 1～254 的有效软件版本。");
    await using var mqtt = new FakeMqttTransport();
    var pageZeroRequests = new ConcurrentDictionary<uint, int>();
    mqtt.OnPublished = message =>
    {
        var payload = message.GetPayloadAsUtf8();
        var sequence = int.Parse(message.Topic.Split('/')[^1]);
        if (payload.Contains("\"query\":\"auth_list\"", StringComparison.Ordinal))
        {
            mqtt.Inject("ucchip/up/sgw/704027/auth-list",
                "{\"cmd\":3,\"auth_num\":1,\"101\":{\"detail\":\"online-a\",\"device_type\":2,\"software_version\":1}}");
            mqtt.Inject("ucchip/up/sgw/704027/auth-list",
                "{\"cmd\":3,\"auth_num\":9,\"101\":{\"detail\":\"online-a-duplicate\",\"device_type\":2,\"software_version\":1},\"202\":{\"detail\":\"online-b\",\"device_type\":2,\"software_version\":1},\"303\":{\"detail\":\"online-empty\",\"device_type\":2,\"software_version\":1},\"404\":{\"detail\":\"online-full\",\"device_type\":2,\"software_version\":1},\"505\":{\"detail\":\"online-one\",\"device_type\":2,\"software_version\":1},\"606\":{\"detail\":\"online-page\",\"device_type\":2,\"software_version\":1},\"707\":{\"detail\":\"online-retry\",\"device_type\":2,\"software_version\":1},\"808\":{\"detail\":\"online-missing\",\"device_type\":2,\"software_version\":1},\"909\":{\"detail\":\"online-duplicate-node\",\"device_type\":2,\"software_version\":1}}");
            return Task.CompletedTask;
        }
        if (payload.Contains("\"query\":\"base\"", StringComparison.Ordinal))
        {
            mqtt.Inject($"ucchip/up/sgw/704027/{sequence}",
                "{\"cmd\":3,\"ver\":\"v2.0\",\"src\":704027,\"base\":{\"dev_id\":704027,\"ota_software_version\":2,\"sw_ver\":\"v1.3.1\"}}");
            return Task.CompletedTask;
        }
        if (OtaMessageCodec.TryParseAsyncVersionQuery(payload, out var asyncExtenderId))
        {
            if (asyncExtenderId != 202)
            {
                mqtt.Inject($"ucchip/up/sgw/704027/{sequence}",
                    $"{{\"cmd\":13,\"async_version\":{{\"query_seq\":{sequence},\"extender_id\":{asyncExtenderId},\"result\":\"OK\",\"reason\":\"NONE\",\"software_version\":2}}}}");
            }
            return Task.CompletedTask;
        }
        if (!OtaMessageCodec.TryParseNodeListQuery(payload, out var extenderId, out var pageIndex))
        {
            return Task.CompletedTask;
        }
        if (extenderId == 202)
        {
            return Task.CompletedTask;
        }
        if (pageIndex == 0)
        {
            pageZeroRequests.AddOrUpdate(extenderId, 1, (_, count) => count + 1);
        }
        if ((extenderId == 707 && pageIndex == 1 && pageZeroRequests[extenderId] == 1) ||
            (extenderId == 808 && pageIndex == 1))
        {
            return Task.CompletedTask;
        }
        var total = extenderId switch
        {
            101 or 707 or 808 or 909 => 65,
            303 => 0,
            404 => 256,
            505 => 1,
            606 => 56,
            _ => 0,
        };
        var pageCount = Math.Max(1, (total + 55) / 56);
        var start = pageIndex * 56 + 1;
        var count = Math.Min(56, Math.Max(0, total - pageIndex * 56));
        var nodeIds = Enumerable.Range(start, count);
        if (extenderId == 606)
        {
            nodeIds = nodeIds.Reverse();
        }
        if (extenderId == 909 && pageIndex == 1)
        {
            nodeIds = [56];
        }
        var nodes = string.Join(',', nodeIds
            .Select(nodeId => extenderId == 505
                ? $"{{\"node_id\":{nodeId},\"node_type\":2,\"software_version\":0,\"rssi\":0}}"
                : $"{{\"node_id\":{nodeId},\"node_type\":2,\"software_version\":1,\"rssi\":-50}}"));
        mqtt.Inject($"ucchip/up/sgw/704027/{sequence}",
            $"{{\"cmd\":11,\"node_list\":{{\"query_seq\":{sequence},\"extender_id\":{extenderId},\"page_index\":{pageIndex},\"page_count\":{pageCount},\"total_count\":{total},\"item_count\":{count},\"result\":\"OK\",\"reason\":\"NONE\",\"nodes\":[{nodes}]}}}}");
        return Task.CompletedTask;
    };

    var discovery = new DeviceDiscoveryService(
        mqtt,
        new DeviceDiscoveryOptions(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10)));
    var basicInfo = await discovery.QueryGatewayBasicInfoAsync("704027");
    Assert(basicInfo.GatewayId == 704027 && basicInfo.SoftwareVersion == 2,
        "Gateway 基础信息查询未正确关联响应或软件版本。");
    var extenders = await discovery.DiscoverExtendersAsync("704027");
    Assert(extenders.Select(item => item.ExtenderId).SequenceEqual([101U, 202U, 303U, 404U, 505U, 606U, 707U, 808U, 909U]),
        "Extender 分页聚合错误。");
    var asyncVersions = await discovery.DiscoverAsyncVersionsAsync("704027", [101U, 202U]);
    Assert(asyncVersions.Count == 2 &&
           asyncVersions.Single(item => item.ExtenderId == 101).SoftwareVersion == 2 &&
           !asyncVersions.Single(item => item.ExtenderId == 202).IsSuccess,
        "异步板版本查询应按 Extender 并行关联响应，并隔离单板超时。");
    var results = await discovery.DiscoverNodesAsync("704027", [101U, 202U, 303U, 404U, 505U, 606U, 707U, 808U, 909U]);
    Assert(results.Count == 9 && results[0].IsSuccess && results[0].Nodes.Count == 65,
        "Node 65 项分页聚合错误。");
    Assert(!results[1].IsSuccess && results[0].Nodes.Count == 65,
        "单个 Extender 超时不应丢弃其他 Extender 结果。");
    Assert(results[2].IsSuccess && results[2].Nodes.Count == 0,
        "Node 空列表分页聚合错误。");
    Assert(results[3].IsSuccess && results[3].Nodes.Count == 256,
        "Node 256 项分页聚合错误。");
    Assert(results[4].IsSuccess && results[4].Nodes.Count == 0,
        "版本和 RSSI 同时为 0 的失效 Node 记录不应进入可升级列表。");
    Assert(results[5].IsSuccess && results[5].Nodes.Count == 56,
        "Node 56 项分页聚合错误。");
    Assert(results[5].Nodes.Select(node => node.NodeId).SequenceEqual(Enumerable.Range(1, 56).Select(value => (ushort)value)),
        "乱序 Node 项未按 Node ID 稳定聚合。");
    Assert(results[6].IsSuccess && results[6].Nodes.Count == 65 && pageZeroRequests[707] == 2,
        "缺页后未按整组重试并恢复完整结果。");
    Assert(!results[7].IsSuccess && pageZeroRequests[808] == 2,
        "持续缺页应在整组重试一次后仅标记对应 Extender 失败。");
    Assert(!results[8].IsSuccess && pageZeroRequests[909] == 2,
        "重复 Node 导致总数不完整时应整组重试并隔离失败。");

    await using var oldGatewayMqtt = new FakeMqttTransport();
    var oldGatewayDiscovery = new DeviceDiscoveryService(
        oldGatewayMqtt,
        new DeviceDiscoveryOptions(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10)));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var oldGatewayResults = await oldGatewayDiscovery.DiscoverNodesAsync(
        "704027",
        [101U, 202U, 303U, 404U]);
    stopwatch.Stop();
    Assert(oldGatewayResults.Count == 4 && oldGatewayResults.All(item => !item.IsSuccess),
        "旧 Gateway 不响应 cmd=10 时应返回分组失败结果，而不是抛出异常。");
    Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
        "旧 Gateway 不响应时应并行等待各 Extender，不能逐个累计超时。");
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("等待测试条件超时。");
        }
        await Task.Delay(5);
    }
}

static async Task VerifyEmbeddedBrokerAndMqttClientAsync()
{
    var port = GetFreeTcpPort();
    await using var broker = new EmbeddedMqttBroker();
    await broker.StartAsync(new EmbeddedMqttBrokerOptions(port));
    await using var subscriber = new Mqtt311Client();
    await using var publisher = new Mqtt311Client();
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receivedCount = 0;
    subscriber.MessageReceived += (_, message) =>
    {
        Interlocked.Increment(ref receivedCount);
        received.TrySetResult(message.GetPayloadAsUtf8());
    };
    await subscriber.ConnectAsync(new MqttClientOptions("127.0.0.1", port, "smoke-subscriber"));
    await publisher.ConnectAsync(new MqttClientOptions("127.0.0.1", port, "smoke-publisher"));
    await subscriber.SubscribeAsync("smoke/ota");
    await Task.Delay(25);
    await publisher.PublishAsync(new MqttApplicationMessage("smoke/ota", Encoding.UTF8.GetBytes("ok"), QualityOfService: 1));
    Assert(await received.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "ok", "MQTT 发布/订阅失败。");
    await subscriber.UnsubscribeAsync("smoke/ota");
    await Task.Delay(25);
    await publisher.PublishAsync(new MqttApplicationMessage("smoke/ota", Encoding.UTF8.GetBytes("ignored"), QualityOfService: 1));
    await Task.Delay(100);
    Assert(Volatile.Read(ref receivedCount) == 1, "MQTT 取消订阅后不应继续收到旧主题消息。");
}

static async Task VerifyReportsAsync(string workspace)
{
    var finalResult = new GatewayFinalResult(1, 1, 2, "Sync", "升级完成", true);
    var report = new OtaReport
    {
        Task = new OtaTask
        {
            Mode = OtaMode.Traditional,
            DeviceType = DeviceType.Sync,
            Target = OtaTaskTarget.Broadcast(),
            OldVersion = "1",
            NewVersion = "2",
        },
        FinalState = OtaTaskState.Succeeded,
    };
    report.AddUpdate(new OtaExecutionUpdate(
        report.Task.Id,
        OtaTaskState.Succeeded,
        "Gateway 最终结果上报：升级完成。",
        DateTimeOffset.Now,
        FinalResult: finalResult));
    var jsonPath = await OtaReportExporter.ExportJsonAsync(report, Path.Combine(workspace, "report.json"));
    var htmlPath = await OtaReportExporter.ExportHtmlAsync(report, Path.Combine(workspace, "report.html"));
    Assert(File.Exists(jsonPath) && File.Exists(htmlPath), "报告导出失败。");
    Assert((await File.ReadAllTextAsync(htmlPath)).Contains("日志解析不支持", StringComparison.Ordinal), "传统模式报告应标记日志解析不支持。");
    var databasePath = Path.Combine(workspace, "reports.db");
    var store = new SqliteReportStore(databasePath);
    await store.SaveAsync(report);
    Assert((await store.LoadRecentAsync()).Single().Id == report.Id, "SQLite 报告存储失败。");
    await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
    {
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT json FROM ota_reports WHERE id = $id;";
        read.Parameters.AddWithValue("$id", report.Id.ToString());
        var json = (string)(await read.ExecuteScalarAsync())!;
        var legacyJson = json
            .Replace("\"OldVersion\":1", "\"OldVersion\":\"1\"", StringComparison.Ordinal)
            .Replace("\"NewVersion\":2", "\"NewVersion\":\"2\"", StringComparison.Ordinal);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE ota_reports SET json = $json WHERE id = $id;";
        update.Parameters.AddWithValue("$json", legacyJson);
        update.Parameters.AddWithValue("$id", report.Id.ToString());
        await update.ExecuteNonQueryAsync();
    }
    var migratedReport = (await store.LoadRecentAsync()).Single();
    Assert(
        migratedReport.Timeline.Single().FinalResult is { OldVersion: 1, NewVersion: 2 },
        "SQLite 报告应兼容旧版本中以字符串保存的数值字段。");
    report.ArchivedAt = DateTimeOffset.Now;
    await store.SaveAsync(report);
    Assert((await store.LoadRecentAsync()).Single().IsArchived, "SQLite 报告归档状态未持久化。");
    await store.DeleteAsync(report.Id);
    Assert((await store.LoadRecentAsync()).Count == 0, "SQLite 报告删除失败。");
    Assert(await store.NextAsync() == 1 && await store.NextAsync() == 2, "SQLite 任务序号递增错误。");
}

static async Task VerifyCycleRunnerAsync(string workspace)
{
    var patch = Path.Combine(workspace, "cycle.patch");
    await File.WriteAllBytesAsync(patch, [1, 2, 3]);
    var forward = new OtaTask { Mode = OtaMode.Traditional, DeviceType = DeviceType.Gateway, OldVersion = "1", NewVersion = "2", PatchPath = patch };
    var reverse = new OtaTask { Mode = OtaMode.Traditional, DeviceType = DeviceType.Gateway, OldVersion = "2", NewVersion = "1", PatchPath = patch };
    var launcher = new StaticTaskLauncher(OtaTaskState.Succeeded);
    var fixedDelays = new List<TimeSpan>();
    var fixedRunner = new OtaCycleRunner((delay, _) =>
    {
        fixedDelays.Add(delay);
        return Task.CompletedTask;
    });
    var result = await fixedRunner.RunAsync(
        new OtaCycleDefinition(
            forward,
            reverse,
            2,
            new OtaCycleIntervalOptions(OtaCycleIntervalMode.Fixed, FixedSeconds: 3)),
        launcher);
    Assert(result.State == OtaTaskState.Succeeded && launcher.CallCount == 4, "双向循环升级未按预期执行。 ");
    Assert(fixedDelays.Count == 3 && fixedDelays.All(delay => delay == TimeSpan.FromSeconds(3)),
        "固定循环间隔应只出现在相邻单次升级之间。");

    var randomDelays = new List<TimeSpan>();
    var randomRunner = new OtaCycleRunner((delay, _) =>
    {
        randomDelays.Add(delay);
        return Task.CompletedTask;
    }, new Random(20260820));
    await randomRunner.RunAsync(
        new OtaCycleDefinition(
            forward,
            reverse,
            2,
            new OtaCycleIntervalOptions(OtaCycleIntervalMode.Random, RandomMinimumSeconds: 2, RandomMaximumSeconds: 5)),
        new StaticTaskLauncher(OtaTaskState.Succeeded));
    Assert(randomDelays.Count == 3 && randomDelays.All(delay => delay >= TimeSpan.FromSeconds(2) && delay <= TimeSpan.FromSeconds(5)),
        "随机循环间隔必须落在用户配置的闭区间内。");

    var zeroDelayCalls = 0;
    var zeroRunner = new OtaCycleRunner((_, _) =>
    {
        zeroDelayCalls++;
        return Task.CompletedTask;
    });
    await zeroRunner.RunAsync(
        new OtaCycleDefinition(
            forward,
            reverse,
            1,
            new OtaCycleIntervalOptions(OtaCycleIntervalMode.Fixed, FixedSeconds: 0)),
        new StaticTaskLauncher(OtaTaskState.Succeeded));
    Assert(zeroDelayCalls == 0, "循环间隔为 0 秒时必须保持原有连续执行逻辑。");

    var waitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellableRunner = new OtaCycleRunner(async (_, cancellationToken) =>
    {
        waitStarted.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    });
    using var cancellation = new CancellationTokenSource();
    var cancellableRun = cancellableRunner.RunAsync(
        new OtaCycleDefinition(
            forward,
            reverse,
            1,
            new OtaCycleIntervalOptions(OtaCycleIntervalMode.Fixed, FixedSeconds: 10)),
        new StaticTaskLauncher(OtaTaskState.Succeeded),
        cancellation.Token);
    await waitStarted.Task;
    cancellation.Cancel();
    try
    {
        await cancellableRun;
        Assert(false, "循环升级间隔等待应支持取消。");
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task VerifyDiffManifestGateAsync(string workspace)
{
    var oldPath = Path.Combine(workspace, "old.bin");
    var newPath = Path.Combine(workspace, "new.bin");
    var samePath = Path.Combine(workspace, "same.bin");
    var patchPath = Path.Combine(workspace, "diff.patch");
    var oldImage = CreateFirmwareImage(FirmwareDeviceType.Socket, 1, 0x11);
    var newImage = CreateFirmwareImage(FirmwareDeviceType.Socket, 2, 0x22);
    await File.WriteAllBytesAsync(oldPath, oldImage);
    await File.WriteAllBytesAsync(newPath, newImage);
    await File.WriteAllBytesAsync(samePath, oldImage);
    await File.WriteAllBytesAsync(patchPath, [7, 8]);
    Assert(await FirmwareImageHash.AreIdenticalAsync(oldPath, samePath), "相同镜像哈希校验失败。");
    Assert(!await FirmwareImageHash.AreIdenticalAsync(oldPath, newPath), "不同镜像被误判为相同。");
    var request = new DiffRequest(oldPath, newPath, patchPath, DeviceType.Node, "1", "2");
    var engine = new UnavailableDiffEngine();
    Assert(!((await engine.GenerateAsync(request)).IsSuccess), "未认证差分引擎不得生成 Patch。");
    var manifest = await PackageManifestFactory.CreateAsync(engine.GetInfo(), request, await PatchMetadata.FromFileAsync(patchPath), false);
    var output = await PackageManifestExporter.ExportAsync(manifest, patchPath + ".json");
    var oldIdentity = await FirmwareIdentityReader.ReadAsync(oldPath);
    var newIdentity = await FirmwareIdentityReader.ReadAsync(newPath);
    Assert(oldIdentity.DeviceType == FirmwareDeviceType.Socket && oldIdentity.Version == 1
        && oldIdentity.SuggestedPatchNameTo(newIdentity) == "node-v1-to-v2.patch",
        "固件身份识别或 Patch 自动命名错误。");
    Assert(File.Exists(output) && !manifest.PatchVerified, "Manifest 导出或 PatchTest 门禁错误。");
}

static byte[] CreateFirmwareImage(FirmwareDeviceType deviceType, byte version, byte fill)
{
    var image = Enumerable.Repeat(fill, 28 * 1024).Select(value => (byte)value).ToArray();
    image[FirmwareIdentityReader.IdentityOffset] = version;
    image[FirmwareIdentityReader.EcoMagicOffset] = (byte)'e';
    image[FirmwareIdentityReader.EcoMagicOffset + 1] = (byte)'c';
    image[FirmwareIdentityReader.EcoMagicOffset + 2] = (byte)'o';
    image[FirmwareIdentityReader.EcoMagicOffset + 3] = (byte)deviceType;
    return image;
}

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class TransientPatchHttpHandler(byte[] payload) : HttpMessageHandler
{
    private int _headAttempts;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Head && Interlocked.Increment(ref _headAttempts) == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        if (request.Method == HttpMethod.Head)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }

        if (request.Headers.Range is not null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[..1]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, payload.Length);
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
    }
}

sealed class FakeMqttTransport : IMqttTransport
{
    public bool IsConnected { get; private set; } = true;

    public List<MqttApplicationMessage> Published { get; } = [];

    public List<string> Subscriptions { get; } = [];

    public List<string> Unsubscriptions { get; } = [];

    public Func<MqttApplicationMessage, Task>? OnPublished { get; set; }

    public event EventHandler<MqttApplicationMessage>? MessageReceived;

    public Task ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicFilter, byte qualityOfService = 1, CancellationToken cancellationToken = default)
    {
        Subscriptions.Add(topicFilter);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        Unsubscriptions.Add(topicFilter);
        return Task.CompletedTask;
    }

    public async Task PublishAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        lock (Published)
        {
            Published.Add(message);
        }
        if (OnPublished is not null) await OnPublished(message);
    }

    public void Inject(string topic, string payload) => MessageReceived?.Invoke(this, new MqttApplicationMessage(topic, Encoding.UTF8.GetBytes(payload)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class StaticTaskLauncher(OtaTaskState state) : IOtaTaskLauncher
{
    public int CallCount { get; private set; }

    public Task<OtaTaskResult> StartAndWaitAsync(OtaTask task, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(new OtaTaskResult(state, state.ToString(), DateTimeOffset.Now));
    }
}
