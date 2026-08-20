using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OtaTool.Update;

var tests = new (string Name, Func<Task> Run)[]
{
    ("版本号严格解析与比较", TestReleaseVersionAsync),
    ("正式版检查与 24 小时节流", TestUpdateCheckThrottleAsync),
    ("失败检查按 2 小时节流且手动检查绕过", TestFailedCheckThrottleAsync),
    ("相同版本与不完整 Release 被正确区分", TestReleaseValidationAsync),
    ("更新包下载、摘要校验与安全解压", TestDownloadAndPrepareAsync),
    ("摘要不匹配时清理全部临时文件", TestDigestMismatchCleanupAsync),
    ("ZIP 路径穿越与缺失文件被拒绝", TestZipValidationAsync),
    ("启动确认文件限制在任务目录", TestStartupConfirmationAsync),
    ("独立更新器成功切换目录", TestUpdaterSuccessAsync),
    ("新版未确认时回滚旧版本", TestUpdaterRollbackAsync),
    ("原程序退出超时时不切换目录", TestUpdaterExitTimeoutAsync),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[通过] {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception}");
        Console.WriteLine($"[失败] {test.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"在线升级测试全部通过，共 {tests.Length} 项。");
return 0;

static Task TestReleaseVersionAsync()
{
    Assert(ReleaseVersion.TryParse("0.1.0", out var current), "0.1.0 应可解析");
    Assert(ReleaseVersion.TryParseTag("v1.2.3", out var latest), "正式标签应可解析");
    Assert(latest.CompareTo(current) > 0, "新版本应大于当前版本");
    foreach (var invalid in new[] { "V1.2.3", "v01.2.3", "v1.2", "v1.2.3-beta", "1.2.3" })
    {
        Assert(!ReleaseVersion.TryParseTag(invalid, out _), $"非法标签不应通过：{invalid}");
    }

    return Task.CompletedTask;
}

static async Task TestUpdateCheckThrottleAsync()
{
    using var fixture = new UpdateFixture();
    using var service = fixture.CreateService();
    var first = await service.CheckForUpdatesAsync(force: false);
    Assert(first.Status == UpdateCheckStatus.UpdateAvailable, "首次检查应发现更新");
    Assert(first.Release?.Version.ToString() == "0.2.0", "应返回 v0.2.0");
    Assert(fixture.Handler.ReleaseRequestCount == 1, "首次应请求 GitHub");

    var skipped = await service.CheckForUpdatesAsync(force: false);
    Assert(skipped.Status == UpdateCheckStatus.Skipped, "24 小时内自动检查应跳过");
    Assert(fixture.Handler.ReleaseRequestCount == 1, "跳过时不应请求网络");

    var forced = await service.CheckForUpdatesAsync(force: true);
    Assert(forced.Status == UpdateCheckStatus.UpdateAvailable, "手动强制检查不受节流限制");
    Assert(fixture.Handler.ReleaseRequestCount == 2, "强制检查应再次请求网络");

    Assert(service.ShouldPrompt(first.Release!), "新版本首次应提示");
    service.MarkPrompted(first.Release!);
    Assert(!service.ShouldPrompt(first.Release!), "同一版本只应自动提示一次");
}

static async Task TestFailedCheckThrottleAsync()
{
    using var fixture = new UpdateFixture();
    fixture.Handler.ReleaseStatusCode = HttpStatusCode.ServiceUnavailable;
    using var service = fixture.CreateService();
    Assert((await service.CheckForUpdatesAsync(force: false)).Status == UpdateCheckStatus.Failed, "首次失败应返回可诊断状态");
    Assert((await service.CheckForUpdatesAsync(force: false)).Status == UpdateCheckStatus.Skipped, "2 小时内自动失败重试应跳过");
    Assert(fixture.Handler.ReleaseRequestCount == 1, "失败节流时不应再次请求网络");
    Assert((await service.CheckForUpdatesAsync(force: true)).Status == UpdateCheckStatus.Failed, "手动检查应绕过失败节流");
    Assert(fixture.Handler.ReleaseRequestCount == 2, "手动检查应再次请求网络");
}

static async Task TestReleaseValidationAsync()
{
    using (var currentFixture = new UpdateFixture())
    {
        currentFixture.Handler.TagName = "v0.1.0";
        using var service = currentFixture.CreateService();
        Assert((await service.CheckForUpdatesAsync(force: true)).Status == UpdateCheckStatus.NoUpdate, "相同版本应返回无需更新");
    }

    using (var incompleteFixture = new UpdateFixture())
    {
        incompleteFixture.Handler.IncludeChecksumAsset = false;
        using var service = incompleteFixture.CreateService();
        var result = await service.CheckForUpdatesAsync(force: true);
        Assert(result.Status == UpdateCheckStatus.Failed, "缺少校验资产的 Release 必须被拒绝");
        Assert(result.ErrorMessage?.Contains("资源", StringComparison.Ordinal) == true, "错误应说明 Release 资源异常");
    }
}

static async Task TestDownloadAndPrepareAsync()
{
    using var fixture = new UpdateFixture();
    using var service = fixture.CreateService();
    var check = await service.CheckForUpdatesAsync(force: true);
    var prepared = await service.DownloadAndPrepareAsync(check.Release!, progress: null);
    Assert(File.Exists(Path.Combine(prepared.StagingDirectory, UpdatePaths.ApplicationFileName)), "暂存目录应包含主程序");
    Assert(File.Exists(Path.Combine(prepared.StagingDirectory, UpdatePaths.UpdaterFileName)), "暂存目录应包含更新器");
    Assert(File.Exists(prepared.UpdaterExecutablePath), "更新器运行副本应位于数据目录");
    Assert(File.Exists(prepared.JobFilePath), "应生成更新任务文件");
    Assert(service.GetState().PendingUpdate?.TargetVersion == "0.2.0", "应记录待安装版本");
}

static async Task TestDigestMismatchCleanupAsync()
{
    using var fixture = new UpdateFixture();
    fixture.Handler.CorruptPackageDownload = true;
    using var service = fixture.CreateService();
    var check = await service.CheckForUpdatesAsync(force: true);
    try
    {
        await service.DownloadAndPrepareAsync(check.Release!, progress: null);
        throw new InvalidOperationException("损坏更新包不应进入安装阶段");
    }
    catch (InvalidDataException)
    {
    }

    var downloadRoot = Path.Combine(fixture.UpdateRoot, "downloads");
    Assert(!Directory.Exists(downloadRoot) || !Directory.EnumerateFiles(downloadRoot, "*", SearchOption.AllDirectories).Any(), "失败后不应残留下载文件");
    Assert(!Directory.Exists(Path.Combine(fixture.UpdateRoot, "jobs")), "失败后不应生成更新任务");
    Assert(service.GetState().PendingUpdate is null, "失败后不应记录待安装状态");
}

static Task TestZipValidationAsync()
{
    using var fixture = new TemporaryDirectory();
    var traversalZip = Path.Combine(fixture.Path, "traversal.zip");
    CreateZip(traversalZip, new Dictionary<string, byte[]> { ["../escape.txt"] = [1] });
    AssertThrows<InvalidDataException>(() =>
        UpdatePackageUtilities.ExtractVerifiedZip(traversalZip, Path.Combine(fixture.Path, "stage-a")));

    var incompleteZip = Path.Combine(fixture.Path, "incomplete.zip");
    CreateZip(incompleteZip, new Dictionary<string, byte[]>
    {
        [UpdatePaths.ApplicationFileName] = [1],
        [UpdatePaths.UpdaterFileName] = [1],
    });
    AssertThrows<InvalidDataException>(() =>
        UpdatePackageUtilities.ExtractVerifiedZip(incompleteZip, Path.Combine(fixture.Path, "stage-b")));
    return Task.CompletedTask;
}

static Task TestStartupConfirmationAsync()
{
    using var fixture = new TemporaryDirectory();
    var updateRoot = Path.Combine(fixture.Path, "updates");
    var confirmation = Path.Combine(updateRoot, "jobs", "1", "confirmed.txt");
    Assert(UpdateStartupConfirmation.TryConfirmFromCommandLine(
        [UpdateStartupConfirmation.ArgumentName, confirmation],
        updateRoot,
        out var error), error ?? "确认应成功");
    Assert(File.Exists(confirmation), "应写入启动确认文件");

    var outside = Path.Combine(fixture.Path, "outside.txt");
    Assert(!UpdateStartupConfirmation.TryConfirmFromCommandLine(
        [UpdateStartupConfirmation.ArgumentName, outside],
        updateRoot,
        out _), "目录外确认文件必须被拒绝");
    return Task.CompletedTask;
}

static async Task TestUpdaterSuccessAsync()
{
    using var fixture = new UpdaterFixture(confirmNewApplication: true);
    var result = await fixture.Engine.RunAsync(fixture.JobFile);
    Assert(result == 0, "更新器应返回成功");
    Assert(File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)) == "new", "安装目录应切换到新版本");
    Assert(!Directory.Exists(fixture.BackupDirectory), "成功后应删除备份目录");
    Assert(fixture.Runtime.StartCount == 1, "成功时只启动一次新版");
}

