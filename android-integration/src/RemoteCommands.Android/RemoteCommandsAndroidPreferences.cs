using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCommands.Android;

/// <summary>
/// Android-only knobs kept beside (never inside) the desktop-compatible <c>settings.json</c>.
///
/// The desktop executor hardcodes the conda path and has no per-run timeout because it can just wait
/// on a local process. On the phone both have to be explicit: the conda prefix is used exactly like
/// the desktop (the configured path is only what gets sent as <c>CONDA_EXE</c>), and the timeout keeps
/// a stuck SSH channel from holding a foreground-service lease forever.
/// </summary>
internal sealed class RemoteCommandsAndroidPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [JsonPropertyName("condaExecutable")]
    public string CondaExecutable { get; init; } = RemoteCommandsAndroidOptions.DefaultCondaExecutable;

    [JsonPropertyName("commandTimeoutMinutes")]
    public int CommandTimeoutMinutes { get; init; } = RemoteCommandsAndroidOptions.DefaultCommandTimeoutMinutes;

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(20);

    public TimeSpan CommandTimeout =>
        TimeSpan.FromMinutes(Math.Clamp(CommandTimeoutMinutes, 1, 24 * 60));

    public static RemoteCommandsAndroidPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new RemoteCommandsAndroidPreferences();
            }

            var loaded = JsonSerializer.Deserialize<RemoteCommandsAndroidPreferences>(File.ReadAllText(path), JsonOptions);
            return (loaded ?? new RemoteCommandsAndroidPreferences()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RemoteCommandsAndroidPreferences();
        }
    }

    public static void Save(string path, RemoteCommandsAndroidPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("远程命令私有目录不可用。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Normalize(), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public RemoteCommandsAndroidPreferences Normalize() => new()
    {
        CondaExecutable = string.IsNullOrWhiteSpace(CondaExecutable)
            ? RemoteCommandsAndroidOptions.DefaultCondaExecutable
            : CondaExecutable.Trim(),
        CommandTimeoutMinutes = Math.Clamp(CommandTimeoutMinutes, 1, 24 * 60)
    };

    public RemoteCommandsAndroidPreferences With(string? condaExecutable, int? commandTimeoutMinutes) =>
        new RemoteCommandsAndroidPreferences
        {
            CondaExecutable = string.IsNullOrWhiteSpace(condaExecutable) ? CondaExecutable : condaExecutable.Trim(),
            CommandTimeoutMinutes = commandTimeoutMinutes ?? CommandTimeoutMinutes
        }.Normalize();
}
