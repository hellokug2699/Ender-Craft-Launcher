// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Additions;

namespace Ecl.TestAddition;

/// <summary>
/// 测试用最小扩展：只通过受控上下文读取自己的数据目录，并写入可观察的初始化/停止标记。
/// 它不引用启动器 UI、实例服务或任何内部对象，用于验证按需加载与卸载边界。
/// </summary>
public sealed class TestAdditionModule : IAdditionModule
{
    public ValueTask InitializeAsync(AdditionHostContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.DataDirectory);
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "initialized.txt"),
            $"{context.Id.Value}|{context.AdditionVersion}|{context.CoreApiVersion}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