static async Task TestUpdaterRollbackAsync()
{
    using var fixture = new UpdaterFixture(confirmNewApplication: false);
    var result = await fixture.Engine.RunAsync(fixture.JobFile);
    Assert(result == 3, "启动未确认应返回回滚状态");
    Assert(File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)) == "old", "回滚后应恢复旧版本");
    Assert(fixture.Runtime.KillCount == 1, "回滚前应结束未确认的新版");
    Assert(fixture.Runtime.StartCount == 2, "回滚后应重新启动旧版本");
}

static async Task TestUpdaterExitTimeoutAsync()
{
    using var fixture = new UpdaterFixture(confirmNewApplication: true, originalApplicationExits: false);
    var result = await fixture.Engine.RunAsync(fixture.JobFile);
    Assert(result == 2, "原程序退出超时应返回取消状态");
    Assert(File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)) == "old", "超时时不得切换安装目录");
    Assert(fixture.Runtime.StartCount == 0, "超时时不得启动新旧程序副本");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}");
}

static void CreateZip(string path, IReadOnlyDictionary<string, byte[]> files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in files)
    {
        var entry = archive.CreateEntry(file.Key);
        using var destination = entry.Open();
        destination.Write(file.Value);
    }
}

sealed class UpdateFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly byte[] _package = TestData.CreateValidPackage();
    private readonly byte[] _checksum;

    public UpdateFixture()
    {
        InstallDirectory = Path.Combine(_temporary.Path, "install");
        UpdateRoot = Path.Combine(_temporary.Path, "updates");
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllText(Path.Combine(InstallDirectory, UpdatePaths.ApplicationFileName), "old");
        File.WriteAllText(Path.Combine(InstallDirectory, UpdatePaths.UpdaterFileName), "updater");
        var packageName = "OtaTool-v0.2.0-win-x64-portable.zip";
        _checksum = Encoding.UTF8.GetBytes($"{TestData.Sha256(_package)}  {packageName}\n");
        Handler = new ReleaseHandler(_package, _checksum);
    }

    public string InstallDirectory { get; }

    public string UpdateRoot { get; }

    public ReleaseHandler Handler { get; }

    public UpdateService CreateService() => new(
        UpdateRoot,
        InstallDirectory,
        new ReleaseVersion(0, 1, 0),
        new HttpClient(Handler, disposeHandler: false),
        () => new DateTimeOffset(2026, 8, 19, 1, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        Handler.Dispose();
        _temporary.Dispose();
    }
}

