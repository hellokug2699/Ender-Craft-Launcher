// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;

namespace Ecl.Core.Storage;

/// <summary>
/// 工作区配置的原子读写。写入时先写同目录临时文件再替换，避免中断留下半份配置。
/// 读取失败不删除原文件，由调用方报告并决定是否让用户重新选择。
/// </summary>
public sealed class WorkspaceStore
{
    private readonly string _filePath;

    public WorkspaceStore(string configDirectory)
    {
        _filePath = Path.Combine(configDirectory, "workspace.json");
    }

    public string FilePath => _filePath;

    public WorkspaceFile Load(out string? error)
    {
        error = null;
        if (!File.Exists(_filePath))
        {
            return new WorkspaceFile();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<WorkspaceFile>(json, EclJson.Default);
            if (file is null)
            {
                error = "工作区配置为空。";
                return new WorkspaceFile();
            }

            if (file.SchemaVersion > WorkspaceFile.CurrentSchemaVersion)
            {
                error = $"工作区配置版本 {file.SchemaVersion} 高于当前支持的 {WorkspaceFile.CurrentSchemaVersion}。";
                return new WorkspaceFile();
            }

            return file;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"读取工作区配置失败：{exception.Message}";
            return new WorkspaceFile();
        }
    }

    public void Save(WorkspaceFile file)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(file, EclJson.Indented);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _filePath, overwrite: true);
    }
}
