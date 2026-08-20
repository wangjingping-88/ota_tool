using System.Text.Json;

namespace OtaTool.Update;

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public UpdateStateStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public UpdateState Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_path), JsonOptions) ?? new UpdateState()
                : new UpdateState();
        }
        catch
        {
            return new UpdateState();
        }
    }

    public void Save(UpdateState state)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("更新状态文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void ClearPendingUpdate()
    {
        var state = Load();
        state.PendingUpdate = null;
        Save(state);
    }
}
