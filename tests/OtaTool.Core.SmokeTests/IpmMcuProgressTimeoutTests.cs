using System.Diagnostics;
using System.Text.Json;
using OtaTool.Core.Execution;
using OtaTool.Core.Models;
using OtaTool.Core.Protocols;

static class IpmMcuProgressTimeoutTests
{
    public static async Task RunAsync(string root)
    {
        var path = Path.Combine(root, "mcu_timeout.bin");
        await File.WriteAllBytesAsync(path, McuImageFixture.Factory(McuImageFixture.App()));
        var metadata = await IpmPackageMetadata.CreateAsync(path, DeviceType.Mcu, PackageKind.FullImage,
            2, IpmPackageMetadata.FirmwareAddress);
        OtaTask CreateTask() => new()
        {
            Mode = OtaMode.IPM, DeviceType = DeviceType.Mcu, PackageKind = PackageKind.FullImage,
            GatewayId = "123456", OldVersion = "1", NewVersion = "2",
            Target = new(TargetScope.SpecifiedIds, ["10001"]),
            PatchPath = path, PatchUrl = "http://127.0.0.1:8080/mcu_timeout.bin",
            PatchMd5 = metadata.Md5, PatchSha256 = metadata.Sha256, PackageMetadata = metadata,
            ProtocolProfileId = OtaProtocolProfiles.Id(OtaMode.IPM), Timeout = TimeSpan.FromSeconds(1),
        };
        var polling = new OtaPollingOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

        // 缩短时间重现慢速广播：总耗时超过 Timeout，但字节和阶段持续推进。
        await using var mqtt = new FakeMqttTransport();
        await using var runner = new OtaTaskRunner(mqtt, new IpmProtocolProfile(),
            new InMemoryTaskSequenceStore(), pollingOptions: polling);
        var clock = Stopwatch.StartNew();
        var sentEndPrompt = false;
        var sawBootVerify = false;
        var wireTimeout = 0;
        mqtt.OnPublished = message =>
        {
            using var json = JsonDocument.Parse(message.GetPayloadAsUtf8());
            if (json.RootElement.GetProperty("cmd").GetInt32() == 5)
            {
                wireTimeout = json.RootElement.GetProperty("ota").GetProperty("timeout_s").GetInt32();
                clock.Restart();
            }
            else
            {
                var q = json.RootElement.GetProperty("ota_status").GetProperty("query_seq").GetInt32();
                var elapsed = clock.Elapsed.TotalMilliseconds;
                var succeeded = elapsed >= 1500;
                var boot = elapsed >= 1100;
                var stage = succeeded ? "FINISHED" : boot ? "BOOT_VERIFY" : "TRANSFER";
                var bytes = boot ? metadata.Length : (long)(elapsed / 150) * 512;
                if (boot && !succeeded)
                {
                    sawBootVerify = true;
                    Check(runner.HasActiveTask, "总耗时超过窗口后，MCU 队列任务被提前关闭。");
                    if (!sentEndPrompt)
                    {
                        mqtt.Inject("ucchip/up/sgw/123456/1",
                            "{\"cmd\":6,\"old_ver\":1,\"new_ver\":2,\"dev_type\":\"ex_mcu\",\"prompt\":\"upgrade process has end!\"}");
                        sentEndPrompt = true;
                        Check(runner.HasActiveTask, "cmd=6 提示不能直接判定 MCU 成功。");
                    }
                }
                Inject(mqtt, q, 77, succeeded ? "SUCCESS" : "RUNNING", stage, bytes, metadata.Length);
            }
            return Task.CompletedTask;
        };
        var result = await runner.StartAndWaitAsync(CreateTask(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.State == OtaTaskState.Succeeded && sawBootVerify && sentEndPrompt && wireTimeout == 1,
            "持续推进的 MCU 升级未由同会话最终成功闭环，或修改了下游 timeout_s。");

        // 停滞时仍有心跳、耗时增长、错误 SID 和越界字节；这些均不能延期。
        await using var idleMqtt = new FakeMqttTransport();
        await using var idleRunner = new OtaTaskRunner(idleMqtt, new IpmProtocolProfile(),
            new InMemoryTaskSequenceStore(), pollingOptions: polling);
        var idleClock = Stopwatch.StartNew();
        var attemptedInvalidProgress = false;
        idleMqtt.OnPublished = message =>
        {
            using var json = JsonDocument.Parse(message.GetPayloadAsUtf8());
            if (json.RootElement.GetProperty("cmd").GetInt32() == 8)
            {
                var q = json.RootElement.GetProperty("ota_status").GetProperty("query_seq").GetInt32();
                if (idleClock.ElapsedMilliseconds > 300)
                {
                    attemptedInvalidProgress = true;
                    Inject(idleMqtt, q, 78, "RUNNING", "BOOT_VERIFY", metadata.Length, metadata.Length);
                    Inject(idleMqtt, q, 77, "RUNNING", "TRANSFER", metadata.Length + 1, metadata.Length);
                }
                else
                {
                    Inject(idleMqtt, q, 77, "RUNNING", "TRANSFER", 512, metadata.Length);
                }
            }
            return Task.CompletedTask;
        };
        var idleResult = await idleRunner.StartAndWaitAsync(CreateTask(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Check(idleResult.State == OtaTaskState.TimedOut && attemptedInvalidProgress && !idleRunner.HasActiveTask,
            "心跳、错误 SID 或非法字节数延长了停滞任务的等待窗口。");
        Console.WriteLine("IPM MCU 慢速传输跨超时窗口闭环、停滞超时与会话隔离测试通过。");
    }

    private static void Inject(FakeMqttTransport mqtt, int query, int session, string status,
        string stage, long bytes, long length)
        => mqtt.Inject("ucchip/up/sgw/123456/9", JsonSerializer.Serialize(new
        {
            cmd = 9,
            ota_status = new
            {
                query_seq = query, task_seq = 1, session_id = session, result = "OK", status, stage,
                file_size = length, transferred_bytes = bytes, task_elapsed_ms = Environment.TickCount64,
            },
        }));

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("MCU 进度超时：" + message);
    }
}
