// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Cli;

public sealed class ParsedCommandLine
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "help",
        "h",
        "json",
        "force",
        "default",
        "dry-run",
        "accept-eula",
        "verbose",
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string?> Options => _options;

    private ParsedCommandLine(IReadOnlyList<string> arguments, Dictionary<string, string?> options)
    {
        Arguments = arguments;
        _options = options;
    }

    public static ParsedCommandLine Parse(string[] args)
    {
        var arguments = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var endOfOptions = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (endOfOptions)
            {
                arguments.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (argument is "-h" or "-?")
            {
                options["h"] = null;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                arguments.Add(argument);
                continue;
            }

            var optionText = argument[2..];
            string? value = null;

            var equalsIndex = optionText.IndexOf('=');
            if (equalsIndex >= 0)
            {
                value = optionText[(equalsIndex + 1)..];
                optionText = optionText[..equalsIndex];
            }
            else if (!KnownFlags.Contains(optionText) &&
                     index + 1 < args.Length &&
                     !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            options[optionText] = value;
        }

        return new ParsedCommandLine(arguments, options);
    }

    public bool HasFlag(string name) => _options.ContainsKey(name) && _options[name] is null;

    public bool HasOption(string name) => _options.ContainsKey(name);

    public string? GetValue(string name) => _options.GetValueOrDefault(name);

    public string? DataDirectory => GetValue("data-dir");

    public string? Language => GetValue("lang");
}
