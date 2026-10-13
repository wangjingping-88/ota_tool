using System.Buffers.Binary;
using System.Text.Json;
using OtaTool.Core.Models;
using OtaTool.Core.Protocols;
using OtaTool.Core.Execution;
using OtaTool.Core.Settings;
using OtaTool.Core.Diff;

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
        await CheckFirmwareIdentitiesAsync(root);
        await IpmMcuProgressTimeoutTests.RunAsync(root);
        Check(!profile.SupportsStartAck && profile.SupportsGatewayStatusPolling &&
            profile.SupportedDeviceTypes.SetEquals([DeviceType.Gateway, DeviceType.Sync, DeviceType.Mcu]), "协议能力错误。");
        var file = Path.Combine(root, "ipm_mcu.bin");
        var valid = McuImageFixture.Factory(McuImageFixture.App(229376));
        foreach (var invalid in new[] { McuImageFixture.App(0), McuImageFixture.App(519), McuImageFixture.App(229377),
            McuImageFixture.App(stack: 0x20000000), McuImageFixture.App(stack: 0x20008008),
            McuImageFixture.App(stack: 0x20007FFF), McuImageFixture.App(reset: 0x08008208),
            McuImageFixture.App(reset: 0x08008009), McuImageFixture.App(reset: 0x0800A201),
            McuImageFixture.App(version: 0), McuImageFixture.App(version: 255) })
        {
            try { McuAppImageReader.Parse(McuImageFixture.Factory(invalid)); throw new Exception("非法 MCU 镜像未拒绝。"); }
            catch (InvalidDataException) { }
        }
        foreach (var (offset, value) in new (int, uint)[] { (8, 0), (10, 32), (16, 0), (28, 1),
            (32, 0x08000000), (36, 0), (40, 1000), (48, 2), (50, 0), (50, 3), (52, 1), (56, 1), (64, 0) })
        {
            var corrupt = McuImageFixture.App();
            McuImageFixture.Put(corrupt, offset, value);
            McuImageFixture.Seal(corrupt);
            try { McuAppImageReader.Parse(McuImageFixture.Factory(corrupt)); throw new Exception($"非法头字段 {offset} 未拒绝。"); }
            catch (InvalidDataException) { }
        }
        foreach (var offset in new[] { 12, 60, 1000 })
        {
            var corrupt = McuImageFixture.App(); corrupt[offset] ^= 1;
            try { McuAppImageReader.Parse(McuImageFixture.Factory(corrupt)); throw new Exception("CRC 错误未拒绝。"); }
            catch (InvalidDataException) { }
        }
        Check(McuAppImageReader.Parse(McuImageFixture.Factory(McuImageFixture.App(8192, reset: 0x08008201))).AppBytes.Length == 8192 &&
            McuAppImageReader.Parse(McuImageFixture.Factory(McuImageFixture.App(version: 254))).Version == 254, "最小长度或版本上界未通过。");
        var parsed = McuAppImageReader.Parse(valid);
        Check(parsed.Version == 2 && parsed.DeviceId == "IPM_GD32L233" && parsed.LinkAddress == 0x08000000 &&
            parsed.AppBytes.SequenceEqual(valid.AsSpan(0x8000).ToArray()), "完整固件识别错误。");
        try { McuAppImageReader.Parse(parsed.AppBytes); throw new Exception("仅 APP 包不应作为统一全包。"); }
        catch (InvalidDataException) { }
        var badBoot = valid.ToArray(); McuImageFixture.Put(badBoot, 4, 0x08008001);
        try { McuAppImageReader.Parse(badBoot); throw new Exception("非法 UBoot 向量未拒绝。"); }
        catch (InvalidDataException) { }
        var validationRoot = Environment.GetEnvironmentVariable("IPM_MCU_VALIDATION_DIR");
        if (!string.IsNullOrEmpty(validationRoot))
        {
            using var validation = JsonDocument.Parse(File.ReadAllText(Path.Combine(validationRoot, "validation.json")));
            foreach (byte version in new byte[] { 1, 2 })
            {
                var filePath = Path.Combine(validationRoot, $"v0.{version}", "rtthread.bin");
                var actual = McuAppImageReader.Read(filePath);
                var actualHash = await PatchMetadata.FromFileAsync(filePath);
                var expected = validation.RootElement.GetProperty(version == 1 ? "baseline" : "target");
                Check(actual.Version == version && actualHash.Length == expected.GetProperty("image_size").GetInt64() &&
                    actualHash.Md5 == expected.GetProperty("md5").GetString() &&
                    actual.PackageCrc32.ToString("x8") == expected.GetProperty("package_crc32").GetString(),
                    "真实整包版本、长度、MD5 或 CRC32 与独立构建器不一致。");
            }
        }
        await File.WriteAllBytesAsync(file, valid);
        IpmPackageMetadata.ValidateMcuVectors(file, IpmPackageMetadata.FirmwareAddress);
        try { IpmPackageMetadata.ValidateMcuVectors(file, 0x08008000); throw new Exception("错误链接地址未拒绝。"); }
        catch (InvalidDataException) { }
        var metadata = await IpmPackageMetadata.CreateAsync(file, DeviceType.Mcu, PackageKind.FullImage, 2, IpmPackageMetadata.FirmwareAddress);
        Check(IpmPackageMetadata.ValidateForCatalog(file) == metadata,
            "已识别 MCU 包的本地记录未正确恢复。");
        foreach (var invalidMetadata in new[]
        {
            metadata with { Mode = OtaMode.EcoLink },
            metadata with { DeviceType = DeviceType.Gateway },
            metadata with { PackageKind = PackageKind.Patch },
            metadata with { Version = 0 },
            metadata with { Version = 255 },
            metadata with { LinkAddress = 0x08008000 },
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
        OtaTask TaskFor(DeviceType type = DeviceType.Mcu, TargetScope scope = TargetScope.SpecifiedIds,
            string[]? ids = null, PackageKind kind = PackageKind.FullImage, int? nodeType = null,
            string? packagePath = null, IpmPackageMetadata? packageMetadata = null) => new()
        {
            Mode = OtaMode.IPM, DeviceType = type, GatewayId = "123456", OldVersion = "1", NewVersion = "2",
            Target = new OtaTaskTarget(scope, ids ?? ["10001", "10002"]), PackageKind = kind,
            PatchPath = packagePath ?? file, PatchUrl = "http://127.0.0.1:8080/ipm_mcu.bin",
            PatchMd5 = (packageMetadata ?? metadata).Md5,
            PatchSha256 = (packageMetadata ?? metadata).Sha256, PackageMetadata = packageMetadata ?? metadata, NodeType = nodeType,
            ProtocolProfileId = OtaProtocolProfiles.Id(OtaMode.IPM), Timeout = TimeSpan.FromSeconds(5),
        };
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(file), JsonSerializer.Serialize(metadata with { Version = 3 }));
        try { IpmPackageMetadata.Validate(file, DeviceType.Mcu, PackageKind.FullImage, 3); throw new Exception("清单伪造版本未拒绝。"); }
        catch (InvalidDataException) { }
        try { await IpmPackageMetadata.CreateAsync(file, DeviceType.Mcu, PackageKind.FullImage, 3, IpmPackageMetadata.FirmwareAddress); throw new Exception("导入不能覆盖内嵌版本。"); }
        catch (InvalidDataException) { }
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(file), JsonSerializer.Serialize(metadata));
        var legacyFile = Path.Combine(root, "legacy_mcu.bin");
        var legacyBytes = new byte[1024];
        McuImageFixture.Put(legacyBytes, 0, 0x20008000); McuImageFixture.Put(legacyBytes, 4, 0x08000009);
        await File.WriteAllBytesAsync(legacyFile, legacyBytes);
        var legacyHash = await PatchMetadata.FromFileAsync(legacyFile);
        var legacyMetadata = metadata with { Length = legacyHash.Length, Md5 = legacyHash.Md5, Sha256 = legacyHash.Sha256 };
        await File.WriteAllTextAsync(IpmPackageMetadata.SidecarPath(legacyFile), JsonSerializer.Serialize(legacyMetadata));
        Check(!OtaTaskValidator.Validate(TaskFor(packagePath: legacyFile, packageMetadata: legacyMetadata), profile).IsValid,
            "历史无 UBoot 镜像不应进入正式升级。");
        var task = TaskFor();
        Check(OtaTaskValidator.Validate(task, profile).IsValid, "合法 MCU 包未通过。");
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
            PatchCapacityPolicy.Check(DeviceType.Mcu, 0x40000).IsAllowed && !PatchCapacityPolicy.Check(DeviceType.Mcu, 0x40001).IsAllowed, "容量门限串扰。");
        await File.WriteAllBytesAsync(file, new byte[32]);
        Check(!OtaTaskValidator.Validate(task, profile).IsValid, "已替换镜像未被哈希校验拒绝。");
        await File.WriteAllBytesAsync(file, valid);

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

    private static async Task CheckFirmwareIdentitiesAsync(string root)
    {
        foreach (var (code, expected, ecoExpected) in new[]
        {
            ((byte)2, DeviceType.Sync, FirmwareDeviceType.ExtenderA),
            ((byte)8, DeviceType.Gateway, FirmwareDeviceType.Dtu),
        })
        {
            var paths = new List<string>();
            foreach (byte version in new byte[] { 1, 2 })
            {
                var path = Path.Combine(root, $"ipm_type{code}_v{version}.bin");
                var data = new byte[64 * 1024];
                new byte[] { version, (byte)'e', (byte)'c', (byte)'o', code }.CopyTo(data, FirmwareIdentityReader.IdentityOffset);
                new byte[] { (byte)'I', (byte)'P', (byte)'M', code, version }.CopyTo(data, IpmImageIdentity.Offset);
                await File.WriteAllBytesAsync(path, data);
                paths.Add(path);
                var ipm = await FirmwareIdentityReader.ReadAsync(path, mode: OtaMode.IPM);
                foreach (var otherMode in new[] { OtaMode.EcoLink, OtaMode.Traditional })
                {
                    try { await FirmwareIdentityReader.ReadAsync(path, mode: otherMode); throw new Exception("其他模式接受了新 IPM 固件。"); }
                    catch (InvalidDataException) { }
                }
                var ecoPath = Path.Combine(root, $"eco_type{code}_v{version}.bin");
                data.AsSpan(IpmImageIdentity.Offset, IpmImageIdentity.Length).Clear();
                await File.WriteAllBytesAsync(ecoPath, data);
                var eco = await FirmwareIdentityReader.ReadAsync(ecoPath);
                Check(ipm.OtaDeviceType == expected && ipm.Version == version && ipm.ImageDeviceTypeCode == code,
                    "IPM 类型或版本识别错误。");
                Check(eco.DeviceType == ecoExpected, "IPM 修复改变了 EcoLink 类型表。");
                try { ipm.EnsureCompatibleWith(eco); throw new Exception("跨模式镜像未拒绝。"); }
                catch (InvalidOperationException) { }
            }
            if (code == 2)
            {
                var patch = await PatchMetadata.FromFileAsync(paths[0]);
                var request = new DiffRequest(paths[0], paths[1], Path.Combine(root, "sync.patch"), DeviceType.Sync,
                    "1", "2", Mode: OtaMode.IPM);
                var manifest = await PackageManifestFactory.CreateAsync(new("test", "1", "", true, ""), request, patch, true);
                Check(manifest.OtaDeviceType == DeviceType.Sync && manifest.DeviceTypeCode == 2 &&
                    manifest.PatchType == "ipm-wiota" && manifest.Mode == OtaMode.IPM,
                    "WIoTa 差分清单仍按异步类型生成。");
                Check(FirmwarePatchNaming.GetPrefix(FirmwareDeviceType.ExtenderS) == "ext-s" &&
                    FirmwarePatchNaming.GetPrefix(FirmwareDeviceType.ExtenderS, OtaMode.Traditional) == "ext-s" &&
                    FirmwarePatchNaming.CreateName(manifest.PatchType, 1, 2, "jpwang") == "ipm-wiota-v1-to-v2-jpwang.patch",
                    "IPM 命名改变了其他模式的前缀。");
                await PackageManifestExporter.ExportAsync(manifest, paths[0] + ".json");
                await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: OtaMode.IPM);
                foreach (var otherMode in new[] { OtaMode.EcoLink, OtaMode.Traditional })
                {
                    try { await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: otherMode); throw new Exception("其他模式接受了 IPM 差分包。"); }
                    catch (InvalidDataException) { }
                    var original = manifest with { PatchType = "ext-s", DeviceTypeCode = 3, Mode = otherMode };
                    await PackageManifestExporter.ExportAsync(original, paths[0] + ".json");
                    await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: otherMode);
                    try { await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: OtaMode.IPM); throw new Exception("IPM 接受了其他模式的新清单。"); }
                    catch (InvalidDataException) { }
                    await PackageManifestExporter.ExportAsync(manifest, paths[0] + ".json");
                }
                await PackageManifestExporter.ExportAsync(manifest with { PatchType = "ext-s", DeviceTypeCode = 3, Mode = null }, paths[0] + ".json");
                await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: OtaMode.EcoLink);
                await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: OtaMode.Traditional);
                try { await PackageManifestImporter.LoadAndValidateAsync(paths[0], expectedMode: OtaMode.IPM); throw new Exception("IPM 接受了没有模式标识的旧差分包。"); }
                catch (InvalidDataException) { }
            }
        }
        var mcuPath = Path.Combine(root, "ipm_identity_mcu.bin");
        await File.WriteAllBytesAsync(mcuPath, McuImageFixture.Factory(McuImageFixture.App(version: 1)));
        var mcu = await FirmwareIdentityReader.ReadAsync(mcuPath, mode: OtaMode.IPM);
        Check(mcu.OtaDeviceType == DeviceType.Mcu && mcu.Version == 1 && mcu.DeviceId == "IPM_GD32L233",
            "统一导入入口未识别 MCU 信息头。");
        var invalidPath = Path.Combine(root, "ipm_invalid_type.bin");
        foreach (var (version, code) in new (byte, byte)[] { (1, 3), (1, 1), (0, 2), (255, 8), (1, 10) })
        {
            var bytes = new byte[64 * 1024];
            new byte[] { version, (byte)'e', (byte)'c', (byte)'o', code }.CopyTo(bytes, FirmwareIdentityReader.IdentityOffset);
            new byte[] { (byte)'I', (byte)'P', (byte)'M', code, version }.CopyTo(bytes, IpmImageIdentity.Offset);
            await File.WriteAllBytesAsync(invalidPath, bytes);
            try { await FirmwareIdentityReader.ReadAsync(invalidPath, mode: OtaMode.IPM); throw new Exception("IPM 非法类型或版本未拒绝。"); }
            catch (InvalidDataException) { }
        }
        var mismatch = McuImageFixture.App();
        mismatch[IpmImageIdentity.Offset - 0x8000 + 4] = 1;
        McuImageFixture.Seal(mismatch);
        try { McuAppImageReader.Parse(McuImageFixture.Factory(mismatch)); throw new Exception("两个 MCU 版本字段不一致未拒绝。"); }
        catch (InvalidDataException) { }
    }
}
