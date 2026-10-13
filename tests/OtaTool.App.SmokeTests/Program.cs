using System.IO;
using System.Reflection;
using System.Windows;
using OtaTool.App.ViewModels;
using OtaTool.Core.Execution;
using OtaTool.Core.Models;
using OtaTool.Core.Mqtt;
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
        var bytes = McuImageFixture.Factory(McuImageFixture.App());
        await File.WriteAllBytesAsync(mcu, bytes);
        await IpmPackageMetadata.CreateAsync(mcu, DeviceType.Mcu, PackageKind.FullImage, 2, IpmPackageMetadata.FirmwareAddress);
        var foreign = Path.Combine(packages, "other_gateway.bin");
        var gatewayBytes = new byte[32 * 1024];
        new byte[] { 2, (byte)'e', (byte)'c', (byte)'o', 8 }.CopyTo(gatewayBytes, FirmwareIdentityReader.IdentityOffset);
        await File.WriteAllBytesAsync(foreign, gatewayBytes);
        var settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(new AppSettings { ActiveMode = "IPM", ModeWorkspaces = new Dictionary<string, ModeWorkspaceSettings>
        {
            ["IPM"] = new() { HttpRootDirectory = packages, SelectedTaskType = "倾角定位-MCU升级", OldVersion = "1", GatewayId = "123", TargetIdList = "10001", SelectedUpgradePatchPath = mcu,
                DiscoveredExtenders = [new(10001, "测试同步端", 2, 99, true), new(10002, "非同步端", 1, 88, false)] },
            ["EcoLink"] = new() { HttpRootDirectory = packages, SelectedTaskType = "拓展器-异步升级", TargetIdList = "20001", GatewayId = "456",
                DiscoveredExtenders = [new(20001, "Gateway 鉴权同步端 A", 2, 1, true), new(20002, "非同步端", 0, 1, false),
                    new(20003, "Gateway 鉴权同步端 B", 2, 1, false), new(20004, "应用帧类型号不是鉴权类型号", 3, 1, false)] },
            ["Traditional"] = new() { HttpRootDirectory = packages },
        }});
        // 老设置中保留的模式字段不应恢复已移除的联调路径。
        var legacySettings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "settings.json")))!;
        legacySettings["modeWorkspaces"]!["IPM"]!["ipmMcuUpgradeMode"] = 0;
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), legacySettings.ToJsonString());
        var vm = new MainWindowViewModel(root, new MemorySecrets());
        await vm.Initialization;
        var receiveMqtt = typeof(MainWindowViewModel).GetMethod("OnMqttMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Receive(string topic, string payload) => receiveMqtt.Invoke(vm,
            [null, new MqttApplicationMessage(topic, System.Text.Encoding.UTF8.GetBytes(payload))]);
        var heartbeatEvents = 0;
        vm.GatewayHeartbeatReceived += (_, _) => heartbeatEvents++;
        const string gatewayTopic = "ucchip/up/sgw/123/1";
        Receive(gatewayTopic, "{\"cmd\":1,\"ver\":\"v2.0\",\"src\":123}");
        Receive(gatewayTopic, "{\"src\":123,\"cmd\":1}");
        Check(vm.GatewayHeartbeatCount == 2 && heartbeatEvents == 2 && vm.MqttMessages.Count == 0,
            "网关心跳未计数、未触发动画事件或仍刷入 MQTT 收发记录。");
        Receive(gatewayTopic, "{\"cmd\":201,\"src\":123}");
        Receive(gatewayTopic, "{\"cmd\":1,\"src\":456}");
        Receive(gatewayTopic, "{\"cmd\":\"1\",\"src\":123}");
        Receive(gatewayTopic, "不是 JSON");
        Check(vm.GatewayHeartbeatCount == 2 && vm.MqttMessages.Count == 4,
            "非心跳、来源不符或异常载荷被误过滤。");
        vm.ClearMqttMessagesCommand.Execute(null);
        Check(vm.GatewayHeartbeatCount == 0 && vm.MqttMessages.Count == 0, "清理收发记录后心跳计数未归零。");
        async Task Reload() => await (Task)typeof(MainWindowViewModel).GetMethod("RestoreCurrentModePatchCatalogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
        await Reload();
        Check(vm.IsIpm && !vm.IsEcoLink && !vm.IsTraditional, "启动模式恢复失败。");
        Check(vm.EcoLinkSettingsVisibility == Visibility.Collapsed && vm.ModernOtaVisibility == Visibility.Visible &&
            vm.IpmVisibility == Visibility.Visible && vm.McuImageLimit == 256 * 1024 &&
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
        await File.WriteAllBytesAsync(reverseFile, McuImageFixture.Factory(McuImageFixture.App(version: 1)));
        await IpmPackageMetadata.CreateAsync(reverseFile, DeviceType.Mcu, PackageKind.FullImage, 1, IpmPackageMetadata.FirmwareAddress);
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
        var broadcastItem = new OtaTestPlanItemTemplate
        {
            Mode = OtaMode.IPM, DeviceType = DeviceType.Sync, GatewayId = vm.GatewayId,
            OldVersion = "1", NewVersion = "2", TargetRule = broadcast,
        };
        var discoverSnapshot = typeof(MainWindowViewModel).GetMethod("DiscoverTestPlanSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var broadcastSnapshotTask = (Task)discoverSnapshot.Invoke(vm, [new[] { broadcastItem }, CancellationToken.None])!;
        await broadcastSnapshotTask;
        var broadcastSnapshot = broadcastSnapshotTask.GetType().GetProperty("Result")!.GetValue(broadcastSnapshotTask)!;
        Check(((System.Collections.IDictionary)broadcastSnapshot.GetType().GetProperty("Extenders")!.GetValue(broadcastSnapshot)!).Count == 0,
            "IPM 同步广播不应强制查询在线鉴权列表。");
        var resolveTargets = typeof(MainWindowViewModel).GetMethod("ResolveTestPlanTargets", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var broadcastTargets = resolveTargets.Invoke(vm, [broadcastItem, broadcastSnapshot])!;
        Check(((OtaTaskTarget)broadcastTargets.GetType().GetProperty("Target")!.GetValue(broadcastTargets)!).Scope == TargetScope.Broadcast &&
            ((IReadOnlyList<string>)broadcastTargets.GetType().GetProperty("BroadcastIds")!.GetValue(broadcastTargets)!).Count == 0,
            "IPM 同步广播被 EcoLink 在线目标门禁拦截。");
        var fixedSyncItem = broadcastItem with { TargetRule = new OtaTestPlanTargetRule
        {
            Scope = TargetScope.SpecifiedIds, ResolutionMode = OtaTargetResolutionMode.FixedIds, DeviceIds = ["10001"],
        } };
        try { resolveTargets.Invoke(vm, [fixedSyncItem, broadcastSnapshot]); throw new Exception("IPM 定向同步任务绕过在线目标检查。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException error &&
            error.Message.Contains("倾角定位同步端", StringComparison.Ordinal) && !error.Message.Contains("Extender", StringComparison.Ordinal)) { }
        try { resolveTargets.Invoke(vm, [broadcastItem with { Mode = OtaMode.EcoLink }, broadcastSnapshot]);
            throw new Exception("EcoLink 同步任务错误复用了 IPM 广播豁免。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException error &&
            error.Message.Contains("Extender", StringComparison.Ordinal)) { }
        var verifyBroadcast = (Task<OtaTestPlanOperationResult>)typeof(MainWindowViewModel)
            .GetMethod("VerifyTaskVersionWithRetryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [new OtaTask { Mode = OtaMode.IPM, DeviceType = DeviceType.Sync, Target = OtaTaskTarget.Broadcast(), NewVersion = "2" }, CancellationToken.None])!;
        Check((await verifyBroadcast).State == OtaTaskState.Succeeded,
            "IPM 无目标快照的同步广播在网关最终成功后仍被独立版本复查阻塞。");
        vm.OldVersion = "99";
        vm.SelectedTaskType = "倾角定位-MCU升级";
        Check(vm.OldVersion != "99", "MCU 继承了同步任务旧版本。");
        vm.SelectedUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 2);
        vm.SelectedReverseUpgradePatch = vm.UpgradePatchChoices.Single(x => x.NewVersion == 1);
        vm.IsEcoLink = true;
        await Reload();
        Check(vm.EcoLinkSettingsVisibility == Visibility.Visible && vm.IpmVisibility == Visibility.Collapsed &&
            vm.NodePatchLimit == 124 * 1024,
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
        Check(vm.EcoLinkSettingsVisibility == Visibility.Collapsed && vm.ModernOtaVisibility == Visibility.Collapsed &&
            vm.IpmVisibility == Visibility.Collapsed,
            "传统模式仍显示现代协议的 Patch 门限。");
        vm.IsIpm = true;
        await Reload();
        Check(vm.TargetIdList == "10001" && vm.OldVersion == "1", "切回 IPM 未恢复独立工作区。");
        vm.GatewayId = "789";
        Check(vm.TargetIdList.Length == 0 && vm.DiscoveredExtenders.Count == 0, "切换网关未清除旧目标。");
        var sourceMcu = Path.Combine(root, "ipm_source_mcu.bin");
        await File.WriteAllBytesAsync(sourceMcu, bytes);
        async Task Import(string path) => await (Task)typeof(MainWindowViewModel)
            .GetMethod("ImportPatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [path])!;
        vm.SelectedTaskType = "网关升级";
        await Import(sourceMcu);
        var importedMcu = vm.PatchCatalog.Single(x => x.FileName == Path.GetFileName(sourceMcu));
        Check(importedMcu.ManifestVerified && importedMcu.NewVersion == 2 && importedMcu.OtaDeviceType == DeviceType.Mcu,
            "无外部清单 APP 未自动识别内嵌版本。");
        Check(vm.SelectedTaskType == "倾角定位-MCU升级", "MCU 导入仍依赖事先选择 MCU 任务。");
        await Import(importedMcu.FilePath);
        Check(vm.PatchCatalog.Count(x => x.FileName == importedMcu.FileName) == 1, "再次导入同一 APP 产生副本。");
        Check(!File.Exists(IpmPackageMetadata.SidecarPath(sourceMcu)) && (await File.ReadAllBytesAsync(sourceMcu)).SequenceEqual(bytes),
            "导入不能修改用户源文件或要求源目录清单。");
        var factoryFile = Path.Combine(root, "factory.bin");
        var factoryBytes = bytes.ToArray();
        await File.WriteAllBytesAsync(factoryFile, factoryBytes);
        await Import(factoryFile);
        var extracted = vm.PatchCatalog.Single(x => x.FileName == "factory.bin");
        Check(extracted.Length == bytes.Length && extracted.NewVersion == 2 && extracted.Md5 == importedMcu.Md5 &&
            (await File.ReadAllBytesAsync(extracted.FilePath)).SequenceEqual(bytes) &&
            (await File.ReadAllBytesAsync(factoryFile)).SequenceEqual(factoryBytes),
            "导入未原样保留完整固件或整包哈希。");
        var validationRoot = Environment.GetEnvironmentVariable("IPM_MCU_VALIDATION_DIR");
        if (!string.IsNullOrEmpty(validationRoot))
        {
            using var validation = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(validationRoot, "validation.json")));
            var baselineHash = validation.RootElement.GetProperty("baseline").GetProperty("md5").GetString();
            var targetHash = validation.RootElement.GetProperty("target").GetProperty("md5").GetString();
            await Import(Path.Combine(validationRoot, "v0.1", "rtthread.bin"));
            var actualBaseline = vm.PatchCatalog.Single(x => x.Md5 == baselineHash);
            await Import(Path.Combine(validationRoot, "v0.2", "rtthread.bin"));
            var actualTarget = vm.PatchCatalog.Single(x => x.Md5 == targetHash);
            Check(actualBaseline.NewVersion == 1 && actualTarget.NewVersion == 2 &&
                actualTarget.Length == validation.RootElement.GetProperty("target").GetProperty("image_size").GetInt64(),
                "真实完整固件导入没有保留版本、整包长度或 MD5。");
            vm.SelectedUpgradePatch = actualTarget;
            vm.SelectedReverseUpgradePatch = actualBaseline;
            vm.TargetIdList = "10001";
            var actualTemplate = await (Task<OtaTestPlanItemTemplate>)typeof(MainWindowViewModel)
                .GetMethod("BuildTestPlanItemTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vm, [OtaTestPlanExecutionKind.Cycle, 1])!;
            Check(actualTemplate.OldVersion == "1" && actualTemplate.NewVersion == "2" &&
                actualTemplate.ForwardPatch.Md5 == actualTarget.Md5 && actualTemplate.ReversePatch!.Md5 == actualBaseline.Md5,
                "真实固件版本对未正确绑定正式循环任务。");
        }
        await Reload();
        Check(vm.PatchCatalog.Any(x => x.FilePath == importedMcu.FilePath && x.NewVersion == 2), "目录恢复丢失 MCU 内嵌版本。");
        vm.SelectedUpgradePatch = vm.UpgradePatchChoices.Single(x => x.FilePath == importedMcu.FilePath);
        vm.SelectedReverseUpgradePatch = vm.UpgradePatchChoices.Single(x => x.FilePath == reverseFile);
        vm.TargetIdList = "10001";
        Check(vm.OldVersion == "1" && vm.NewVersion == "2" && vm.CanStartForwardUpgrade && vm.CanStartCycleUpgrade,
            "正式 MCU 版本对不能创建单次或循环任务。");
        var formalTemplate = await (Task<OtaTestPlanItemTemplate>)typeof(MainWindowViewModel)
            .GetMethod("BuildTestPlanItemTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [OtaTestPlanExecutionKind.Forward, 1])!;
        Check(formalTemplate.ForwardPatch.FullImageTargetVersion == 2 && OtaTestPlanVersionProjection.GetProjectedEndVersion(formalTemplate) == 2,
            "正式任务未推进预计运行版本。");
        var publishValidation = typeof(MainWindowViewModel).GetMethod("ValidatePatchForPublication", BindingFlags.NonPublic | BindingFlags.Instance)!;
        publishValidation.Invoke(vm, [importedMcu]);
        var knownPackage = IpmPackageMetadata.Read(importedMcu.FilePath)!;
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(importedMcu.FilePath), System.Text.Json.JsonSerializer.Serialize(knownPackage with { Version = 3 }));
        try { publishValidation.Invoke(vm, [importedMcu]); throw new Exception("发布接受了与内嵌版本不一致的清单。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(importedMcu.FilePath), System.Text.Json.JsonSerializer.Serialize(knownPackage));
        var invalidDiff = new PatchSelection("测试差分包", importedMcu.FilePath, importedMcu.Length,
            importedMcu.Md5, importedMcu.Sha256, true, otaDeviceType: DeviceType.Sync);
        try { publishValidation.Invoke(vm, [invalidDiff]); throw new Exception("缺少清单的差分包仍应禁止发布。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        var legacyFile = Path.Combine(root, "legacy.bin");
        await File.WriteAllBytesAsync(legacyFile, new byte[1024]);
        try { await Import(legacyFile); throw new Exception("导入接受了没有信息头的旧镜像。"); }
        catch (InvalidDataException) { }
        var appOnlyFile = Path.Combine(root, "app_only.bin");
        await File.WriteAllBytesAsync(appOnlyFile, McuImageFixture.App());
        try { await Import(appOnlyFile); throw new Exception("仅 APP 文件不应进入全包流程。"); }
        catch (InvalidDataException) { }
        foreach (var item in vm.PatchCatalog) item.IsSelectedForPublish = item.FilePath == importedMcu.FilePath;
        var publishCommand = typeof(MainWindowViewModel).GetMethod("PublishPatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        vm.PublicHttpBaseUrl = "https://example.invalid/ota/";
        await (Task)publishCommand.Invoke(vm, null)!;
        Check(vm.IsPatchDialogOpen && vm.PatchDialogMessage.Contains(importedMcu.FileName), "正式 MCU 完整包被差分清单门禁拦截。");
        typeof(MainWindowViewModel).GetMethod("CancelPatchDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        bytes[0x8000 + 1000] ^= 1;
        await File.WriteAllBytesAsync(importedMcu.FilePath, bytes);
        try { publishValidation.Invoke(vm, [importedMcu]); throw new Exception("发布前未拒绝篡改文件。"); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        await (Task)publishCommand.Invoke(vm, null)!;
        Check(!vm.IsPatchDialogOpen && vm.PublishStatus.Contains("内容与导入清单不一致"), "发布前未验证 MCU 文件哈希。");
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
        await CheckFirmwareImportsAsync(vm, root);
        await vm.DisposeAsync();
        var restored = await settings.LoadAsync();
        Check(restored.ActiveMode == "IPM" && restored.ModeWorkspaces["IPM"].GatewayId == "789" &&
            restored.ModeWorkspaces["EcoLink"].GatewayId == "456", "三模式保存失败。");
        Check(!(await File.ReadAllTextAsync(Path.Combine(root, "settings.json"))).Contains("ipmMcuUpgradeMode", StringComparison.OrdinalIgnoreCase),
            "保存设置仍包含已移除的 MCU 模式字段。");
        Console.WriteLine("IPM 界面模型、正式镜像导入、版本对、三模式切换及持久化测试通过。");
    }

    static async Task CheckFirmwareImportsAsync(MainWindowViewModel vm, string root)
    {
        vm.PatchOutputDirectory = Path.Combine(root, "identity_imports");
        var pairRoot = Environment.GetEnvironmentVariable("IPM_OTA_PAIR_DIR");
        async Task ImportFirmware(string path, bool old) => await (Task)typeof(MainWindowViewModel)
            .GetMethod("ImportFirmwareImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [path, old])!;
        foreach (var project in new[] { "gateway", "mcu_app", "wiota" })
        {
            var paths = new List<string>();
            foreach (byte version in new byte[] { 1, 2 })
            {
                string path;
                if (!string.IsNullOrEmpty(pairRoot))
                    path = Directory.GetFiles(Path.Combine(pairRoot, project), $"ipm_{project}_v0.{version}_*.bin").Single();
                else
                {
                    path = Path.Combine(root, $"{project}_v{version}.bin");
                    var bytes = project == "mcu_app" ? McuImageFixture.Factory(McuImageFixture.App(version: version))
                        : RadioFirmwareFixture(version, (byte)(project == "wiota" ? 2 : 8));
                    await File.WriteAllBytesAsync(path, bytes);
                }
                paths.Add(path);
            }
            await ImportFirmware(paths[0], true);
            await ImportFirmware(paths[1], false);
            Check(vm.OldVersion == "1" && vm.NewVersion == "2", project + " A/B 版本未正确读取。");
            if (project == "wiota")
            {
                Check(vm.CanGeneratePatch && vm.SelectedTaskType == "倾角定位-同步升级" &&
                    vm.OldImageIdentityDetail.Contains("WIoTa") && vm.OldImageIdentityDetail.Contains("类型 2"),
                    "WIoTa 仍被识别为异步扩展器。");
                Check(vm.ForwardPatchName.StartsWith("ipm-wiota-v1-to-v2-") &&
                    vm.ReversePatchName.StartsWith("ipm-wiota-v2-to-v1-"), "IPM 名称仍使用 EcoLink 前缀。");
                await (Task)typeof(MainWindowViewModel).GetMethod("GeneratePatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
                Check(vm.PatchCatalog.Any(x => !x.IsFullImage && x.OtaDeviceType == DeviceType.Sync && x.OldVersion == 1 && x.NewVersion == 2) &&
                    vm.PatchCatalog.Any(x => !x.IsFullImage && x.OtaDeviceType == DeviceType.Sync && x.OldVersion == 2 && x.NewVersion == 1),
                    "WIoTa 正反向 Patch 制作或清单类型不正确：" + vm.PatchStatus);
                foreach (var patch in vm.PatchCatalog.Where(x => !x.IsFullImage && x.OtaDeviceType == DeviceType.Sync))
                {
                    Check(patch.FileName.StartsWith("ipm-wiota-"), "实际差分文件仍使用旧前缀。");
                    var manifest = await OtaTool.Core.Diff.PackageManifestImporter.LoadAndValidateAsync(patch.FilePath, expectedMode: OtaMode.IPM);
                    Check(manifest.PatchType == "ipm-wiota" && manifest.Mode == OtaMode.IPM && manifest.DeviceTypeCode == 2,
                        "IPM 清单前缀、模式或镜像类型不一致。");
                }
            }
            else
            {
                var expected = project == "gateway" ? DeviceType.Gateway : DeviceType.Mcu;
                Check(!vm.CanGeneratePatch && vm.SelectedUpgradePatch?.OtaDeviceType == expected &&
                    vm.SelectedUpgradePatch.NewVersion == 2 && vm.SelectedReverseUpgradePatch?.NewVersion == 1,
                    project + " 完整固件未自动匹配正反向版本。");
                vm.PatchNameSuffix = "identitytest";
                foreach (var path in paths)
                {
                    var hash = await PatchMetadata.FromFileAsync(path);
                    var imported = vm.PatchCatalog.Single(x => x.IsFullImage && x.Sha256 == hash.Sha256 &&
                        Path.GetDirectoryName(x.FilePath) == vm.PatchOutputDirectory);
                    Check(imported.OtaDeviceType == expected && (await File.ReadAllBytesAsync(imported.FilePath)).SequenceEqual(await File.ReadAllBytesAsync(path)),
                        project + " 全包导入未保持源内容。");
                    typeof(MainWindowViewModel).GetMethod("ValidatePatchForPublication", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [imported]);
                }
                vm.TargetIdList = "10001";
                if (project == "gateway")
                {
                    // 模拟已查询到网关当前版本；保留正式任务的版本查询门禁。
                    typeof(MainWindowViewModel).GetField("_gatewaySoftwareVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, (byte)1);
                    typeof(MainWindowViewModel).GetField("_gatewayVersionGatewayId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, vm.GatewayId);
                }
                var template = await (Task<OtaTestPlanItemTemplate>)typeof(MainWindowViewModel)
                    .GetMethod("BuildTestPlanItemTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [OtaTestPlanExecutionKind.Cycle, 1])!;
                Check(template.DeviceType == expected && template.OldVersion == "1" && template.NewVersion == "2",
                    project + " 完整镜像不能用于循环升级计划。");
            }
        }
        var ipmDirectory = vm.PatchOutputDirectory;
        var ipmFiles = Directory.GetFiles(ipmDirectory).ToDictionary(path => path, File.ReadAllBytes);
        foreach (var mode in new[] { OtaMode.EcoLink, OtaMode.Traditional })
        {
            if (mode == OtaMode.EcoLink) vm.IsEcoLink = true;
            else vm.IsTraditional = true;
            await (Task)typeof(MainWindowViewModel).GetMethod("RestoreCurrentModePatchCatalogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
            vm.PatchOutputDirectory = Path.Combine(root, "naming_" + mode);
            vm.PatchNameSuffix = "independent";
            foreach (byte version in new byte[] { 1, 2 })
            {
                var source = Path.Combine(root, $"{mode}_sync_v{version}.bin");
                await File.WriteAllBytesAsync(source, RadioFirmwareFixture(version, 3, ipm: false));
                await ImportFirmware(source, version == 1);
            }
            Check(vm.CanGeneratePatch && vm.ForwardPatchName == "ext-s-v1-to-v2-independent.patch" &&
                vm.ReversePatchName == "ext-s-v2-to-v1-independent.patch", mode + " 原有名称受 IPM 改动影响。");
            await (Task)typeof(MainWindowViewModel).GetMethod("GeneratePatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
            var created = vm.PatchCatalog.Where(x => Path.GetDirectoryName(x.FilePath) == vm.PatchOutputDirectory).ToArray();
            Check(created.Length == 2 && created.All(x => x.FileName.StartsWith("ext-s-")), mode + " 正反向差分制作失败：" + vm.PatchStatus);
            foreach (var patch in created)
            {
                var manifest = await OtaTool.Core.Diff.PackageManifestImporter.LoadAndValidateAsync(patch.FilePath, expectedMode: mode);
                Check(manifest.Mode == mode && manifest.PatchType == "ext-s", mode + " 清单被 IPM 规则改写。");
            }
            Check(ipmFiles.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), mode + " 修改了 IPM 的归档。");
        }
        vm.IsIpm = true;
        await (Task)typeof(MainWindowViewModel).GetMethod("RestoreCurrentModePatchCatalogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
        Check(vm.PatchOutputDirectory == ipmDirectory && vm.ForwardPatchName.StartsWith("ipm-wiota-") &&
            vm.ReversePatchName.StartsWith("ipm-wiota-"), "切回 IPM 未恢复独立目录和前缀。");
        Console.WriteLine("IPM 网关、WIoTa、MCU 双版本导入、全包循环计划及 WIoTa 正反向差分验证通过。");
        Console.WriteLine("EcoLink、传统模式正反向差分还原及三模式目录、命名隔离验证通过。");
    }

    static byte[] RadioFirmwareFixture(byte version, byte type, bool ipm = true)
    {
        var bytes = Enumerable.Repeat((byte)0x11, 64 * 1024).ToArray();
        var table = FirmwareIdentityReader.BootloaderLength - 128;
        new byte[] { (byte)'U', (byte)'C', 4, (byte)'F' }.CopyTo(bytes, table);
        foreach (var (index, length) in new[] { (0, FirmwareIdentityReader.BootloaderLength), (1, 8 * 1024), (2, 8 * 1024), (3, 16 * 1024) })
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + 4 + index * 4), length);
        new byte[] { version, (byte)'e', (byte)'c', (byte)'o', type }.CopyTo(bytes, FirmwareIdentityReader.IdentityOffset);
        for (var offset = FirmwareIdentityReader.BootloaderLength; offset < bytes.Length; offset += 97)
            bytes[offset] = version;
        if (ipm)
            new byte[] { (byte)'I', (byte)'P', (byte)'M', type, version }.CopyTo(bytes, IpmImageIdentity.Offset);
        return bytes;
    }

    sealed class MemorySecrets : ISecretStore
    {
        readonly Dictionary<string, string> values = [];
        public void Save(string name, string secret) => values[name] = secret;
        public bool TryGet(string name, out string? secret) => values.TryGetValue(name, out secret);
        public void Delete(string name) => values.Remove(name);
    }
}