sealed class ReleaseHandler(byte[] package, byte[] checksum) : HttpMessageHandler
{
    public int ReleaseRequestCount { get; private set; }
    public HttpStatusCode ReleaseStatusCode { get; set; } = HttpStatusCode.OK;
    public string TagName { get; set; } = "v0.2.0";
    public bool IncludeChecksumAsset { get; set; } = true;
    public bool CorruptPackageDownload { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "api.github.com")
        {
            ReleaseRequestCount++;
            if (ReleaseStatusCode != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(ReleaseStatusCode));
            }
            var packageName = "OtaTool-v0.2.0-win-x64-portable.zip";
            var checksumName = $"{packageName}.sha256.txt";
            var assets = new List<object>
            {
                new { name = packageName, browser_download_url = "https://download.test/package", size = package.Length, digest = $"sha256:{TestData.Sha256(package)}" },
            };
            if (IncludeChecksumAsset)
            {
                assets.Add(new { name = checksumName, browser_download_url = "https://download.test/checksum", size = checksum.Length, digest = $"sha256:{TestData.Sha256(checksum)}" });
            }
            var json = JsonSerializer.Serialize(new
            {
                tag_name = TagName,
                body = "修复升级链路并增加在线升级。",
                html_url = "https://github.com/wangjingping-88/ota_tool/releases/tag/v0.2.0",
                published_at = "2026-08-19T00:00:00Z",
                draft = false,
                prerelease = false,
                assets,
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        var content = request.RequestUri?.AbsolutePath == "/package"
            ? CorruptPackageDownload ? TestData.Corrupt(package) : package
            : checksum;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
    }
}

sealed class UpdaterFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    public UpdaterFixture(
        bool confirmNewApplication,
        bool originalApplicationExits = true)
    {
        InstallDirectory = Path.Combine(_temporary.Path, "install");
        StagingDirectory = Path.Combine(_temporary.Path, $"{UpdatePaths.StagePrefix}test");
        BackupDirectory = Path.Combine(_temporary.Path, $"{UpdatePaths.BackupPrefix}test");
        UpdateRoot = Path.Combine(_temporary.Path, "updates");
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(StagingDirectory);
        File.WriteAllText(Path.Combine(InstallDirectory, UpdatePaths.ApplicationFileName), "old");
        File.WriteAllText(Path.Combine(StagingDirectory, UpdatePaths.ApplicationFileName), "new");
        File.WriteAllText(Path.Combine(StagingDirectory, UpdatePaths.UpdaterFileName), "updater");
        var jobDirectory = Path.Combine(UpdateRoot, "jobs", "test");
        JobFile = Path.Combine(jobDirectory, "job.json");
        UpdateJobStore.Save(JobFile, new UpdateJob
        {
            CurrentProcessId = 7,
            InstallDirectory = InstallDirectory,
            StagingDirectory = StagingDirectory,
            BackupDirectory = BackupDirectory,
            TargetVersion = "0.2.0",
            ConfirmationFile = Path.Combine(jobDirectory, "confirmed.txt"),
            LogFilePath = Path.Combine(UpdateRoot, "logs", "updater.log"),
            UpdateStateFilePath = Path.Combine(UpdateRoot, "state.json"),
        });
        Runtime = new FakeProcessRuntime(confirmNewApplication, originalApplicationExits);
        Engine = new UpdaterEngine(
            Runtime,
            UpdateRoot,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(5));
    }

