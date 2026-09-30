using System.IO;
using System.Reflection;
using System.Windows;
using OtaTool.App.ViewModels;
using OtaTool.Core.Models;
using OtaTool.Core.Protocols;
using OtaTool.Core.Reports;
using OtaTool.Core.Security;
using OtaTool.Core.Settings;

static class Program
{
    [STAThread]
    static int Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ota-ipm-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        var mcu = Path.Combine(packages, "ipm_mcu.bin");
        var bytes = new byte[64];
        BitConverter.GetBytes(0x20008000U).CopyTo(bytes, 0);
        BitConverter.GetBytes(0x08008009U).CopyTo(bytes, 4);
        await File.WriteAllBytesAsync(mcu, bytes);
        await IpmPackageMetadata.CreateAsync(mcu, DeviceType.Mcu, PackageKind.FullImage, 2, IpmPackageMetadata.AppAddress);
        var foreign = Path.Combine(packages, "other_gateway.bin");
        var gatewayBytes = new byte[32 * 1024];
        new byte[] { 2, (byte)'e', (byte)'c', (byte)'o', (byte)FirmwareDeviceType.Gateway }.CopyTo(gatewayBytes, FirmwareIdentityReader.IdentityOffset);
        await File.WriteAllBytesAsync(foreign, gatewayBytes);
        var settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(new AppSettings { ActiveMode = "IPM", ModeWorkspaces = new Dictionary<string, ModeWorkspaceSettings>
        {
            ["IPM"] = new() { HttpRootDirectory = packages, SelectedTaskType = "倾角定位-MCU升级", IpmMcuUpgradeMode = IpmMcuUpgradeMode.Formal, OldVersion = "1", GatewayId = "123", TargetIdList = "10001", SelectedUpgradePatchPath = mcu,
                DiscoveredExtenders = [new(10001, "测试同步端", 2, 99, true), new(10002, "非同步端", 1, 88, false)] },
            ["EcoLink"] = new() { HttpRootDirectory = packages, SelectedTaskType = "拓展器-异步升级", TargetIdList = "20001", GatewayId = "456",
                DiscoveredExtenders = [new(20001, "Gateway 鉴权同步端 A", 2, 1, true), new(20002, "非同步端", 0, 1, false),
                    new(20003, "Gateway 鉴权同步端 B", 2, 1, false), new(20004, "应用帧类型号不是鉴权类型号", 3, 1, false)] },
            ["Traditional"] = new() { HttpRootDirectory = packages },
        }});
        var vm = new MainWindowViewModel(root, new MemorySecrets());
        await vm.Initialization;
        async Task Reload() => await (Task)typeof(MainWindowViewModel).GetMethod("RestoreCurrentModePatchCatalogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
        await Reload();
        Check(vm.IsIpm && !vm.IsEcoLink && !vm.IsTraditional, "启动模式恢复失败。");
        Check(vm.IsFormalIpmMcu, "IPM 正式模式未恢复。");
        Check(vm.EcoLinkSettingsVisibility == Visibility.Collapsed && vm.ModernOtaVisibility == Visibility.Visible &&
            vm.SyncPatchLimit == 52 * 1024 && vm.GatewayPatchLimit == 2 * 1024 * 1024,
            "IPM 门限显示或默认值错误。");
        Check(vm.TaskTypes.SequenceEqual(new[] { "网关升级", "倾角定位-同步升级", "倾角定位-MCU升级" }), "IPM 任务类型不正确。");
        Check(vm.UpgradePatchChoices.Count == 1 && vm.SelectedUpgradePatch?.OtaDeviceType == DeviceType.Mcu, "MCU 包筛选失败。");
        Check(vm.PatchCatalog.All(x => x.FilePath != foreign), "IPM 目录扫描误收其他项目包。");
        Check(vm.OldVersion == string.Empty && vm.NewVersion == "2", "MCU 未选择反向镜像时不应沿用旧版本。");
        Check(vm.DiscoveredExtenders.Count == 1 && vm.DiscoveredExtenders[0].AsyncSoftwareVersion is null, "发现恢复类型或 MCU 未知版本语义错误。");
        Check(vm.NodeDiscoveryVisibility == Visibility.Collapsed && vm.NodeTaskVisibility == Visibility.Collapsed && vm.RequiresSpecifiedTarget, "IPM 节点功能未隐藏。");
        vm.DiscoveredExtenders[0].IsSelected = false;
        Check(vm.TargetIdList.Length == 0, "IPM 取消勾选未同步固定目标。");
        vm.DiscoveredExtenders[0].IsSelected = true;
        Check(vm.TargetIdList == "10001", "IPM 勾选目标未同步固定目标。");
        typeof(MainWindowViewModel).GetProperty("TaskStatusMessage")!.SetValue(vm, "固定目标请至少选择一个 Extender。");
        Check(!vm.GlobalLogText.Contains("Extender") && vm.GlobalLogText.Contains("倾角定位同步"), "IPM 日志仍保留其他模式字段。");
        Check(vm.OtherProtocolModeLabel == "EcoLink 模式", "模式切换按钮名称不统一。");
        foreach (var text in new[] { vm.CurrentPageSubtitle, vm.ModeBadge, vm.LogAnalysisTitle, vm.DeviceDiscoveryStatus, vm.TaskStatusMessage, vm.SelectedUpgradePatch!.PublishState })
            Check(!text.Contains("ecolink", StringComparison.OrdinalIgnoreCase), "IPM 界面出现旧模式字样：" + text);
        Check(vm.DeviceDiscoveryButtonText == "刷新倾角定位同步", "IPM 刷新文案错误。");
        var reverseFile = Path.Combine(packages, "ipm_mcu_v1.bin");
        bytes[20] = 1;
        await File.WriteAllBytesAsync(reverseFile, bytes);
        await IpmPackageMetadata.CreateAsync(reverseFile, DeviceType.Mcu, PackageKind.FullImage, 1, IpmPackageMetadata.AppAddress);
        await Reload();
        vm.SelectedUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 2);
        vm.SelectedReverseUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 1);
        Check(vm.OldVersion == "1" && vm.NewVersion == "2", "MCU 未从正反向完整镜像自动识别版本对。");
        vm.SelectedReverseUpgradePatch = null;
        Check(vm.OldVersion == string.Empty, "清除反向 MCU 镜像后仍残留旧版本。");
        vm.SelectedReverseUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 1);
        var cycle = await (Task<OtaTestPlanItemTemplate>)typeof(MainWindowViewModel)
            .GetMethod("BuildTestPlanItemTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [OtaTestPlanExecutionKind.Cycle, 1])!;
        Check(cycle.Mode == OtaMode.IPM && cycle.DeviceType == DeviceType.Mcu &&
            cycle.ForwardPatch.PackageKind == PackageKind.FullImage && cycle.ReversePatch?.PackageKind == PackageKind.FullImage &&
            cycle.ForwardPatch.FullImageTargetVersion == 2 && cycle.ReversePatch.FullImageTargetVersion == 1 &&
            cycle.TargetRule.ResolutionMode == OtaTargetResolutionMode.FixedIds && cycle.TargetRule.DeviceIds.SequenceEqual(new[] { "10001" }),
            "MCU 循环任务未固化两个方向的完整包或目标。");
        vm.SelectedTaskType = "倾角定位-同步升级";
        vm.IsSpecifiedTarget = false;
        var broadcast = (OtaTestPlanTargetRule)typeof(MainWindowViewModel)
            .GetMethod("BuildPlanTargetRule", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [DeviceType.Sync])!;
        Check(broadcast.Scope == TargetScope.Broadcast && broadcast.DeviceIds.Count == 0, "同步广播范围未进入队列模型。");
        vm.OldVersion = "99";
        vm.SelectedTaskType = "倾角定位-MCU升级";
        Check(vm.OldVersion != "99", "MCU 继承了同步任务旧版本。");
        vm.SelectedUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 2);
        vm.SelectedReverseUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 1);
        vm.IsEcoLink = true;
        await Reload();
        Check(vm.EcoLinkSettingsVisibility == Visibility.Visible && vm.NodePatchLimit == 124 * 1024,
            "设备类型模式下的 Node 门限错误。");
        Check(vm.TargetIdList == "20001" && !vm.TaskTypes.Contains("倾角定位-MCU升级"), "模式目标串扰。");
        Check(vm.DiscoveredExtenders.Count == 2 &&
            vm.DiscoveredExtenders.Select(item => item.ExtenderId).SequenceEqual(new uint[] { 20001, 20003 }) &&
            vm.DiscoveredExtenders.All(item => item.DeviceType == OtaMessageCodec.GatewayAuthSyncDeviceType),
            "EcoLink 鉴权列表的两个类型 2 同步端均应显示，类型 0/3 不应作为目标。");
        Check(!NodeTypeCatalog.IsSelectable(2) && !NodeTypeCatalog.IsSelectable(3) &&
            Enumerable.Range(4, 6).All(NodeTypeCatalog.IsSelectable),
            "Extender 类型 2/3 不能进入 Node 选项，内置 Node 类型 4～9 必须保留。");
        Check(vm.PatchCatalog.All(x => x.FilePath != mcu), "其他模式误收 IPM 包。");
        vm.IsTraditional = true;
        Check(vm.EcoLinkSettingsVisibility == Visibility.Collapsed && vm.ModernOtaVisibility == Visibility.Collapsed,
            "传统模式仍显示现代协议的 Patch 门限。");
        vm.IsIpm = true;
        await Reload();
        Check(vm.TargetIdList == "10001" && vm.OldVersion == "1", "切回 IPM 未恢复独立工作区。");
        vm.GatewayId = "789";
        Check(vm.TargetIdList.Length == 0 && vm.DiscoveredExtenders.Count == 0, "切换网关未清除旧目标。");
        vm.IsNoUbootValidation = true;
        Check(vm.UpgradePatchChoices.Count == 0, "无 uboot 模式不应展示正式 MCU 包。");
        var rawMcu = Path.Combine(root, "ipm_raw_mcu.bin");
        await File.WriteAllBytesAsync(rawMcu, bytes);
        Check(!File.Exists(IpmPackageMetadata.SidecarPath(rawMcu)), "测试源镜像不得预置项目清单。");
        await (Task)typeof(MainWindowViewModel).GetMethod("ImportPatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [rawMcu])!;
        var importedMcu = vm.PatchCatalog.Single(x => x.FileName == "ipm_raw_mcu.bin");
        Check(importedMcu.FileName == Path.GetFileName(rawMcu), "导入修改了原文件名或重复添加模式前缀。");
        await (Task)typeof(MainWindowViewModel).GetMethod("ImportPatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [importedMcu.FilePath])!;
        Check(vm.PatchCatalog.Count(x => x.FileName == importedMcu.FileName) == 1, "从输出目录再次导入产生了额外前缀副本。");
        Check(importedMcu.OtaDeviceType == DeviceType.Mcu && importedMcu.NewVersion is null &&
            importedMcu.PublishState.Contains("版本未知"), "无清单 MCU 应正常导入并保留未知版本。");
        vm.SelectedUpgradePatch = importedMcu;
        vm.SelectedReverseUpgradePatch = null;
        Check(vm.SelectedUpgradePatch == importedMcu && vm.NewVersion == string.Empty &&
            !vm.CanStartForwardUpgrade && !vm.CanStartReverseUpgrade && !vm.CanStartCycleUpgrade,
            "未知版本 MCU 不应被当作可执行任务。");
        Check(vm.CanEditMcuVersions && vm.ManualMcuVersionVisibility == Visibility.Visible, "待识别 MCU 没有临时版本输入入口。");
        await Reload();
        Check(vm.PatchCatalog.Any(x => x.FilePath == importedMcu.FilePath && x.NewVersion is null),
            "重新加载目录时丢失已导入的未知版本 MCU 包。");
        Check(!File.Exists(IpmPackageMetadata.SidecarPath(rawMcu)), "导入不应要求或改写用户源目录清单。");
        foreach (var item in vm.PatchCatalog) item.IsSelectedForPublish = item.FilePath == importedMcu.FilePath;
        vm.PublicHttpBaseUrl = "https://example.invalid/ota/";
        var publishCommand = typeof(MainWindowViewModel).GetMethod("PublishPatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var publishValidation = typeof(MainWindowViewModel).GetMethod("ValidatePatchForPublication", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var identifiedMcu = vm.PatchCatalog.Single(x => x.FilePath == mcu);
        vm.IsFormalIpmMcu = true;
        publishValidation.Invoke(vm, [identifiedMcu]);
        await IpmPackageMetadata.CreateAsync(mcu, DeviceType.Mcu, PackageKind.FullImage, 3, IpmPackageMetadata.AppAddress);
        try { publishValidation.Invoke(vm, [identifiedMcu]); throw new Exception("已识别 MCU 版本不一致时不能使用临时发布放行。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        await IpmPackageMetadata.CreateAsync(mcu, DeviceType.Mcu, PackageKind.FullImage, 2, IpmPackageMetadata.AppAddress);
        var unverifiedKnownMcu = new PatchSelection("测试未验证镜像", mcu, identifiedMcu.Length,
            identifiedMcu.Md5, identifiedMcu.Sha256, true, isFullImage: true, otaDeviceType: DeviceType.Mcu, newVersion: 2);
        try { publishValidation.Invoke(vm, [unverifiedKnownMcu]); throw new Exception("已识别但未经验证的 MCU 不应使用未知版本临时放行。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        vm.IsNoUbootValidation = true;
        publishValidation.Invoke(vm, [importedMcu]);
        await (Task)publishCommand.Invoke(vm, null)!;
        Check(vm.IsPatchDialogOpen && vm.PatchDialogTitle == "确认 Patch 发布" &&
            vm.PatchDialogMessage.Contains(importedMcu.FileName), "未知版本 MCU 完整包被差分清单门禁拦截。");
        typeof(MainWindowViewModel).GetMethod("CancelPatchDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        var invalidDiff = new PatchSelection("测试差分包", importedMcu.FilePath, importedMcu.Length,
            importedMcu.Md5, importedMcu.Sha256, true, otaDeviceType: DeviceType.Sync);
        try { publishValidation.Invoke(vm, [invalidDiff]); throw new Exception("缺少清单的差分包仍应禁止发布。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        vm.SelectedUpgradePatch = vm.PatchCatalog.Single(x => x.FilePath == importedMcu.FilePath);
        vm.OldVersion = "1";
        vm.NewVersion = "2";
        vm.TargetIdList = "10001";
        Check(vm.CanStartForwardUpgrade && !vm.CanStartCycleUpgrade && !vm.CanStartReverseUpgrade,
            "手填有效版本后应允许单次下载联调，不能自动循环或反向。");
        var manualTemplate = await (Task<OtaTestPlanItemTemplate>)typeof(MainWindowViewModel)
            .GetMethod("BuildTestPlanItemTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [OtaTestPlanExecutionKind.Forward, 1])!;
        Check(manualTemplate.OldVersion == "1" && manualTemplate.NewVersion == "2" &&
            manualTemplate.ForwardPatch.IsMcuDownloadTest && manualTemplate.ForwardPatch.FullImageTargetVersion == 2 &&
            OtaTestPlanVersionProjection.GetProjectedEndVersion(manualTemplate) == 1,
            "联调任务未保存手填版本或错误推进运行版本。");
        var manualPackage = IpmPackageMetadata.Validate(importedMcu.FilePath, DeviceType.Mcu, PackageKind.FullImage, 2);
        Check(manualPackage.IsDownloadTest, "手填版本未记录联调来源。");
        OtaTask ManualTask(IpmPackageMetadata? packageMetadata) => new()
        {
            Mode = OtaMode.IPM, DeviceType = DeviceType.Mcu, GatewayId = "789",
            OldVersion = manualTemplate.OldVersion, NewVersion = manualTemplate.NewVersion,
            Target = OtaTaskTarget.Specified("10001"), PackageKind = PackageKind.FullImage,
            PatchPath = importedMcu.FilePath, PatchUrl = "http://127.0.0.1/" + importedMcu.FileName,
            PatchMd5 = manualPackage.Md5, PatchSha256 = manualPackage.Sha256, PackageMetadata = packageMetadata,
        };
        Check(OtaTaskValidator.Validate(ManualTask(manualPackage), new IpmProtocolProfile()).IsValid,
            "手填版本仅通过入队但仍被执行校验拦截。");
        Check(!OtaTaskValidator.Validate(ManualTask(null), new IpmProtocolProfile()).IsValid,
            "联调镜像不能冒充正式升级任务。");
        importedMcu = vm.SelectedUpgradePatch!;
        bytes[21] ^= 1;
        await File.WriteAllBytesAsync(importedMcu.FilePath, bytes);
        try { publishValidation.Invoke(vm, [importedMcu]); throw new Exception("发布确认后的文件篡改未被上传前校验拒绝。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        await (Task)publishCommand.Invoke(vm, null)!;
        Check(!vm.IsPatchDialogOpen && vm.PublishStatus.Contains("内容与导入清单不一致"),
            "发布前未验证 MCU 实际文件哈希。");
        var reportStore = new SqliteReportStore(Path.Combine(root, "ota-tool.db"));
        foreach (var reportMode in new[] { OtaMode.IPM, OtaMode.EcoLink, OtaMode.Traditional })
            await reportStore.SaveAsync(new OtaReport { Task = new OtaTask { Mode = reportMode, DeviceType = DeviceType.Gateway } });
        async Task LoadReports() => await (Task)typeof(MainWindowViewModel)
            .GetMethod("LoadReportsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [false])!;
        await LoadReports();
        Check(vm.RecentReports.Count == 1 && vm.RecentReports.All(item => item.Mode == "IPM"),
            "IPM 历史报告混入其他模式。");
        var previousModeLoad = LoadReports();
        vm.IsEcoLink = true;
        await previousModeLoad;
        await LoadReports();
        Check(vm.RecentReports.Count == 1 && vm.RecentReports.All(item => item.Mode == "EcoLink"),
            "模式切换后旧查询覆盖了当前历史报告。");
        vm.IsTraditional = true;
        await LoadReports();
        Check(vm.RecentReports.Count == 1 && vm.RecentReports.All(item => item.Mode == "传统模式"),
            "传统模式历史报告混入其他模式。");
        vm.IsIpm = true;
        await LoadReports();
        await vm.DisposeAsync();
        var restored = await settings.LoadAsync();
        Check(restored.ActiveMode == "IPM" && restored.ModeWorkspaces["IPM"].GatewayId == "789" &&
            restored.ModeWorkspaces["IPM"].IpmMcuUpgradeMode == IpmMcuUpgradeMode.NoUbootValidation &&
            restored.ModeWorkspaces["EcoLink"].GatewayId == "456", "三模式保存失败。");
        Console.WriteLine("IPM 界面模型、包筛选、版本语义、三模式切换及持久化测试通过。");
    }

    sealed class MemorySecrets : ISecretStore
    {
        readonly Dictionary<string, string> values = [];
        public void Save(string name, string secret) => values[name] = secret;
        public bool TryGet(string name, out string? secret) => values.TryGetValue(name, out secret);
        public void Delete(string name) => values.Remove(name);
    }
}
