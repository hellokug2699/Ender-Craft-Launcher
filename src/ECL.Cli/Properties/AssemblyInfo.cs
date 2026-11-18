// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;

// CLI 命令编排测试需要构造命令对象并注入假服务，避免启动完整宿主或访问网络。
[assembly: InternalsVisibleTo("Ecl.Tests")]
