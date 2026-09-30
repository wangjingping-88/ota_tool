using System.Buffers.Binary;
using System.Text.Json;
using OtaTool.Core.Models;
using OtaTool.Core.Protocols;
using OtaTool.Core.Execution;
using OtaTool.Core.Settings;

static class IpmTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("IPM：" + message);
    }

    public static async Task RunAsync(string root)
    {
        Check((int)OtaMode.EcoLink == 1 && (int)OtaMode.IPM == 2 && (int)DeviceType.Node == 3 && (int)DeviceType.Mcu == 4,
            "枚举必须追加，旧值不可改变。");
        var profile = new IpmProtocolProfile();
        Check(!profile.SupportsStartAck && profile.SupportsGatewayStatusPolling &&
            profile.SupportedDeviceTypes.SetEquals([DeviceType.Gateway, DeviceType.Sync, DeviceType.Mcu]), "协议能力错误。");
        var file = Path.Combine(root, "ipm_mcu.bin");
        static byte[] Image(int length, uint stack = 0x20008000, uint reset = 0x08008009)
        {
            var bytes = new byte[length];
            if (length >= 8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, stack);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), reset);
            }
            return bytes;
        }
        foreach (var sample in new[] { Image(0), Image(7), Image(229377), Image(32, 0x20000000),
            Image(32, 0x20008008), Image(32, 0x20007FFF), Image(32, reset: 0x08008008),
            Image(32, reset: 0x08000009), Image(32, reset: 0x08008021) })
        {
            await File.WriteAllBytesAsync(file, sample);
            try { IpmPackageMetadata.ValidateMcuVectors(file, IpmPackageMetadata.AppAddress); throw new Exception("非法向量未拒绝。"); }
            catch (InvalidDataException) { }
        }
        await File.WriteAllBytesAsync(file, Image(229376));
        IpmPackageMetadata.ValidateMcuVectors(file, IpmPackageMetadata.AppAddress);
        try { IpmPackageMetadata.ValidateMcuVectors(file, 0x08000000); throw new Exception("错误链接地址未拒绝。"); }
        catch (InvalidDataException) { }
        var metadata = await IpmPackageMetadata.CreateAsync(file, DeviceType.Mcu, PackageKind.FullImage, 2, IpmPackageMetadata.AppAddress);
        Check(IpmPackageMetadata.ValidateForCatalog(file) == metadata,
            "已识别 MCU 包的本地记录未正确恢复。");
        foreach (var invalidMetadata in new[]
        {
            metadata with { Mode = OtaMode.EcoLink },
            metadata with { DeviceType = DeviceType.Gateway },
            metadata with { PackageKind = PackageKind.Patch },
            metadata with { Version = 0 },
            metadata with { Version = 255 },
            metadata with { LinkAddress = 0x08000000 },
            metadata with { Sha256 = new string('0', 64) },
        })
        {
            await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(file), JsonSerializer.Serialize(invalidMetadata));
            try { IpmPackageMetadata.Validate(file, DeviceType.Mcu, PackageKind.FullImage, metadata.Version); throw new Exception("非法 MCU 任务包未拒绝。"); }
            catch (InvalidDataException) { }
        }
        File.Delete(IpmPackageMetadata.SidecarPath(file));
        try { IpmPackageMetadata.Validate(file, DeviceType.Mcu, PackageKind.FullImage, metadata.Version); throw new Exception("无本地记录的 MCU 镜像不应执行升级。"); }
        catch (InvalidDataException) { }
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(file), JsonSerializer.Serialize(metadata));
        var pendingFile = Path.Combine(root, "mcu_without_version.bin");
        await File.WriteAllBytesAsync(pendingFile, Image(64, reset: 0x08000009));
        var pending = await IpmPackageMetadata.CreatePendingMcuImageAsync(pendingFile);
        Check(pending.Version == 0 && pending.LinkAddress == 0 &&
            IpmPackageMetadata.ValidateForCatalog(pendingFile) == pending, "未知 MCU 镜像未按待识别状态保存和恢复。");
        try { IpmPackageMetadata.Validate(pendingFile, DeviceType.Mcu, PackageKind.FullImage, 0); throw new Exception("未知版本包不应执行升级。"); }
        catch (InvalidDataException) { }
        var downloadTest = await IpmPackageMetadata.SetDownloadTestVersionAsync(pendingFile, 2);
        Check(downloadTest.IsDownloadTest && downloadTest.Version == 2 &&
            IpmPackageMetadata.Validate(pendingFile, DeviceType.Mcu, PackageKind.FullImage, 2) == downloadTest,
            "直启镜像不能用于明确标记的下载联调。");
        try { IpmPackageMetadata.ValidateMcuVectors(pendingFile, IpmPackageMetadata.AppAddress); throw new Exception("正式升级错误放行直启镜像。"); }
        catch (InvalidDataException) { }
        try { await IpmPackageMetadata.SetDownloadTestVersionAsync(pendingFile, 255); throw new Exception("非法联调版本未拒绝。"); }
        catch (InvalidDataException) { }
        try { await IpmPackageMetadata.SetDownloadTestVersionAsync(file, 3); throw new Exception("已识别版本不应被手填参数覆盖。"); }
        catch (InvalidDataException) { }
        await File.WriteAllBytesAsync(pendingFile, Image(65));
        try { IpmPackageMetadata.ValidateForCatalog(pendingFile); throw new Exception("未知版本包也必须检查导入后的内容变化。"); }
        catch (InvalidDataException) { }
        await File.WriteAllBytesAsync(pendingFile, Image(64, reset: 0x08000009));
        OtaTask TaskFor(DeviceType type = DeviceType.Mcu, TargetScope scope = TargetScope.SpecifiedIds,
            string[]? ids = null, PackageKind kind = PackageKind.FullImage, int? nodeType = null,
            IpmMcuUpgradeMode mcuMode = IpmMcuUpgradeMode.Formal,
            string? packagePath = null, IpmPackageMetadata? packageMetadata = null) => new()
        {
            Mode = OtaMode.IPM, DeviceType = type, GatewayId = "123456", OldVersion = "1", NewVersion = "2",
            Target = new OtaTaskTarget(scope, ids ?? ["10001", "10002"]), PackageKind = kind,
            PatchPath = packagePath ?? file, PatchUrl = "http://127.0.0.1:8080/ipm_mcu.bin",
            PatchMd5 = (packageMetadata ?? metadata).Md5,
            PatchSha256 = (packageMetadata ?? metadata).Sha256, PackageMetadata = packageMetadata ?? metadata, NodeType = nodeType,
            IpmMcuUpgradeMode = mcuMode,
            ProtocolProfileId = OtaProtocolProfiles.Id(OtaMode.IPM), Timeout = TimeSpan.FromSeconds(5),
        };
        var task = TaskFor();
        Check(OtaTaskValidator.Validate(task, profile).IsValid, "合法 MCU 包未通过。");
        Check(!OtaTaskValidator.Validate(TaskFor(mcuMode: IpmMcuUpgradeMode.NoUbootValidation), profile).IsValid,
            "无 uboot 验证模式不应接受正式 MCU 包。");
        Check(OtaTaskValidator.Validate(TaskFor(mcuMode: IpmMcuUpgradeMode.NoUbootValidation,
            packagePath: pendingFile, packageMetadata: downloadTest), profile).IsValid,
            "无 uboot 验证模式应接受明确标记的下载联调包。");
        Check(!OtaTaskValidator.Validate(TaskFor(packagePath: pendingFile,
            packageMetadata: downloadTest), profile).IsValid,
            "正式模式不应接受下载联调包。");
        foreach (var invalid in new[] { TaskFor(ids: []), TaskFor(ids: ["1", "01"]), TaskFor(ids: ["0"]),
            TaskFor(ids: ["abc"]), TaskFor(ids: Enumerable.Range(1, 17).Select(x => x.ToString()).ToArray()),
            TaskFor(scope: TargetScope.Broadcast, ids: []), TaskFor(kind: PackageKind.Patch), TaskFor(nodeType: 2),
            TaskFor(type: DeviceType.Async), TaskFor(type: DeviceType.Node) })
        {
            Check(!OtaTaskValidator.Validate(invalid, profile).IsValid, "非法任务未被拒绝。");
            try { OtaMessageCodec.CreateUpgradeRequest(invalid, 1); throw new Exception("编码器未拒绝非法 IPM 契约。"); }
            catch (InvalidOperationException) { }
        }
        using (var payload = JsonDocument.Parse(OtaMessageCodec.CreateUpgradeRequest(task, 1, new(OtaType: 1)).JsonPayload))
        {
            var ota = payload.RootElement.GetProperty("ota");
            Check(ota.GetProperty("type").GetInt32() == 0 && ota.GetProperty("range").GetInt32() == 1 &&
                ota.GetProperty("dev_type").GetString() == "ex_mcu", "MCU 请求被默认差分配置覆盖。");
            Check(!ota.TryGetProperty("node_type", out _) && !ota.TryGetProperty("node_retry_count", out _) &&
                !ota.GetProperty("targets")[0].TryGetProperty("nodes", out _), "MCU 携带节点字段。");
            Check(ota.GetProperty("targets")[0].GetProperty("dev_id").ValueKind == JsonValueKind.Number, "目标 ID 必须是数值。");
        }
        foreach (var type in new[] { DeviceType.Gateway, DeviceType.Sync })
        {
            using var payload = JsonDocument.Parse(OtaMessageCodec.CreateUpgradeRequest(TaskFor(type,
                TargetScope.Broadcast, [], type == DeviceType.Sync ? PackageKind.Patch : PackageKind.FullImage), 2).JsonPayload);
            var ota = payload.RootElement.GetProperty("ota");
            Check(!ota.TryGetProperty("targets", out _), "广播或网关不应携带目标。");
            Check(ota.GetProperty("type").GetInt32() == (type == DeviceType.Sync ? 1 : 0), "包类型错误。");
            Check(ota.GetProperty("range").GetInt32() == (type == DeviceType.Sync ? 0 : 1), "范围编码错误。");
        }
        Check(PatchCapacityPolicy.Check(DeviceType.Async, 0x2F001).IsAllowed == false &&
            PatchCapacityPolicy.Check(DeviceType.Sync, 0xD001).IsAllowed == false &&
            PatchCapacityPolicy.Check(DeviceType.Mcu, 0x38000).IsAllowed, "容量门限串扰。");
        await File.WriteAllBytesAsync(file, Image(32));
        Check(!OtaTaskValidator.Validate(task, profile).IsValid, "已替换镜像未被哈希校验拒绝。");
        await File.WriteAllBytesAsync(file, Image(229376));

        // 无 cmd=10：直接查询；先超时、再明确不存在，重发保持同序号同参数。
        var transport = new FakeMqttTransport();
        var queries = 0;
        var starts = new List<string>();
        var terminal = new TaskCompletionSource<OtaExecutionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runner = new OtaTaskRunner(transport, profile, new InMemoryTaskSequenceStore(),
            pollingOptions: new(TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(30)));
        runner.Updated += (_, update) => { if (update.State is OtaTaskState.Succeeded or OtaTaskState.Failed) terminal.TrySetResult(update); };
        transport.OnPublished = message =>
        {
            using var json = JsonDocument.Parse(message.GetPayloadAsUtf8());
            var cmd = json.RootElement.GetProperty("cmd").GetInt32();
            if (cmd == 5) starts.Add(message.Topic + message.GetPayloadAsUtf8());
            if (cmd == 8)
            {
                queries++;
                var query = json.RootElement.GetProperty("ota_status");
                var q = query.GetProperty("query_seq").GetInt32();
                Check(query.GetProperty("task_seq").GetInt32() == 1, "查询覆盖了原始任务序号。");
                if (queries == 1) return Task.CompletedTask;
                if (queries == 2) Check(starts.Count == 1, "查询超时不应重发。");
                var result = queries == 2 ? "TASK_NOT_FOUND" : "OK";
                var state = queries >= 5 ? "SUCCESS" : "RUNNING";
                var stage = queries == 3 ? "COMMIT" : queries == 4 ? "BOOT_VERIFY" : "FINISHED";
                if (queries == 3)
                    transport.Inject("ucchip/up/sgw/123456/900", "{\"cmd\":6,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"ex_mcu\",\"prompt\":\"upgrade process has end!\"}");
                if (queries is 4 or 5) Check(!terminal.Task.IsCompleted, "COMMIT/BOOT_VERIFY 不应提前成功。");
                var body = $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{q},\"task_seq\":1,\"session_id\":77,\"result\":\"{result}\",\"status\":\"{state}\",\"stage\":\"{stage}\",\"file_size\":229376,\"transferred_bytes\":229376}}}}";
                transport.Inject("ucchip/up/sgw/999999/1", body.Replace("RUNNING", "SUCCESS"));
                Check(!terminal.Task.IsCompleted, "其他网关消息影响了任务。");
                if (queries >= 4)
                {
                    transport.Inject("ucchip/up/sgw/123456/1", body.Replace("77", "78").Replace("RUNNING", "SUCCESS"));
                    Check(!terminal.Task.IsCompleted, "错误 SID 消息影响了任务。");
                }
                transport.Inject("ucchip/up/sgw/123456/900", body);
            }
            return Task.CompletedTask;
        };
        Check((await runner.StartAsync(task)).State == OtaTaskState.Running, "启动失败。");
        var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(final.State == OtaTaskState.Succeeded && starts.Count == 2 && starts[0] == starts[1], "受理重试不幂等。");
        Check(transport.Subscriptions.Single() == "ucchip/up/sgw/123456/#", "未固定上行主题。");

        var cancelTransport = new FakeMqttTransport();
        var allowFinal = false;
        var cancelSent = false;
        var cancelledLocally = false;
        var cancelTerminal = new TaskCompletionSource<OtaExecutionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancelRunner = new OtaTaskRunner(cancelTransport, profile, new InMemoryTaskSequenceStore(),
            pollingOptions: new(TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)));
        cancelRunner.Updated += (_, update) =>
        {
            if (update.State == OtaTaskState.Cancelled) cancelledLocally = true;
            if (update.State == OtaTaskState.Succeeded) cancelTerminal.TrySetResult(update);
        };
        cancelTransport.OnPublished = message =>
        {
            using var json = JsonDocument.Parse(message.GetPayloadAsUtf8());
            if (json.RootElement.GetProperty("cmd").GetInt32() == 5)
                cancelSent |= json.RootElement.GetProperty("ota").GetProperty("active").GetInt32() == 0;
            else
            {
                var q = json.RootElement.GetProperty("ota_status").GetProperty("query_seq").GetInt32();
                cancelTransport.Inject("ucchip/up/sgw/123456/9", $"{{\"cmd\":9,\"ota_status\":{{\"query_seq\":{q},\"task_seq\":1,\"session_id\":77,\"result\":\"OK\",\"status\":\"{(allowFinal ? "SUCCESS" : "RUNNING")}\",\"stage\":\"COMMIT\"}}}}");
            }
            return Task.CompletedTask;
        };
        await cancelRunner.StartAsync(task);
        await cancelRunner.CancelAndNotifyGatewayAsync();
        Check(cancelSent && cancelRunner.HasActiveTask && !cancelledLocally, "取消请求不能伪造设备取消终态。");
        allowFinal = true;
        await cancelTerminal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rejectionTransport = new FakeMqttTransport();
        await using var rejectionRunner = new OtaTaskRunner(rejectionTransport, profile, new InMemoryTaskSequenceStore(),
            pollingOptions: new(TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)));
        var rejected = new TaskCompletionSource<OtaExecutionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        rejectionRunner.Updated += (_, update) => { if (update.State == OtaTaskState.Failed) rejected.TrySetResult(update); };
        await rejectionRunner.StartAsync(task);
        rejectionTransport.Inject("ucchip/up/sgw/123456/1", "{\"cmd\":6,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"ex_mcu\",\"prompt\":\"RESOURCE_UNAVAILABLE\"}");
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var store = new JsonSettingsStore(Path.Combine(root, "ipm-settings.json"));
        await store.SaveAsync(new AppSettings { ActiveMode = "IPM", ModeWorkspaces = new Dictionary<string, ModeWorkspaceSettings>
        {
            ["IPM"] = new() { TargetIdList = "10001", OldVersion = "1", SelectedTaskType = "倾角定位-MCU升级" },
            ["EcoLink"] = new() { TargetIdList = "20001", OldVersion = "8" },
        }});
        var settings = await store.LoadAsync();
        Check(settings.ActiveMode == "IPM" && settings.ModeWorkspaces["IPM"].TargetIdList == "10001" &&
            settings.ModeWorkspaces["EcoLink"].OldVersion == "8", "模式持久化不独立。");
        Console.WriteLine("IPM 完整镜像、协议约束、受理重试、状态隔离与工作区测试通过。");
    }
}
