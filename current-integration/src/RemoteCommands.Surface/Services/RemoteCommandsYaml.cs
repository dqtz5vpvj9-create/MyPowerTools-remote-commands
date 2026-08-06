using System.Text;

namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandDefinition(
    string Id,
    string Label,
    string Command,
    string Description,
    string Type,
    string Host = "");

/// <summary>
/// Narrow YAML reader for the powertool commands.yaml contract plus the bundled default catalog.
/// </summary>
public static class RemoteCommandsYaml
{
    public static IReadOnlyList<RemoteCommandDefinition> ParseCommands(string text)
    {
        var commands = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var inCommands = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (!char.IsWhiteSpace(raw[0]) && trimmed.EndsWith(':'))
            {
                var section = trimmed[..^1];
                inCommands = section == "commands";
                current = null;
                continue;
            }

            if (!inCommands)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                commands.Add(current);
                ParseKeyValue(trimmed[2..], current);
                continue;
            }

            if (current is not null)
            {
                ParseKeyValue(trimmed, current);
            }
        }

        return commands
            .Select(item => new RemoteCommandDefinition(
                item.GetValueOrDefault("id", ""),
                item.GetValueOrDefault("label", item.GetValueOrDefault("id", "")),
                item.GetValueOrDefault("command", ""),
                item.GetValueOrDefault("description", ""),
                item.GetValueOrDefault("type", "shell"),
                item.GetValueOrDefault("host", "")))
            .Where(command => !string.IsNullOrWhiteSpace(command.Id) && !string.IsNullOrWhiteSpace(command.Command))
            .ToArray();
    }

    public static bool TryValidate(string text, out string? error)
    {
        var hasCommandsSection = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(raw[0]) && trimmed.EndsWith(':'))
            {
                if (trimmed[..^1] == "commands")
                {
                    hasCommandsSection = true;
                }
            }
        }

        if (!hasCommandsSection)
        {
            error = "YAML must have a top-level 'commands' key.";
            return false;
        }

        try
        {
            _ = ParseCommands(text);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string DefaultCommandsYaml { get; } = """
        commands:
          - id: decode_stack
            label: "Decode Kernel Stack"
            command: "/home/lixr/.local/bin/decode_kernel_stack"
            description: "Decodes the kernel stack for debugging purposes."
            type: "shell"

          - id: decode_stack_with_symbols
            label: "Decode Kernel Stack with Symbols"
            command: "/home/lixr/.local/bin/decode_kernel_stack -s"
            description: "Decodes the kernel stack with symbol resolution."
            type: "shell"

          - id: gc_trace_analyzer
            label: "GC Trace Analyzer"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/stat_tools/gc_trace_analyzer.py"
            description: "Analyzes garbage collection traces to optimize performance."
            type: "shell"

          - id: collect_rets
            label: "Collect Multi-app Rets"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/collect_MULTIAPP_rets.py"
            description: "Collects and reports on multi-application interactions."
            type: "shell"

          - id: collect_pc_mark_rets
            label: "Collect PCMark Rets"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/collect_pc_mark_rets.py"
            description: "Collects and reports on multi-application interactions."
            type: "shell"

          - id: replace_host
            label: "Replace Host Directory"
            command: "replace_host_directory"
            description: "Replaces local directory paths with remote URLs."
            type: "py"

          - id: remove_comments
            label: "Remove C++ Comments"
            command: "remove_cpp_comments"
            description: "Strips C++ comments from the source code."
            type: "py"

          - id: remove_latex_comment_lines
            label: "Remove LaTeX Comment Lines"
            command: "remove_latex_comment_lines"
            description: "Removes LaTeX lines whose first non-whitespace character is %."
            type: "py"

          - id: format_latex_comma_period_lines
            label: "Format LaTeX Comma/Period Lines"
            command: "format_latex_comma_period_lines"
            description: "Reflows LaTeX so plain text only breaks after commas and periods, while preserving LaTeX command syntax."
            type: "py"

          - id: add_extract_result_prefix
            label: "Add Extract Result Prefix"
            command: "add_extract_result_prefix"
            description: "Prefixes each input line with extract_result."
            type: "py"

          - id: gen_rsync_from_folders
            label: "Generate Rsync Commands from Folders"
            command: "gen_rsync_from_folders"
            description: "Given remote folder paths, generates rsync commands to sync them locally, plus postconditions_db."
            type: "py"

        types:
          - shell
          - py
        """;

    private static void ParseKeyValue(string text, Dictionary<string, string> target)
    {
        var index = text.IndexOf(':', StringComparison.Ordinal);
        if (index <= 0)
        {
            return;
        }

        var key = text[..index].Trim();
        var value = text[(index + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        target[key] = value;
    }
}
