using System.Text;

namespace RemoteCommands.Surface.Services;

/// <summary>
/// C# port of the original powertool/command_tools.py text transforms used by the py command type.
/// </summary>
public static class RemoteCommandsTextTransforms
{
    private const string AospHostDir = "/home/lixr/aosp_host_working_dir/";
    private const string AospHostUrl = "http://r743.ipads-lab.se.sjtu.edu.cn:7112/";
    private const string PostconditionsDbRsync =
        "rsync -avP r743-autodroid:/home/lxr2/repo/androidtools/AutoDroid/data/postconditions_db/ $AutoDroid/data/postconditions_db/";

    private static readonly string[] LatexBlockCommands =
    [
        "part", "chapter", "section", "subsection", "subsubsection", "paragraph",
        "subparagraph", "label", "begin", "end", "item"
    ];

    private static readonly string[] LatexStandaloneBlockCommands =
    [
        "emph", "textbf", "textit"
    ];

    private static readonly string[] ProtectedPlainTextTokens =
    [
        "e.g.", "i.e.", "etc.", "cf.", "vs.", "Fig.", "Eq.", "Sec.", "Tab.",
        "Dr.", "Mr.", "Ms.", "Prof.", "et al."
    ];

    public static string ReplaceHostDirectory(string text)
    {
        return text.Replace(AospHostDir, AospHostUrl, StringComparison.Ordinal);
    }

    public static string RemoveLatexCommentLines(string text)
    {
        return string.Concat(
            SplitLinesKeepEndings(text)
                .Where(line => !line.TrimStart().StartsWith('%')));
    }

    public static string RemoveCppComments(string source)
    {
        var result = new StringBuilder();
        var state = "normal";
        var quote = '\0';

        for (var i = 0; i < source.Length;)
        {
            var ch = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (state == "normal")
            {
                if (ch is '"' or '\'')
                {
                    result.Append(ch);
                    quote = ch;
                    state = "string";
                    i++;
                }
                else if (ch == '/' && next == '/')
                {
                    state = "line-comment";
                    i += 2;
                }
                else if (ch == '/' && next == '*')
                {
                    state = "block-comment";
                    i += 2;
                }
                else
                {
                    result.Append(ch);
                    i++;
                }
            }
            else if (state == "string")
            {
                result.Append(ch);
                if (ch == '\\' && i + 1 < source.Length)
                {
                    result.Append(source[i + 1]);
                    i += 2;
                }
                else if (ch == quote)
                {
                    state = "normal";
                    i++;
                }
                else
                {
                    i++;
                }
            }
            else if (state == "line-comment")
            {
                if (ch == '\r' && next == '\n')
                {
                    result.Append("\r\n");
                    state = "normal";
                    i += 2;
                }
                else if (ch is '\r' or '\n')
                {
                    result.Append(ch);
                    state = "normal";
                    i++;
                }
                else
                {
                    i++;
                }
            }
            else if (state == "block-comment")
            {
                if (ch == '*' && next == '/')
                {
                    state = "normal";
                    i += 2;
                }
                else if (ch == '\r' && next == '\n')
                {
                    result.Append("\r\n");
                    i += 2;
                }
                else if (ch is '\r' or '\n')
                {
                    result.Append(ch);
                    i++;
                }
                else
                {
                    i++;
                }
            }
        }

        return result.ToString();
    }

    public static string AddExtractResultPrefix(string lines)
    {
        return string.Join('\n', SplitLines(lines).Select(line => "extract_result " + line));
    }