    public string InstallDirectory { get; }
    public string StagingDirectory { get; }
    public string BackupDirectory { get; }
    public string UpdateRoot { get; }
    public string JobFile { get; }
    public FakeProcessRuntime Runtime { get; }
    public UpdaterEngine Engine { get; }

    public void Dispose() => _temporary.Dispose();
}

sealed class FakeProcessRuntime(
    bool confirmNewApplication,
    bool originalApplicationExits) : IUpdaterProcessRuntime
{
    private readonly HashSet<int> _exited = [];
    private int _nextProcessId = 100;

    public int StartCount { get; private set; }
    public int KillCount { get; private set; }

    public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(originalApplicationExits);

    public int Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        StartCount++;
        var processId = _nextProcessId++;
        if (confirmNewApplication && arguments.Count == 2 && arguments[0] == UpdateStartupConfirmation.ArgumentName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arguments[1])!);
            File.WriteAllText(arguments[1], "confirmed");
        }
        return processId;
    }

    public bool HasExited(int processId) => _exited.Contains(processId);

    public void Kill(int processId)
    {
        KillCount++;
        _exited.Add(processId);
    }
}

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"OtaTool.Update.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

static class TestData
{
    public static byte[] CreateValidPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = new Dictionary<string, byte[]>
            {
                [UpdatePaths.ApplicationFileName] = Encoding.UTF8.GetBytes("new"),
                [UpdatePaths.UpdaterFileName] = Encoding.UTF8.GetBytes("updater"),
                ["bsdiff_cmd.exe"] = [1],
                ["Tools/OTA_TOOL/OTA_TOOL.exe"] = [2],
                ["Tools/OTA_TOOL/Qt5Core.dll"] = [3],
                ["Tools/OTA_TOOL/platforms/qwindows.dll"] = [4],
                ["Scripts/TestPatchWithOtaTool.ps1"] = [5],
                ["analyze_ota_logs.py"] = [6],
            };
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key);
                using var destination = entry.Open();
                destination.Write(file.Value);
            }
        }

        return stream.ToArray();
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Corrupt(byte[] bytes)
    {
        var copy = bytes.ToArray();
        copy[^1] ^= 0xFF;
        return copy;
    }
}
