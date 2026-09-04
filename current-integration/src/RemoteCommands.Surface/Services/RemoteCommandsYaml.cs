namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandDefinition(
    string Id,
    string Label,
    string Command,
    string Description,
    string Type,
    string Host = "",
    string Input1Label = "Input",
    string Input1Placeholder = "Paste or type the command input.",
    string Input2Label = "Additional input",
    string Input2Placeholder = "Optional second input.",
    bool ShowSecondInput = false)
{
    public bool UsesRemoteHost =>
        !string.Equals(Type, "py", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reader and validator for the flat commands.yaml format used by Remote Commands.
/// Optional input metadata lets a command explain what the user should provide without
/// requiring a Surface code change.
/// </summary>
public static class RemoteCommandsYaml
{
    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "shell", "py" };

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
            .Select(CreateDefinition)
            .Where(command =>
                !string.IsNullOrWhiteSpace(command.Id) &&
                !string.IsNullOrWhiteSpace(command.Command))
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

            if (!char.IsWhiteSpace(raw[0]) && trimmed.EndsWith(':') && trimmed[..^1] == "commands")
            {
                hasCommandsSection = true;
                break;
            }
        }

        if (!hasCommandsSection)
        {
            error = "YAML must have a top-level 'commands' key.";
            return false;
        }

        try
        {
            var commands = ParseCommands(text);
            if (commands.Count == 0)
            {
                error = "The commands list must contain at least one entry with id and command.";
                return false;
            }

            var duplicateId = commands
                .GroupBy(command => command.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (!string.IsNullOrWhiteSpace(duplicateId))
            {
                error = $"Command id '{duplicateId}' is duplicated.";
                return false;
            }

            foreach (var command in commands)
            {
                if (!SupportedTypes.Contains(command.Type))
                {
                    error = $"Command '{command.Id}' has unsupported type '{command.Type}'. Use shell or py.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(command.Host) &&
                    !RemoteCommandsStore.IsValidHost(command.Host))
                {
                    error = $"Command '{command.Id}' has an invalid SSH host '{command.Host}'.";
                    return false;
                }
            }

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
            input1_label: "Kernel stack"
            input1_placeholder: "Paste the kernel stack to decode."

          - id: decode_stack_with_symbols
            label: "Decode Kernel Stack with Symbols"
            command: "/home/lixr/.local/bin/decode_kernel_stack -s"
            description: "Decodes the kernel stack with symbol resolution."
            type: "shell"
            input1_label: "Kernel stack"
            input1_placeholder: "Paste the kernel stack to decode."

          - id: gc_trace_analyzer
            label: "GC Trace Analyzer"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/stat_tools/gc_trace_analyzer.py"
            description: "Analyzes garbage collection traces to optimize performance."
            type: "shell"
            input1_label: "GC trace"
            input1_placeholder: "Paste the GC trace to analyze."

          - id: collect_rets
            label: "Collect Multi-app Rets"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/collect_MULTIAPP_rets.py"
            description: "Collects and reports on multi-application interactions."
            type: "shell"
            input1_label: "Collection input"
            input1_placeholder: "Paste the primary collection input."

          - id: collect_pc_mark_rets
            label: "Collect PCMark Rets"
            command: "/home/lixr/miniconda3/envs/android_automatic/bin/python3 /home/lixr/repo/androidtools/collect_pc_mark_rets.py"
            description: "Collects and reports on PCMark results."
            type: "shell"
            input1_label: "PCMark input"
            input1_placeholder: "Paste the PCMark collection input."

          - id: replace_host
            label: "Replace Host Directory"
            command: "replace_host_directory"
            description: "Replaces local directory paths with remote URLs."
            type: "py"
            input1_label: "Paths or text"
            input1_placeholder: "Paste text containing host working-directory paths."

          - id: remove_comments
            label: "Remove C++ Comments"
            command: "remove_cpp_comments"
            description: "Strips C++ comments from the source code."
            type: "py"
            input1_label: "C++ source"
            input1_placeholder: "Paste C or C++ source code."

          - id: remove_latex_comment_lines
            label: "Remove LaTeX Comment Lines"
            command: "remove_latex_comment_lines"
            description: "Removes LaTeX lines whose first non-whitespace character is %."
            type: "py"
            input1_label: "LaTeX source"
            input1_placeholder: "Paste LaTeX source text."

          - id: format_latex_comma_period_lines
            label: "Format LaTeX Comma/Period Lines"
            command: "format_latex_comma_period_lines"
            description: "Reflows LaTeX so plain text only breaks after commas and periods, while preserving LaTeX command syntax."
            type: "py"
            input1_label: "LaTeX source"
            input1_placeholder: "Paste LaTeX source text to reflow."

          - id: add_extract_result_prefix
            label: "Add Extract Result Prefix"
            command: "add_extract_result_prefix"
            description: "Prefixes each input line with extract_result."
            type: "py"
            input1_label: "Lines"
            input1_placeholder: "Paste one item per line."

          - id: gen_rsync_from_folders
            label: "Generate Rsync Commands from Folders"
            command: "gen_rsync_from_folders"
            description: "Given remote folder paths, generates rsync commands to sync them locally, plus postconditions_db."
            type: "py"
            input1_label: "Remote folder paths"
            input1_placeholder: "Paste one remote folder path per line."

        types:
          - shell
          - py
        """;

    private static RemoteCommandDefinition CreateDefinition(Dictionary<string, string> item)
    {
        var type = item.GetValueOrDefault("type", "shell");
        var isTransform = string.Equals(type, "py", StringComparison.OrdinalIgnoreCase);

        return new RemoteCommandDefinition(
            item.GetValueOrDefault("id", ""),
            item.GetValueOrDefault("label", item.GetValueOrDefault("id", "")),
            item.GetValueOrDefault("command", ""),
            item.GetValueOrDefault("description", ""),
            type,
            item.GetValueOrDefault("host", ""),
            item.GetValueOrDefault("input1_label", isTransform ? "Text input" : "Primary input"),
            item.GetValueOrDefault("input1_placeholder", "Paste or type the command input."),
            item.GetValueOrDefault("input2_label", "Additional input"),
            item.GetValueOrDefault("input2_placeholder", "Optional second input."),
            ParseBoolean(item.GetValueOrDefault("show_second_input", "false")));
    }

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static void ParseKeyValue(string text, Dictionary<string, string> target)
    {
        var index = text.IndexOf(':', StringComparison.Ordinal);
        if (index <= 0)
        {
            return;
        }

        var key = text[..index].Trim();
        var value = text[(index + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        target[key] = value;
    }
}
