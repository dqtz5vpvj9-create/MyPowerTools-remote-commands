using System.Text.Json.Nodes;

namespace RemoteCommands.Android.Tests;

/// <summary>
/// Keeps the packaged manifests, the module ids and the frozen surface/command contract in sync. A
/// rename that only touches code would otherwise ship a catalog pointing at a missing factory.
/// </summary>
public sealed class PackageManifestTests
{
    private static readonly string PackageRoot = ResolvePackageRoot();

    private static string ResolvePackageRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "package", "module.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "package");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("package/module.json was not found above the test output directory.");
    }

    private static JsonObject Read(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot, relativePath)))!.AsObject();

    [Fact]
    public void ModuleManifestMatchesTheModuleIdentity()
    {
        var manifest = Read("module.json");
        Assert.Equal(RemoteCommandsAndroidOptions.ModuleId, manifest["id"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.PackageId, manifest["packageId"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.ModuleVersion.ToString(), manifest["version"]!.GetValue<string>());

        var entrypoint = manifest["entrypoints"]!.AsArray()[0]!.AsObject();
        Assert.Equal("inproc-dotnet", entrypoint["kind"]!.GetValue<string>());
        Assert.Equal("RemoteCommands.Android.dll", entrypoint["assembly"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.ModuleTypeName, entrypoint["type"]!.GetValue<string>());
        Assert.Equal(
            new[] { "android-arm64", "android-x64" },
            entrypoint["platforms"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        // A remote command is a long-running operation; the in-proc budget must cover it.
        var maxCallMs = manifest["runtimePolicy"]!["inProcRules"]!["maxCallMs"]!.GetValue<int>();
        Assert.True(maxCallMs >= 3600000, $"maxCallMs must cover the longest command, was {maxCallMs}.");
        Assert.Equal(
            "inproc-or-sidecar",
            manifest["runtimePolicy"]!["operationRules"]!["longRunningCommand"]!.GetValue<string>());
    }

    [Fact]
    public void RequiredCapabilitiesAreDeclared()
    {
        var requires = Read("module.json")["requires"]!.AsArray()
            .ToDictionary(
                node => node!["capability"]!.GetValue<string>(),
                node => node!["required"]!.GetValue<bool>());

        Assert.True(requires["secret.store"]);
        Assert.False(requires["background.activity"]);
    }

    [Fact]
    public void ToolManifestPointsAtTheFrozenSurfaceContract()
    {
        var tool = Read("ui/tool.json");
        Assert.Equal(RemoteCommandsAndroidOptions.ToolId, tool["toolId"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.ModuleId, tool["ownerModuleId"]!.GetValue<string>());
        Assert.Equal("dotnet-surface", tool["type"]!.GetValue<string>());

        var route = tool["routes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("workspace", route["routeId"]!.GetValue<string>());
        var surface = route["surface"]!.AsObject();
        Assert.Equal("dotnet", surface["kind"]!.GetValue<string>());
        Assert.Equal(
            "surface/" + RemoteCommandsAndroidOptions.SurfaceAssemblyFileName,
            surface["assembly"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.SurfaceTypeName, surface["type"]!.GetValue<string>());
    }

    [Fact]
    public void CommandIndexOnlyReferencesKnownModuleCommands()
    {
        var index = Read("commands.index.json");
        foreach (var command in index["commands"]!.AsArray())
        {
            var id = command!["id"]!.GetValue<string>();
            Assert.StartsWith(RemoteCommandsAndroidOptions.ModuleId + ".", id, StringComparison.Ordinal);

            var execution = command["execution"]!.AsObject();
            if (execution["type"]!.GetValue<string>() == "navigation")
            {
                Assert.Equal(RemoteCommandsAndroidOptions.ToolId, execution["toolId"]!.GetValue<string>());
                Assert.Equal("workspace", execution["routeId"]!.GetValue<string>());
                continue;
            }

            Assert.Contains(id, RemoteCommandsAndroidOptions.CommandIds);
        }
    }

    [Fact]
    public void UiSurfacesAndStaticIndexesExistInThePackage()
    {
        var manifest = Read("module.json");
        Assert.Equal("commands.index.json", manifest["staticIndexes"]!["commands"]!.GetValue<string>());
        Assert.Equal("ui/tool.json", manifest["tools"]!.AsArray()[0]!.GetValue<string>());

        foreach (var surface in manifest["uiSurfaces"]!.AsArray())
        {
            Assert.True(
                File.Exists(Path.Combine(PackageRoot, surface!.GetValue<string>())),
                $"Declared ui surface is missing: {surface}");
        }
    }

    [Fact]
    public void PackageManifestCoversTheShippedDocuments()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(
            Path.Combine(PackageRoot, "..", "manifest", "android-package-manifest.json")))!.AsObject();
        Assert.Equal(RemoteCommandsAndroidOptions.PackageId, manifest["packageId"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.ModuleId, manifest["moduleId"]!.GetValue<string>());
        Assert.Equal(RemoteCommandsAndroidOptions.ToolId, manifest["toolId"]!.GetValue<string>());

        var declared = manifest["files"]!.AsArray()
            .Select(node => node!["path"]!.GetValue<string>())
            .ToArray();

        foreach (var required in new[]
                 {
                     "module.json",
                     "commands.index.json",
                     "ui/tool.json",
                     "ui/dashboard-card.json",
                     "ui/detail-page.json",
                     "ui/settings.json",
                     "ui/logs.json"
                 })
        {
            Assert.Contains(required, declared);
            Assert.True(File.Exists(Path.Combine(PackageRoot, required)), $"Package template is missing {required}.");
        }

        // The frozen surface contract must be discoverable from the manifest too.
        Assert.Contains(
            "ui/surface/" + RemoteCommandsAndroidOptions.SurfaceAssemblyFileName,
            declared);
    }

    [Fact]
    public void ErrorCodesStillMatchTheProtocolContract()
    {
        Assert.Equal(MyPowerTools.Protocol.MptErrorCodes.NotFound, RemoteCommandsAndroidErrorCodes.NotFound);
        Assert.Equal(MyPowerTools.Protocol.MptErrorCodes.RuntimeUnavailable, RemoteCommandsAndroidErrorCodes.RuntimeUnavailable);
        Assert.Equal(MyPowerTools.Protocol.MptErrorCodes.ValidationFailed, RemoteCommandsAndroidErrorCodes.ValidationFailed);
        Assert.Equal(MyPowerTools.Protocol.MptErrorCodes.PermissionRequired, RemoteCommandsAndroidErrorCodes.PermissionRequired);
        Assert.Equal(MyPowerTools.Protocol.MptErrorCodes.CapabilityMissing, RemoteCommandsAndroidErrorCodes.CapabilityMissing);
    }

    [Fact]
    public void ModuleDataDirectoryMapsToTheToolDirectoryTheSurfaceUses()
    {
        var state = Path.Combine(Path.GetTempPath(), "mpt-state");
        var moduleData = Path.Combine(state, "modules", RemoteCommandsAndroidOptions.ModuleId, "data");

        Assert.Equal(
            Path.Combine(state, "tools", RemoteCommandsAndroidOptions.ToolId),
            RemoteCommandsAndroidPaths.ResolveDataRoot(moduleData));

        // An unexpected shape keeps the module's own directory instead of guessing.
        var plain = Path.Combine(state, "elsewhere");
        Assert.Equal(plain, RemoteCommandsAndroidPaths.ResolveDataRoot(plain));
    }

    [Fact]
    public async Task CommandDescriptorsMatchTheDeclaredCommandIds()
    {
        var module = new RemoteCommandsAndroidModule();
        var descriptors = await module.ListCommandsAsync(CancellationToken.None);
        Assert.Equal(
            RemoteCommandsAndroidOptions.CommandIds.OrderBy(id => id, StringComparer.Ordinal),
            descriptors.Select(descriptor => descriptor.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("action", descriptors[0].Kind);
        Assert.All(descriptors, descriptor => Assert.Equal(RemoteCommandsAndroidOptions.ModuleId, descriptor.ModuleId));

        var run = descriptors.Single(descriptor => descriptor.Id == RemoteCommandsAndroidOptions.CommandRun);
        Assert.True(run.SupportsCancellation);
        Assert.True(run.SupportsProgress);
    }
}
