// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed partial class EnsureEulaStep(ISettingsService settings, ILocalizer localizer) : ILaunchStep
{
    public string Id => "eula";

    public int Order => 370;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var eulaPath = Path.Combine(context.GameDirectory, "eula.txt");

        if (File.Exists(eulaPath) && IsAccepted(File.ReadAllText(eulaPath)))
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.eula.ok")));
        }

        var acceptedByUser = settings.Get<bool>("game.acceptEula");
        if (!acceptedByUser)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                localizer.Get(context.DryRun ? "launch.eula.notAccepted.preview" : "launch.eula.notAccepted")));
        }

        if (context.DryRun)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.eula.willWrite")));
        }

        try
        {
            Directory.CreateDirectory(context.GameDirectory);
            File.WriteAllText(eulaPath, "eula=true\n");
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.eula.written")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.eula.failed", exception.Message)));
        }
    }

    private static bool IsAccepted(string content) => AcceptedRegex().IsMatch(content);

    [GeneratedRegex(@"(?im)^\s*eula\s*=\s*true\s*$")]
    private static partial Regex AcceptedRegex();
}
