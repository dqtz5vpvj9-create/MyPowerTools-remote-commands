namespace RemoteCommands.Android;

/// <summary>
/// Stable identifiers shared by the Android module, the mobile surface, the packaged manifests and
/// the tests. Keeping them in one place is what lets a test assert that <c>package/module.json</c>,
/// <c>package/ui/tool.json</c> and the data-directory mapping still agree after a rename.
/// </summary>
internal static class RemoteCommandsAndroidOptions
{
    /// <summary>
    /// Module id, package id and tool id intentionally agree. The desktop product keeps its own
    /// <c>android-tools.remote-commands</c> module inside the <c>android-tools-suite</c> package, so
    /// the phone declares a distinct id exactly like <c>remote-notifications-android</c> does.
    /// </summary>
    public const string ModuleId = "remote-commands-android";

    public const string PackageId = ModuleId;

    public const string ToolId = ModuleId;

    public const string DisplayName = "远程命令";

    /// <summary>Title of the foreground notification shown while an SSH command is running.</summary>
    public const string BackgroundActivityTitle = "远程命令执行中";

    // Files that follow the desktop product layout, read and written through the shared
    // RemoteCommandsStore so the phone and the desktop keep the same semantics.
    public const string CommandsFileName = "commands.yaml";
    public const string SettingsFileName = "settings.json";
    public const string HistoryFileName = "history.json";

    // Android-only files; never mixed into the desktop-compatible settings/history documents.
    public const string HostsFileName = "android-hosts.json";
    public const string HostKeysFileName = "known-host-keys.json";
    public const string PreferencesFileName = "android-preferences.json";

    /// <summary>Remote directory the two input files are uploaded to, matching the desktop executor.</summary>
    public const string RemoteUploadDirectory = "/tmp";

    /// <summary>Same CONDA_EXE contract the desktop executor uses; configurable per device.</summary>
    public const string DefaultCondaExecutable = "/home/lixr/miniconda3/bin/conda";

    public const int DefaultCommandTimeoutMinutes = 30;

    public const int MaxOutputLength = 512 * 1024;

    public const string SurfaceAssemblyFileName = "MyPowerTools.MobileRemoteCommands.dll";

    public const string SurfaceTypeName =
        "MyPowerTools.MobileRemoteCommands.RemoteCommandsMobileSurfaceFactory";

    public const string ModuleTypeName = "RemoteCommands.Android.RemoteCommandsAndroidModule";

    // ------------------------------------------------------------------ command ids

    public const string CommandStatus = "remote-commands-android.status";
    public const string CommandCatalog = "remote-commands-android.catalog";
    public const string CommandCatalogSave = "remote-commands-android.catalog.save";
    public const string CommandSettingsUpdate = "remote-commands-android.settings.update";
    public const string CommandRun = "remote-commands-android.run";
    public const string CommandCancel = "remote-commands-android.cancel";
    public const string CommandHostsList = "remote-commands-android.hosts.list";
    public const string CommandHostAdd = "remote-commands-android.host.add";
    public const string CommandHostRemove = "remote-commands-android.host.remove";
    public const string CommandHostKeyStatus = "remote-commands-android.host-key.status";
    public const string CommandHostKeyAccept = "remote-commands-android.host-key.accept";
    public const string CommandHostKeyRevoke = "remote-commands-android.host-key.revoke";
    public const string CommandHistorySummary = "remote-commands-android.history.summary";
    public const string CommandHistoryClear = "remote-commands-android.history.clear";
    public const string CommandTransform = "remote-commands-android.transform";

    public static IReadOnlyList<string> CommandIds { get; } =
    [
        CommandStatus,
        CommandCatalog,
        CommandCatalogSave,
        CommandSettingsUpdate,
        CommandRun,
        CommandCancel,
        CommandHostsList,
        CommandHostAdd,
        CommandHostRemove,
        CommandHostKeyStatus,
        CommandHostKeyAccept,
        CommandHostKeyRevoke,
        CommandHistorySummary,
        CommandHistoryClear,
        CommandTransform
    ];

    // ------------------------------------------------------------------ run arguments

    public const string ArgumentCommandId = "commandId";
    public const string ArgumentHost = "host";
    public const string ArgumentInput1 = "input1";
    public const string ArgumentInput2 = "input2";
    public const string ArgumentSecondInput = "secondInput";
    public const string ArgumentInvocationId = "invocationId";

    /// <summary>
    /// The full commands.yaml document for <c>catalog.save</c>. There is no path argument on purpose:
    /// the module always writes its own canonical data-directory copy.
    /// </summary>
    public const string ArgumentContent = "content";

    /// <summary>The settings document for <c>settings.update</c>: five editable keys, partial allowed.</summary>
    public const string ArgumentValues = "values";

    // host.add arguments
    public const string ArgumentAlias = "alias";
    public const string ArgumentRealHost = "hostName";
    public const string ArgumentPort = "port";
    public const string ArgumentUsername = "username";
    public const string ArgumentAuthKind = "auth";
    public const string ArgumentPassword = "password";
    public const string ArgumentPrivateKey = "privateKey";
    public const string ArgumentPassphrase = "passphrase";

    // host-key.accept arguments
    public const string ArgumentFingerprint = "fingerprint";

    /// <summary>Secret names are `host.&lt;alias&gt;.&lt;field&gt;`; aliases are sanitized first.</summary>
    public const string SecretNamePrefix = "host.";

    public static Version ModuleVersion { get; } = new(0, 1, 0);
}

/// <summary>
/// Resolves the private data root the Android module and the mobile surface share.
///
/// The runtime hands a module <c>&lt;state&gt;/modules/&lt;moduleId&gt;/data</c>, while the Shell hands a
/// dotnet surface <c>&lt;state&gt;/tools/&lt;toolId&gt;</c>. Both must read and write the same
/// <c>commands.yaml</c>, <c>settings.json</c> and <c>history.json</c>, otherwise the phone page would
/// show a second command catalog and a second history. The mapping is the same structural mapping the
/// desktop AndroidTools adapter performs, restricted to the module layout so an unexpected directory
/// shape falls back to the module's own private directory instead of guessing.
/// </summary>
internal static class RemoteCommandsAndroidPaths
{
    public static string ResolveDataRoot(string moduleDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDataDirectory);
        var data = new DirectoryInfo(Path.GetFullPath(moduleDataDirectory));
        var module = data.Parent;
        var modules = module?.Parent;
        var state = modules?.Parent;
        if (string.Equals(data.Name, "data", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(module?.Name, RemoteCommandsAndroidOptions.ModuleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(modules?.Name, "modules", StringComparison.OrdinalIgnoreCase) &&
            state is not null)
        {
            return Path.Combine(state.FullName, "tools", RemoteCommandsAndroidOptions.ToolId);
        }

        return data.FullName;
    }

    public static string CommandsPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.CommandsFileName);

    public static string SettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.SettingsFileName);

    public static string HistoryPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.HistoryFileName);

    public static string HostsPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.HostsFileName);

    public static string HostKeysPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.HostKeysFileName);

    public static string PreferencesPath(string dataRoot) =>
        Path.Combine(dataRoot, RemoteCommandsAndroidOptions.PreferencesFileName);
}
