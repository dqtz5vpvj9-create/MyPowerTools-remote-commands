using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RemoteCommands.Surface.Services;

public static class RemoteCommandRunners
{
    public const string Ssh = "ssh";
    public const string Local = "local";
    public const string Transform = "transform";
}

public sealed record RemoteCommandInputDefinition(
    string Id,
    string Label,
    string Placeholder,
    string Kind,
    bool Required,
    string DefaultValue = "",
    string Description = "");

public sealed record RemoteCommandDefinition(
    string Id,
    string Label,
    string Command,
    string Description,
    string Runner,
    string Group,
    string Host,
    int TimeoutSeconds,
    IReadOnlyList<RemoteCommandInputDefinition> Inputs,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory = "",
    bool LegacyFileArguments = false,
    string CatalogDefaultHost = "")
{
    public bool UsesHost => string.Equals(Runner, RemoteCommandRunners.Ssh, StringComparison.Ordinal);

    // Compatibility alias for persisted history and callers from the first port.
    public string Type => Runner switch
    {
        RemoteCommandRunners.Ssh => "shell",
        RemoteCommandRunners.Transform => "py",
        _ => Runner
    };

    public string GroupLabel => string.IsNullOrWhiteSpace(Group) ? "Other" : Group;

    public string TagsText => Tags.Count == 0 ? "" : string.Join(" · ", Tags);
}

public sealed record RemoteCommandCatalog(
    int Schema,
    IReadOnlyList<RemoteCommandDefinition> Commands);

