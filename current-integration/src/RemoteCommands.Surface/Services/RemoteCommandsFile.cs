using System.Text;

namespace RemoteCommands.Surface.Services;

/// <summary>
/// Writes the Remote Commands data files through a temporary file in the same directory and
/// then moves it over the live file, so an interrupted or failed write never leaves the user
/// with a truncated commands.yaml, settings.json or history.json.
/// </summary>
internal static class RemoteCommandsFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content)
    {
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            File.WriteAllText(temporaryPath, content, Utf8);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporaryPath);
        }
    }

    public static async Task WriteAsync(string path, string content)
    {
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Utf8).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporaryPath);
        }
    }

    private static string CreateTemporaryPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException($"Remote Commands data path '{path}' has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void DeleteTemporary(string temporaryPath)
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }
}
