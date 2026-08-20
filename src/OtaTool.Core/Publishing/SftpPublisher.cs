using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using OtaTool.Core.Http;

namespace OtaTool.Core.Publishing;

public sealed record SftpPublishOptions(
    string Host,
    int Port,
    string UserName,
    string RemoteDirectory,
    string PublicBaseUrl,
    string? Password = null,
    string? PrivateKeyPath = null,
    string? PrivateKeyPassphrase = null,
    string? ExpectedHostKeySha256 = null);

public sealed record PublishedFile(string RemotePath, Uri PublicUri, long Length, string Md5, string Sha256);

public sealed record SftpConnectionTestResult(bool IsSuccess, string Message);

public interface ISftpPublisher
{
    Task<PublishedFile> PublishAsync(string localFilePath, SftpPublishOptions options, CancellationToken cancellationToken = default);

    Task VerifyHttpAsync(PublishedFile file, CancellationToken cancellationToken = default);

    Task<SftpConnectionTestResult> TestConnectionAsync(SftpPublishOptions options, CancellationToken cancellationToken = default);
}

public sealed class SshNetSftpPublisher : ISftpPublisher
{
    public async Task<PublishedFile> PublishAsync(string localFilePath, SftpPublishOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ExpectedHostKeySha256))
        {
            options = options with { ExpectedHostKeySha256 = "optional" };
        }
        var fullPath = Path.GetFullPath(localFilePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("待发布文件不存在。", fullPath);
        ValidateOptions(options);
        var result = await Task.Run(() => Upload(fullPath, options, cancellationToken), cancellationToken);
        return result;
    }

    public async Task VerifyHttpAsync(PublishedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        HttpFileVerificationResult? verification = null;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            verification = await HttpFileVerifier.VerifyAsync(file.PublicUri, file.Length, file.Md5, verifyFullMd5: true, cancellationToken);
            if (verification.IsSuccess) return;

            var isNotReady = verification.Message.Contains("HTTP HEAD 返回 404", StringComparison.Ordinal);
            if (!isNotReady || attempt == 30) break;
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
        }

        throw new InvalidDataException($"公网文件验证失败：{verification?.Message}（SFTP 已上传；已等待 HTTP 文件可见性约 3 分钟）");
    }

    public async Task<SftpConnectionTestResult> TestConnectionAsync(SftpPublishOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ExpectedHostKeySha256)) options = options with { ExpectedHostKeySha256 = "optional" };
        ValidateOptions(options);
        return await Task.Run(() => TestConnection(options), cancellationToken);
    }

    private static SftpConnectionTestResult TestConnection(SftpPublishOptions options)
    {
        var connectionInfo = BuildConnectionInfo(options);
        using var client = new SftpClient(connectionInfo);
        client.HostKeyReceived += (_, eventArgs) => eventArgs.CanTrust = IsTrustedHostKey(eventArgs, options.ExpectedHostKeySha256);
        try
        {
            client.Connect();
            return client.Exists(options.RemoteDirectory)
                ? new SftpConnectionTestResult(true, $"SFTP 连接成功，远端目录可访问：{options.RemoteDirectory}")
                : new SftpConnectionTestResult(true, $"SFTP 连接成功，远端目录不存在；发布时会自动创建：{options.RemoteDirectory}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private static PublishedFile Upload(string localFilePath, SftpPublishOptions options, CancellationToken cancellationToken)
    {
        var connectionInfo = BuildConnectionInfo(options);
        using var client = new SftpClient(connectionInfo);
        client.HostKeyReceived += (_, eventArgs) => eventArgs.CanTrust = IsTrustedHostKey(eventArgs, options.ExpectedHostKeySha256);
        client.Connect();
        EnsureRemoteDirectory(client, options.RemoteDirectory);
        var fileName = Path.GetFileName(localFilePath);
        var finalPath = CombineRemotePath(options.RemoteDirectory, fileName);
        var temporaryPath = $"{finalPath}.uploading-{Guid.NewGuid():N}";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(localFilePath);
            client.UploadFile(input, temporaryPath, uploaded =>
            {
                cancellationToken.ThrowIfCancellationRequested();
            });
            if (client.Exists(finalPath)) client.DeleteFile(finalPath);
            client.RenameFile(temporaryPath, finalPath);
        }
        catch
        {
            if (client.IsConnected && client.Exists(temporaryPath)) client.DeleteFile(temporaryPath);
            throw;
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        var info = new FileInfo(localFilePath);
        var md5 = ComputeHash(localFilePath, MD5.Create());
        var sha256 = ComputeHash(localFilePath, SHA256.Create());
        var uri = new Uri(new Uri(options.PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), Uri.EscapeDataString(fileName));
        return new PublishedFile(finalPath, uri, info.Length, md5, sha256);
    }

    private static ConnectionInfo BuildConnectionInfo(SftpPublishOptions options)
    {
        AuthenticationMethod authentication;
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            authentication = new PrivateKeyAuthenticationMethod(options.UserName, keyFile);
        }
        else
        {
            authentication = new PasswordAuthenticationMethod(options.UserName, options.Password ?? throw new ArgumentException("必须提供 SFTP 密码或私钥。"));
        }
        return new ConnectionInfo(options.Host, options.Port, options.UserName, authentication)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static bool IsTrustedHostKey(HostKeyEventArgs eventArgs, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return true;
        if (string.Equals(expectedHash, "optional", StringComparison.Ordinal)) return true;
        var actual = Convert.ToHexString(SHA256.HashData(eventArgs.HostKey)).ToLowerInvariant();
        var expected = expectedHash.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDirectory)
    {
        var current = remoteDirectory.StartsWith('/') ? "/" : string.Empty;
        foreach (var segment in remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = string.IsNullOrEmpty(current) || current == "/" ? $"{current}{segment}" : $"{current}/{segment}";
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }

    private static string CombineRemotePath(string directory, string fileName) => $"{directory.TrimEnd('/')}/{fileName}";

    private static string ComputeHash(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    private static void ValidateOptions(SftpPublishOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RemoteDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PublicBaseUrl);
        if (options.Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath) && !File.Exists(options.PrivateKeyPath)) throw new FileNotFoundException("SFTP 私钥文件不存在。", options.PrivateKeyPath);
    }
}