/// <summary>
/// Versioned commands.yaml reader. Schema 2 supports dynamic inputs and declarative runners;
/// the original flat shell/py entries remain valid and are normalized at load time.
/// </summary>
public static class RemoteCommandsYaml
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnvironmentNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .EnablePrivateConstructors()
        .WithDuplicateKeyChecking()
        .Build();

    public static IReadOnlyList<RemoteCommandDefinition> ParseCommands(string text) =>
        ParseCatalog(text).Commands;

    public static RemoteCommandCatalog ParseCatalog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("commands.yaml is empty.");
        }

        CatalogFile? file;
        try
        {
            file = Deserializer.Deserialize<CatalogFile>(text);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException(
                $"YAML line {ex.Start.Line + 1}, column {ex.Start.Column + 1}: {ex.Message}",
                ex);
        }

        if (file?.Commands is null)
        {
            throw new InvalidDataException("YAML must have a top-level 'commands' list.");
        }

        var schema = file.Schema <= 0 ? 1 : file.Schema;
        if (schema > 2)
        {
            throw new InvalidDataException($"Unsupported commands.yaml schema {schema}; this build supports schema 1 and 2.");
        }

        var defaults = file.Defaults ?? new CatalogDefaultsFile();
        var normalized = file.Commands
            .Select((item, index) => NormalizeCommand(item, defaults, schema, index))
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("commands.yaml must contain at least one command.");
        }

        ValidateCommands(normalized);
        return new RemoteCommandCatalog(schema, normalized);
    }

    public static bool TryValidate(string text, out string? error)
    {
        try
        {
            _ = ParseCatalog(text);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or YamlException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string DefaultCommandsYaml { get; } = """
        schema: 2
        defaults:
          timeout_seconds: 300
          environment:
            CONDA_EXE: /home/lixr/miniconda3/bin/conda

        commands:
          - id: decode_stack
            label: Decode Kernel Stack
            group: Kernel
            description: Decode a kernel stack on the configured SSH host.
            runner: ssh
            command: /home/lixr/.local/bin/decode_kernel_stack
            arguments:
              - --file1
              - "{{input:input1:file}}"
              - --file2
              - "{{input:input2:file}}"
            inputs:
              - id: input1
                label: Kernel stack
                placeholder: Paste the stack trace
                kind: multiline
                required: true
              - id: input2
                label: Additional context
                placeholder: Optional symbols or context
                kind: multiline
                required: false
            tags: [kernel, debug]

          - id: decode_stack_with_symbols
            label: Decode Kernel Stack with Symbols
            group: Kernel
            description: Decode a kernel stack with symbol resolution enabled.
            runner: ssh
            command: /home/lixr/.local/bin/decode_kernel_stack
            arguments:
              - -s
              - --file1
              - "{{input:input1:file}}"
              - --file2
              - "{{input:input2:file}}"
            inputs:
              - id: input1
                label: Kernel stack
                placeholder: Paste the stack trace
                kind: multiline
                required: true
              - id: input2
                label: Symbol context
                placeholder: Optional symbol information
                kind: multiline
                required: false
            tags: [kernel, symbols]

          - id: gc_trace_analyzer
            label: GC Trace Analyzer
            group: Analysis
            description: Analyze garbage-collection traces on the remote host.
            runner: ssh
            command: /home/lixr/miniconda3/envs/android_automatic/bin/python3
            arguments:
              - /home/lixr/repo/androidtools/stat_tools/gc_trace_analyzer.py
              - --file1
              - "{{input:input1:file}}"
              - --file2
              - "{{input:input2:file}}"
            inputs:
              - id: input1
                label: Trace input
                placeholder: Paste the trace or analyzer input
                kind: multiline
                required: true
              - id: input2
                label: Additional input
                placeholder: Optional second input
                kind: multiline
                required: false
            tags: [gc, performance]

          - id: collect_rets
            label: Collect Multi-app Rets
            group: Collection
            description: Collect and report multi-application experiment results.
            runner: ssh
            command: /home/lixr/miniconda3/envs/android_automatic/bin/python3
            arguments:
              - /home/lixr/repo/androidtools/collect_MULTIAPP_rets.py
              - --file1
              - "{{input:input1:file}}"
              - --file2
              - "{{input:input2:file}}"
            inputs:
              - id: input1
                label: Primary input
                placeholder: Paste paths or collection parameters
                kind: multiline
                required: true
              - id: input2
                label: Secondary input
                placeholder: Optional second input
                kind: multiline
                required: false
            tags: [results, multi-app]

          - id: collect_pc_mark_rets
            label: Collect PCMark Rets
            group: Collection
            description: Collect and report PCMark experiment results.
            runner: ssh
            command: /home/lixr/miniconda3/envs/android_automatic/bin/python3
            arguments:
              - /home/lixr/repo/androidtools/collect_pc_mark_rets.py
              - --file1
              - "{{input:input1:file}}"
              - --file2
              - "{{input:input2:file}}"
            inputs:
              - id: input1
                label: Primary input
                placeholder: Paste paths or collection parameters
                kind: multiline
                required: true
              - id: input2
                label: Secondary input
                placeholder: Optional second input
                kind: multiline
                required: false
            tags: [results, pcmark]

          - id: replace_host
            label: Replace Host Directory
            group: Text transforms
            description: Replace the legacy local working-directory prefix with its remote HTTP URL.
            runner: transform
            command: replace_host_directory
            inputs:
              - id: input1
                label: Text
                placeholder: Paste text containing host paths
                kind: multiline
                required: true
            tags: [paths, url]

          - id: remove_comments
            label: Remove C++ Comments
            group: Text transforms
            description: Remove C++ line and block comments while preserving strings.
            runner: transform
            command: remove_cpp_comments
            inputs:
              - id: input1
                label: C++ source
                placeholder: Paste C++ source code
                kind: multiline
                required: true
            tags: [c++, comments]

          - id: remove_latex_comment_lines
            label: Remove LaTeX Comment Lines
            group: Text transforms
            description: Remove LaTeX lines whose first non-whitespace character is percent.
            runner: transform
            command: remove_latex_comment_lines
            inputs:
              - id: input1
                label: LaTeX source
                placeholder: Paste LaTeX source
                kind: multiline
                required: true
            tags: [latex, comments]

          - id: format_latex_comma_period_lines
            label: Format LaTeX Comma/Period Lines
            group: Text transforms
            description: Reflow plain LaTeX text after commas and periods while preserving command syntax.
            runner: transform
            command: format_latex_comma_period_lines
            inputs:
              - id: input1
                label: LaTeX source
                placeholder: Paste LaTeX source
                kind: multiline
                required: true
            tags: [latex, formatting]

          - id: add_extract_result_prefix
            label: Add Extract Result Prefix
            group: Text transforms
            description: Prefix every non-empty input line with extract_result.
            runner: transform
            command: add_extract_result_prefix
            inputs:
              - id: input1
                label: Lines
                placeholder: Paste one value per line
                kind: multiline
                required: true
            tags: [text, batch]

          - id: gen_rsync_from_folders
            label: Generate Rsync Commands from Folders
            group: Synchronization
            description: Generate rsync commands for remote folders and the postconditions database.
            runner: transform
            command: gen_rsync_from_folders
            inputs:
              - id: input1
                label: Remote folders
                placeholder: Paste one remote folder path per line
                kind: multiline
                required: true
            tags: [rsync, folders]
        """;

    public static string SshCommandSnippet { get; } = """

          - id: new_remote_tool
            label: New Remote Tool
            group: Custom
            description: Describe what the tool does.
            runner: ssh
            command: /absolute/path/to/tool
            arguments:
              - --file1
              - "{{input:source:file}}"
            inputs:
              - id: source
                label: Source
                placeholder: Paste input text
                kind: multiline
                required: true
            tags: [custom]
        """;

    public static string LocalCommandSnippet { get; } = """

          - id: new_local_tool
            label: New Local Tool
            group: Custom
            description: Run a local executable without changing the C# application.
            runner: local
            command: python
            arguments:
              - C:/path/to/tool.py
              - --input
              - "{{input:source:file}}"
            inputs:
              - id: source
                label: Source
                placeholder: Paste input text
                kind: multiline
                required: true
            tags: [custom, local]
        """;

    private static RemoteCommandDefinition NormalizeCommand(
        CommandFile item,
        CatalogDefaultsFile defaults,
        int schema,
        int index)
    {
        var id = (item.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id) && schema == 1)
        {
            id = CreateLegacyId(item.Label, index);
        }

        var label = string.IsNullOrWhiteSpace(item.Label) ? id : item.Label.Trim();
        var command = (item.Command ?? "").Trim();
        var runnerSource = string.IsNullOrWhiteSpace(item.Runner) ? item.Type : item.Runner;
        var runner = NormalizeRunner(runnerSource);
        var legacy = schema == 1 || string.IsNullOrWhiteSpace(item.Runner);

        var inputFiles = item.Inputs is null
            ? CreateDefaultInputs(runner, legacy)
            : item.Inputs.Select(NormalizeInput).ToArray();

        var arguments = (item.Arguments ?? [])
            .Select(value => value ?? "")
            .ToArray();

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (defaults.Environment is not null)
        {
            foreach (var pair in defaults.Environment)
            {
                environment[pair.Key] = pair.Value ?? "";
            }
        }

        if (item.Environment is not null)
        {
            foreach (var pair in item.Environment)
            {
                environment[pair.Key] = pair.Value ?? "";
            }
        }

        var timeout = item.TimeoutSeconds ?? defaults.TimeoutSeconds ?? 300;
        var host = (item.Host ?? "").Trim();
        var catalogDefaultHost = (defaults.Host ?? "").Trim();
        var hasTemplate = command.Contains("{{", StringComparison.Ordinal) ||
                          arguments.Any(argument => argument.Contains("{{", StringComparison.Ordinal));

        return new RemoteCommandDefinition(
            Id: id,
            Label: label,
            Command: command,
            Description: (item.Description ?? "").Trim(),
            Runner: runner,
            Group: (item.Group ?? "").Trim(),
            Host: host,
            TimeoutSeconds: timeout,
            Inputs: inputFiles,
            Arguments: arguments,
            Tags: (item.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToArray(),
            Environment: environment,
            WorkingDirectory: (item.WorkingDirectory ?? "").Trim(),
            LegacyFileArguments: legacy && runner == RemoteCommandRunners.Ssh && arguments.Length == 0 && !hasTemplate,
            CatalogDefaultHost: catalogDefaultHost);
    }

    private static RemoteCommandInputDefinition NormalizeInput(InputFile input)
    {
        var id = (input.Id ?? "").Trim();
        var label = string.IsNullOrWhiteSpace(input.Label) ? id : input.Label.Trim();
        var kind = string.IsNullOrWhiteSpace(input.Kind) ? "multiline" : input.Kind.Trim().ToLowerInvariant();
        return new RemoteCommandInputDefinition(
            id,
            label,
            (input.Placeholder ?? "").Trim(),
            kind,
            input.Required,
            input.Default ?? "",
            (input.Description ?? "").Trim());
    }

    private static IReadOnlyList<RemoteCommandInputDefinition> CreateDefaultInputs(string runner, bool legacy)
    {
        if (runner == RemoteCommandRunners.Transform)
        {
            return [new RemoteCommandInputDefinition("input1", "Input", "Paste input text", "multiline", true)];
        }

        if (legacy)
        {
            return
            [
                new RemoteCommandInputDefinition("input1", "Input 1", "Primary input", "multiline", false),
                new RemoteCommandInputDefinition("input2", "Input 2", "Secondary input", "multiline", false)
            ];
        }

        return [];
    }


    private static string CreateLegacyId(string? label, int index)
    {
        var source = (label ?? "command").Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder();
        var previousWasSeparator = false;
        foreach (var ch in source)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug) || !char.IsLetter(slug[0]))
        {
            slug = "command";
        }

        return $"{slug}-{index + 1}";
    }

    private static string NormalizeRunner(string? value)
    {
        return (value ?? "shell").Trim().ToLowerInvariant() switch
        {
            "shell" or "ssh" => RemoteCommandRunners.Ssh,
            "py" or "transform" => RemoteCommandRunners.Transform,
            "local" or "process" => RemoteCommandRunners.Local,
            var other => other
        };
    }

    private static void ValidateCommands(IReadOnlyList<RemoteCommandDefinition> commands)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id) || !IdentifierPattern.IsMatch(command.Id))
            {
                throw new InvalidDataException(
                    $"Command id '{command.Id}' is invalid. Use a letter followed by letters, digits, '_' or '-'.");
            }

            if (!ids.Add(command.Id))
            {
                throw new InvalidDataException($"Command id '{command.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(command.Label))
            {
                throw new InvalidDataException($"Command '{command.Id}' has no label.");
            }

            if (string.IsNullOrWhiteSpace(command.Command))
            {
                throw new InvalidDataException($"Command '{command.Id}' has no command value.");
            }

            if (command.Runner is not (RemoteCommandRunners.Ssh or RemoteCommandRunners.Local or RemoteCommandRunners.Transform))
            {
                throw new InvalidDataException(
                    $"Command '{command.Id}' uses unsupported runner '{command.Runner}'. Use ssh, local or transform.");
            }

            if (command.TimeoutSeconds is < 1 or > 86400)
            {
                throw new InvalidDataException($"Command '{command.Id}' timeout_seconds must be between 1 and 86400.");
            }

            if (command.UsesHost && !string.IsNullOrWhiteSpace(command.Host) &&
                !RemoteCommandExecutionService.IsValidSshDestination(command.Host, out var hostError))
            {
                throw new InvalidDataException(
                    $"Command '{command.Id}' has an invalid host: {hostError}");
            }

            if (command.UsesHost && !string.IsNullOrWhiteSpace(command.CatalogDefaultHost) &&
                !RemoteCommandExecutionService.IsValidSshDestination(command.CatalogDefaultHost, out var defaultHostError))
            {
                throw new InvalidDataException(
                    $"Command '{command.Id}' inherits an invalid defaults.host value: {defaultHostError}");
            }

            var inputIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in command.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Id) || !IdentifierPattern.IsMatch(input.Id))
                {
                    throw new InvalidDataException($"Command '{command.Id}' has invalid input id '{input.Id}'.");
                }

                if (!inputIds.Add(input.Id))
                {
                    throw new InvalidDataException($"Command '{command.Id}' duplicates input id '{input.Id}'.");
                }

                if (input.Kind is not ("text" or "multiline"))
                {
                    throw new InvalidDataException(
                        $"Command '{command.Id}' input '{input.Id}' uses unsupported kind '{input.Kind}'.");
                }
            }

            foreach (var variable in command.Environment.Keys)
            {
                if (!EnvironmentNamePattern.IsMatch(variable))
                {
                    throw new InvalidDataException(
                        $"Command '{command.Id}' has invalid environment variable name '{variable}'.");
                }
            }

            if (command.Runner == RemoteCommandRunners.Transform &&
                !RemoteCommandsTextTransforms.IsKnownTool(command.Command))
            {
                throw new InvalidDataException(
                    $"Command '{command.Id}' references unknown built-in transform '{command.Command}'. " +
                    "Use runner: local for an external script.");
            }

            if (command.Runner == RemoteCommandRunners.Local &&
                command.Arguments.Count == 0 &&
                command.Command.Contains("{{", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Local command '{command.Id}' uses placeholders without an arguments list. " +
                    "Declare arguments so values are passed through ProcessStartInfo.ArgumentList.");
            }

            RemoteCommandTemplate.Validate(command);
        }
    }

    private sealed class CatalogFile
    {
        public int Schema { get; set; } = 1;
        public CatalogDefaultsFile? Defaults { get; set; }
        public List<CommandFile>? Commands { get; set; }

        // Present in the original file. It is retained only for schema-1 compatibility.
        public List<string>? Types { get; set; }
    }

    private sealed class CatalogDefaultsFile
    {
        public string? Host { get; set; }
        public int? TimeoutSeconds { get; set; }
        public Dictionary<string, string?>? Environment { get; set; }
    }

    private sealed class CommandFile
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Command { get; set; }
        public string? Description { get; set; }
        public string? Runner { get; set; }
        public string? Type { get; set; }
        public string? Group { get; set; }
        public string? Host { get; set; }
        public int? TimeoutSeconds { get; set; }
        public string? WorkingDirectory { get; set; }
        public List<string?>? Arguments { get; set; }
        public List<string>? Tags { get; set; }
        public List<InputFile>? Inputs { get; set; }
        public Dictionary<string, string?>? Environment { get; set; }
    }

    private sealed class InputFile
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Placeholder { get; set; }
        public string? Kind { get; set; }
        public bool Required { get; set; }
        public string? Default { get; set; }
        public string? Description { get; set; }
    }
}
