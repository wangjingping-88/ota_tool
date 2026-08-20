namespace OtaTool.Update;

public static class UpdatePaths
{
    public const string ApplicationFileName = "OtaTool.App.exe";
    public const string UpdaterFileName = "OtaTool.Updater.exe";
    public const string StagePrefix = ".ota-tool-stage-";
    public const string BackupPrefix = ".ota-tool-backup-";

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OtaTool");

    public static string DefaultUpdateRoot => Path.Combine(DataRoot, "updates");

    public static bool IsPathWithin(string path, string root)
    {
        var fullPath = Normalize(path);
        var fullRoot = Normalize(root);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "." ||
               (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
    }

    public static bool IsDangerousInstallDirectory(string path)
    {
        var target = Normalize(path);
        var root = Normalize(Path.GetPathRoot(target) ?? string.Empty);
        var userProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return string.Equals(target, root, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, userProfile, StringComparison.OrdinalIgnoreCase) ||
               IsPathWithin(target, DataRoot);
    }

    public static string EnsureSafeFileName(string value)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return value;
    }

    public static string Normalize(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