    public static string GenerateRsyncCommands(string lines)
    {
        var rsync = string.Join('\n',
            SplitLines(lines)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => "rsync -avP r743-autodroid:" + line + " $aosp_host_working_dir/"));
        return rsync + '\n' + PostconditionsDbRsync;
    }

    public static string FormatLatexCommaPeriodLines(string text)
    {
        var result = new StringBuilder();
        var pendingSpace = false;
        var i = 0;

        void AppendSpaceIfNeeded()
        {
            if (pendingSpace && result.Length > 0 && result[^1] is not (' ' or '\n'))
            {
                result.Append(' ');
            }

            pendingSpace = false;
        }

        void AppendNewline()
        {
            while (result.Length > 0 && result[^1] == ' ')
            {
                result.Length--;
            }

            if (result.Length > 0 && result[^1] != '\n')
            {
                result.Append('\n');
            }
        }

        while (i < text.Length)
        {
            var ch = text[i];

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                i++;
                continue;
            }

            if (ch == '%')
            {
                var end = ConsumeLatexComment(text, i);
                AppendSpaceIfNeeded();
                result.Append(text[i..end].TrimEnd());
                AppendNewline();
                i = end;
                continue;
            }

            if (StartsAt(text, i, "\\["))
            {
                var end = ConsumeLatexCommandMath(text, i, "\\]");
                AppendNewline();
                result.Append(TrimOutputLines(text[i..end]));
                AppendNewline();
                i = end;
                continue;
            }

            if (StartsAt(text, i, "\\("))
            {
                var end = ConsumeLatexCommandMath(text, i, "\\)");
                AppendSpaceIfNeeded();
                result.Append(text[i..end]);
                i = end;
                continue;
            }

            if (ch == '\\')
            {
                var (end, commandName) = ConsumeLatexCommand(text, i);
                var token = text[i..end];
                var isBlock = LatexBlockCommands.Contains(commandName, StringComparer.Ordinal) ||
                    (LatexStandaloneBlockCommands.Contains(commandName, StringComparer.Ordinal) &&
                     IsLatexCommandAloneOnLine(text, i, end));
                if (isBlock)
                {
                    AppendNewline();
                    result.Append(TrimOutputLines(token));
                    AppendNewline();
                }
                else
                {
                    AppendSpaceIfNeeded();
                    result.Append(token);
                }

                i = end;
                continue;
            }

            if (ch == '$')
            {
                var end = ConsumeLatexMath(text, i);
                if (StartsAt(text, i, "$$"))
                {
                    AppendNewline();
                    result.Append(TrimOutputLines(text[i..end]));
                    AppendNewline();
                }
                else
                {
                    AppendSpaceIfNeeded();
                    result.Append(text[i..end]);
                }

                i = end;
                continue;
            }

            var (protectedToken, protectedLength) = MatchProtectedPlainTextToken(text, i);
            if (protectedToken is not null)
            {
                AppendSpaceIfNeeded();
                result.Append(protectedToken);
                i += protectedLength;
                continue;
            }

            if (ch is not (',' or '.'))
            {
                AppendSpaceIfNeeded();
            }

            result.Append(ch);
            if (ch is ',' or '.')
            {
                AppendNewline();
                pendingSpace = false;
            }

            i++;
        }

        var output = TrimOutputLines(result.ToString());
        return output + (text.EndsWith('\n') ? "\n" : "");
    }

    public static string Apply(string toolName, string input)
    {
        return toolName switch
        {
            "replace_host_directory" => ReplaceHostDirectory(input),
            "remove_cpp_comments" => RemoveCppComments(input),
            "remove_latex_comment_lines" => RemoveLatexCommentLines(input),
            "format_latex_comma_period_lines" => FormatLatexCommaPeriodLines(input),
            "add_extract_result_prefix" => AddExtractResultPrefix(input),
            "gen_rsync_from_folders" => GenerateRsyncCommands(input),
            _ => throw new InvalidOperationException($"Unknown Python command tool '{toolName}'.")
        };
    }

    public static bool IsKnownTool(string toolName)
    {
        return toolName is "replace_host_directory" or "remove_cpp_comments" or
            "remove_latex_comment_lines" or "format_latex_comma_period_lines" or
            "add_extract_result_prefix" or "gen_rsync_from_folders";
    }

    private static int ConsumeLatexGroup(string source, int start)
    {
        var pairs = new Dictionary<char, char> { ['{'] = '}', ['['] = ']' };
        var stack = new Stack<char>();
        stack.Push(pairs[source[start]]);
        var i = start + 1;

        while (i < source.Length)
        {
            var ch = source[i];
            if (ch == '\\')
            {
                i += 2;
            }
            else if (pairs.ContainsKey(ch))
            {
                stack.Push(pairs[ch]);
                i++;
            }
            else if (ch == stack.Peek())
            {
                stack.Pop();
                i++;
                if (stack.Count == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return source.Length;
    }

    private static (int End, string Name) ConsumeLatexCommand(string source, int start)
    {
        var i = start + 1;
        var commandName = "";

        if (i < source.Length && char.IsLetter(source[i]))
        {
            var nameStart = i;
            while (i < source.Length && char.IsLetter(source[i]))
            {
                i++;
            }

            commandName = source[nameStart..i];
            if (i < source.Length && source[i] == '*')
            {
                i++;
            }
        }
        else if (i < source.Length)
        {
            commandName = source[i].ToString();
            i++;
        }

        while (i < source.Length)
        {
            var whitespaceStart = i;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
            {
                i++;
            }

            if (i < source.Length && source[i] is '{' or '[')
            {
                i = ConsumeLatexGroup(source, i);
            }
            else
            {
                i = whitespaceStart;
                break;
            }
        }

        return (i, commandName);
    }

    private static int ConsumeLatexMath(string source, int start)
    {
                var delimiter = StartsAt(source, start, "$$") ? "$$" : "$";
        var i = start + delimiter.Length;

        while (i < source.Length)
        {
            if (source[i] == '\\')
            {
                i += 2;
            }
            else if (StartsAt(source, i, delimiter))
            {
                return i + delimiter.Length;
            }
            else
            {
                i++;
            }
        }

        return source.Length;
    }

    private static int ConsumeLatexCommandMath(string source, int start, string closer)
    {
        var i = start + 2;
        while (i < source.Length)
        {
            if (StartsAt(source, i, closer))
            {
                return i + closer.Length;
            }

            if (source[i] == '\\')
            {
                i += 2;
            }
            else
            {
                i++;
            }
        }

        return source.Length;
    }

    private static int ConsumeLatexComment(string source, int start)
    {
        var i = start;
        while (i < source.Length && source[i] is not ('\r' or '\n'))
        {
            i++;
        }

        return i;
    }

    private static bool IsLatexCommandAloneOnLine(string source, int start, int end)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var nextLf = source.IndexOf('\n', end);
        var nextCr = source.IndexOf('\r', end);
        var lineEnd = source.Length;
        if (nextLf != -1)
        {
            lineEnd = Math.Min(lineEnd, nextLf);
        }

        if (nextCr != -1)
        {
            lineEnd = Math.Min(lineEnd, nextCr);
        }

        return string.IsNullOrWhiteSpace(source[lineStart..start]) &&
               string.IsNullOrWhiteSpace(source[end..lineEnd]);
    }

    private static string TrimOutputLines(string text)
    {
        return string.Join('\n', SplitLines(text).Select(line => line.TrimEnd())).Trim();
    }

    private static bool StartsAt(string source, int index, string value)
    {
        return index + value.Length <= source.Length &&
               string.CompareOrdinal(source, index, value, 0, value.Length) == 0;
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static IEnumerable<string> SplitLinesKeepEndings(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var start = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '\n')
            {
                yield return normalized[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < normalized.Length)
        {
            yield return normalized[start..];
        }
    }

    private static (string? Token, int Length) MatchProtectedPlainTextToken(string source, int start)
    {
        var before = start > 0 ? source[start - 1] : '\0';
        if (before != '\0' && (char.IsLetterOrDigit(before) || before is '_' or '~'))
        {
            return (null, 0);
        }

        foreach (var token in ProtectedPlainTextTokens)
        {
            var output = new StringBuilder();
            var i = start;
            var matched = true;

            for (var index = 0; index < token.Length; index++)
            {
                var tokenCh = token[index];
                if (char.IsWhiteSpace(tokenCh))
                {
                    if (i >= source.Length || !char.IsWhiteSpace(source[i]))
                    {
                        matched = false;
                        break;
                    }

                    while (i < source.Length && char.IsWhiteSpace(source[i]))
                    {
                        i++;
                    }

                    output.Append(' ');
                    continue;
                }

                if (i >= source.Length || char.ToLowerInvariant(source[i]) != char.ToLowerInvariant(tokenCh))
                {
                    matched = false;
                    break;
                }

                output.Append(source[i]);
                i++;

                var nextTokenCh = index + 1 < token.Length ? token[index + 1] : '\0';
                if (nextTokenCh != '\0' && !char.IsWhiteSpace(nextTokenCh) && tokenCh == '.')
                {
                    while (i < source.Length && char.IsWhiteSpace(source[i]))
                    {
                        i++;
                    }
                }
            }

            if (!matched)
            {
                continue;
            }

            var end = i;
            if (end < source.Length && source[end] == ',')
            {
                output.Append(source[end]);
                end++;
            }

            var after = end < source.Length ? source[end] : '\0';
            if (after != '\0' && (char.IsLetterOrDigit(after) || after is '_' or '~'))
            {
                continue;
            }

            return (output.ToString(), end - start);
        }

        return (null, 0);
    }
}
