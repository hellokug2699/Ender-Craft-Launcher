// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public sealed record LibraryPlanItem(
    VersionLibrary Library,
    VersionLibraryArtifact? PrimaryArtifact,
    VersionLibraryArtifact? NativeArtifact,
    bool IsNativeOnly);

public static class PlatformLibrarySelector
{
    public static IReadOnlyList<LibraryPlanItem> Plan(
        IEnumerable<VersionLibrary> libraries,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var allowed = libraries
            .Where(library => LibraryRules.IsAllowed(library, osName, osArch, features))
            .ToList();

        var selectedNativeLibraries = new HashSet<VersionLibrary>();
        var modernNatives = allowed.Where(LibraryRules.IsModernNativeClassifier);

        foreach (var group in modernNatives.GroupBy(
                     library => MavenCoordinates.GetBaseName(library.Name) ?? library.Name,
                     StringComparer.Ordinal))
        {
            var best = group
                .Select(library => (
                    Library: library,
                    Priority: LibraryRules.NativeClassifierPriority(
                        MavenCoordinates.GetClassifier(library.Name)!,
                        osName,
                        osArch)))
                .Where(item => item.Priority > 0)
                .OrderByDescending(item => item.Priority)
                .FirstOrDefault();

            if (best.Library is not null)
            {
                selectedNativeLibraries.Add(best.Library);
            }
        }

        var result = new List<LibraryPlanItem>(allowed.Count);
        foreach (var library in allowed)
        {
            if (LibraryRules.IsModernNativeClassifier(library))
            {
                if (!selectedNativeLibraries.Contains(library))
                {
                    continue;
                }

                var artifact = LibraryRules.GetPrimaryArtifact(library);
                result.Add(new LibraryPlanItem(library, artifact, artifact, IsNativeOnly: true));
                continue;
            }

            result.Add(new LibraryPlanItem(
                library,
                LibraryRules.GetPrimaryArtifact(library),
                LibraryRules.GetNativeArtifact(library, osName, osArch),
                IsNativeOnly: false));
        }

        return result;
    }

    public static IReadOnlyList<VersionLibraryArtifact> GetDownloadArtifacts(IEnumerable<LibraryPlanItem> plan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VersionLibraryArtifact>();

        foreach (var item in plan)
        {
            foreach (var artifact in new[] { item.PrimaryArtifact, item.NativeArtifact })
            {
                if (artifact?.Path is null || !seen.Add(artifact.Path))
                {
                    continue;
                }

                result.Add(artifact);
            }
        }

        return result;
    }

    public static IReadOnlyList<VersionLibraryArtifact> GetClasspathArtifacts(IEnumerable<LibraryPlanItem> plan) =>
        plan.Where(item => !item.IsNativeOnly)
            .Select(item => item.PrimaryArtifact)
            .Where(artifact => artifact?.Path is not null)
            .Select(artifact => artifact!)
            .ToArray();
}
