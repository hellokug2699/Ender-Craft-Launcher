// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;

namespace Ecl.Java;

/// <summary>
/// 用户自定义 JVM 预设。支持两种 JSON：字符串数组，或
/// { "name": "My Preset", "arguments": ["-XX:+UseG1GC", ...] }。
/// </summary>
public sealed record CustomJvmPreset(string Name, IReadOnlyList<string> Arguments);

public static class CustomJvmPresetLoader
{
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = 512;

    public static Result<CustomJvmPreset> Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<CustomJvmPreset>.Fail("java.preset_empty", "自定义 JVM 预设路径为空。");
        }

        if (!File.Exists(path))
        {
            return Result<CustomJvmPreset>.Fail(
                "java.preset_missing",
                $"自定义 JVM 预设不存在：{path}");
        }

        try
        {
            return Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<CustomJvmPreset>.Fail(
                "java.preset_read_failed",
                $"读取自定义 JVM 预设失败：{exception.Message}");
        }
    }

    public static Result<CustomJvmPreset> Parse(string json, string fallbackName = "custom")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result<CustomJvmPreset>.Fail("java.preset_invalid", "自定义 JVM 预设为空。");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = document.RootElement;
            var name = fallbackName;
            var arguments = new List<string>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadArguments(root, arguments);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    name = nameElement.GetString()!;
                }

                if (!root.TryGetProperty("arguments", out var argumentsElement) &&
                    !root.TryGetProperty("args", out argumentsElement))
                {
                    return Result<CustomJvmPreset>.Fail(
                        "java.preset_invalid",
                        "自定义 JVM 预设缺少 arguments 数组。");
                }

                if (argumentsElement.ValueKind != JsonValueKind.Array)
                {
                    return Result<CustomJvmPreset>.Fail(
                        "java.preset_invalid",
                        "自定义 JVM 预设的 arguments 必须是数组。");
                }

                ReadArguments(argumentsElement, arguments);
            }
            else
            {
                return Result<CustomJvmPreset>.Fail(
                    "java.preset_invalid",
                    "自定义 JVM 预设必须是数组或对象。");
            }

            if (arguments.Count == 0)
            {
                return Result<CustomJvmPreset>.Fail("java.preset_empty", "自定义 JVM 预设没有参数。");
            }

            if (arguments.Count > MaxArguments)
            {
                return Result<CustomJvmPreset>.Fail(
                    "java.preset_too_many",
                    $"自定义 JVM 预设最多 {MaxArguments} 个参数。");
            }

            foreach (var argument in arguments)
            {
                var error = ValidateArgument(argument);
                if (error is not null)
                {
                    return Result<CustomJvmPreset>.Fail("java.preset_invalid_argument", error);
                }
            }

            return Result<CustomJvmPreset>.Ok(new CustomJvmPreset(name, arguments));
        }
        catch (JsonException exception)
        {
            return Result<CustomJvmPreset>.Fail(
                "java.preset_invalid",
                $"自定义 JVM 预设 JSON 无效：{exception.Message}");
        }
    }

    private static void ReadArguments(JsonElement array, ICollection<string> arguments)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                arguments.Add(item.GetString() ?? string.Empty);
            }
        }
    }

    private static string? ValidateArgument(string raw)
    {
        var argument = raw.Trim();
        if (argument.Length == 0)
        {
            return "自定义 JVM 预设包含空参数。";
        }

        if (argument.Length > MaxArgumentLength)
        {
            return $"JVM 参数过长：{argument[..32]}…";
        }

        if (argument.Contains('\n') || argument.Contains('\r'))
        {
            return "JVM 参数不能包含换行。";
        }

        if (argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
        {
            return "自定义预设不能设置 -Xmx/-Xms，内存由启动器统一管理。";
        }

        if (argument.Equals("-cp", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("-classpath", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--class-path", StringComparison.OrdinalIgnoreCase))
        {
            return "自定义预设不能覆盖 -cp/-classpath。";
        }

        return null;
    }
}
