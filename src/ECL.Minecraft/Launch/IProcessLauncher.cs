// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Launch;

public interface IProcessLauncher
{
    int Start(LaunchCommand command, string workingDirectory, bool showConsole, string instanceId);
}
