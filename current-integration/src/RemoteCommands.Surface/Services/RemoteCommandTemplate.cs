using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCommands.Surface.Services;

/// <summary>
/// Expands the small, explicit placeholder language used by schema-2 commands.
/// Input values are quoted when a command is rendered for a remote shell; direct local
/// process arguments are passed through ProcessStartInfo.ArgumentList without shell parsing.
/// </summary>
public static class RemoteCommandTemplate
{
    private static readonly Regex InputPlaceholder = new(
        @"\{\{\s*input\s*:([A-Za-z][A-Za-z0-9_-]*)(?::(text|file))?\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static void Validate(RemoteCommandDefinition command)
    {
        var inputs = command.Inputs
            .Select(input => input.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in EnumerateTemplates(command))
        {
            foreach (Match match in InputPlaceholder.Matches(template))
            {
                var id = match.Groups[1].Value;
                if (!inputs.Contains(id))
                {
                    throw new InvalidDataException(
                        $"Command '{command.Id}' references unknown input '{id}' in '{match.Value}'.");
                }
            }

            var unmatched = InputPlaceholder.Replace(template, "");
            if (unmatched.Contains("{{", StringComparison.Ordinal) ||
                unmatched.Contains("}}", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Command '{command.Id}' contains an invalid placeholder. " +
                    "Use {{input:id:text}} or {{input:id:file}}.");
            }
        }

        if (command.Runner == RemoteCommandRunners.Transform && command.Arguments.Count != 0)
        {
            throw new InvalidDataException(
                $"Built-in transform '{command.Id}' cannot declare process arguments.");
        }
    }

    public static IReadOnlySet<string> GetFileInputIds(RemoteCommandDefinition command)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (command.LegacyFileArguments)
        {
            foreach (var input in command.Inputs.Take(2))
            {
                result.Add(input.Id);
            }
        }

        foreach (var template in EnumerateTemplates(command))
        {
            foreach (Match match in InputPlaceholder.Matches(template))
            {
                if (string.Equals(match.Groups[2].Value, "file", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(match.Groups[1].Value);
                }
            }
        }

        return result;
    }

    public static string BuildShellCommand(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> files)
    {
        string body;
        if (command.Arguments.Count > 0)
        {
            var executable = Render(command.Command, values, files, replacement => replacement);
            var builder = new StringBuilder(ShellQuote(executable));
            foreach (var argument in command.Arguments)
            {
                builder.Append(' ');
                var rendered = Render(argument, values, files, replacement => replacement);
                builder.Append(ShellQuote(rendered));
            }

            body = builder.ToString();
        }
        else
        {
            body = Render(command.Command, values, files, ShellQuote);
        }

        if (command.LegacyFileArguments)
        {
            var first = command.Inputs.ElementAtOrDefault(0)?.Id ?? "input1";
            var second = command.Inputs.ElementAtOrDefault(1)?.Id ?? "input2";
            body += $" --file1 {ShellQuote(files.GetValueOrDefault(first, ""))}";
            body += $" --file2 {ShellQuote(files.GetValueOrDefault(second, ""))}";
        }

        if (command.Environment.Count == 0)
        {
            return body;
        }

        var environment = string.Join(
            " ",
            command.Environment.Select(pair => $"{pair.Key}={ShellQuote(pair.Value)}"));
        return environment + " " + body;
    }

    public static string RenderLocalExecutable(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> files) =>
        Render(command.Command, values, files, replacement => replacement);

    public static IReadOnlyList<string> RenderLocalArguments(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> files) =>
        command.Arguments
            .Select(argument => Render(argument, values, files, replacement => replacement))
            .ToArray();

    public static string RenderLocalShellCommand(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> files) =>
        Render(command.Command, values, files, QuoteForLocalShell);

    public static string ShellQuote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string QuoteForLocalShell(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return ShellQuote(value);
    }

    private static string Render(
        string template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> files,
        Func<string, string> quote)
    {
        return InputPlaceholder.Replace(template, match =>
        {
            var id = match.Groups[1].Value;
            var mode = match.Groups[2].Success ? match.Groups[2].Value : "text";
            var replacement = string.Equals(mode, "file", StringComparison.OrdinalIgnoreCase)
                ? files.GetValueOrDefault(id, "")
                : values.GetValueOrDefault(id, "");
            return quote(replacement);
        });
    }

    private static IEnumerable<string> EnumerateTemplates(RemoteCommandDefinition command)
    {
        yield return command.Command;
        foreach (var argument in command.Arguments)
        {
            yield return argument;
        }
    }
}
