// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Ecl.Core;

namespace Ecl.App.ViewModels;

public sealed class AboutViewModel
{
    public AboutViewModel(IAppPaths paths)
    {
        Version = typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Runtime = RuntimeInformation.FrameworkDescription;
        OperatingSystem = RuntimeInformation.OSDescription;
        DataDirectory = paths.Root;
        RepositoryUrl = "https://github.com/hellokug2699/Ender-Craft-Launcher";
        License = "GPL-3.0-or-later";
    }

    public string Version { get; }

    public string Runtime { get; }

    public string OperatingSystem { get; }

    public string DataDirectory { get; }

    public string RepositoryUrl { get; }

    public string License { get; }
}
