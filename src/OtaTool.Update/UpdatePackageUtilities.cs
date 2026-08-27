using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OtaTool.Update;

public static partial class UpdatePackageUtilities
{
    private static readonly string[] RequiredPackageFiles =
    [
        UpdatePaths.ApplicationFileName,
        UpdatePaths.UpdaterFileName,
        "bsdiff_cmd.exe",
        "partition_patch_verify.exe",
        Path.Combine("Licenses", "partition_patch_verify.md"),
        "analyze_ota_logs.py",
    ];

    public static string ParseSha256(string checksumText, string expectedFileName)
    {
        foreach (var line in checksumText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Sha256LineRegex().Match(line);
            if (match.Success &&
                string.Equals(match.Groups[2].Value, expectedFileName, StringComparison.Ordinal))
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }
        }

        throw new InvalidDataException("SHA-256 校验文件格式或资产名称无效。");
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public static void ExtractVerifiedZip(
        string packagePath,
        string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
        {
            throw new IOException($"更新暂存目录已经存在：{stagingDirectory}");
        }

        Directory.CreateDirectory(stagingDirectory);
        var stagingRoot = UpdatePaths.Normalize(stagingDirectory) + Path.DirectorySeparatorChar;
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                ValidateEntry(entry);
                var destinationPath = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                if (!destinationPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"压缩包包含不安全路径：{entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: false);
            }

            var missing = RequiredPackageFiles
                .Where(relative => !File.Exists(Path.Combine(stagingDirectory, relative)))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"便携包缺少必要文件：{string.Join("、", missing)}");
            }
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            normalized.StartsWith('\\') ||
            DrivePathRegex().IsMatch(normalized) ||
            Path.IsPathRooted(entry.FullName) ||
            segments.Any(segment => segment == "..") ||
            unixType == 0xA000)
        {
            throw new InvalidDataException($"压缩包包含不安全路径或链接：{entry.FullName}");
        }
    }

    [GeneratedRegex("^([0-9a-fA-F]{64})[ \\t]+[*]?([^\\s]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256LineRegex();

    [GeneratedRegex("^[a-zA-Z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();
}
