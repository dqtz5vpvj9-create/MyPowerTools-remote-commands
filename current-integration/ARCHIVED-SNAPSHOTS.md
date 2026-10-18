# Archived integration snapshots

The 4 files listed below (2,826 physical source lines) were retired from the current tree because they are superseded integration snapshots, not current build inputs.

The active UI is current-integration/src/RemoteCommands.Surface. The shared AndroidTools adapter/runtime is built only from the remote-notifications submodule; build.ps1 stages its shared package and builds this tool's Surface.

The superproject solution, project includes, tool build scripts, package manifests, and runtime source staging do not consume these paths. Existing Surface XAML, reflection-loaded factory types, shared runtime ownership, original-source trees, and linked tests remain unchanged. No runtime behavior change or build-time speedup is claimed from deleting files that were not compiled.

## Restore from Git history

Recovery commit in this tool repository: `dcd29ed8b6c9897e412a86e1356421a2d5302fd5`. Keep the repository history containing this commit when publishing or mirroring it. A shallow clone or a source ZIP alone may not contain these historical blobs.

Run from this tool repository after saving any current changes:

```sh
git cat-file -e dcd29ed8b6c9897e412a86e1356421a2d5302fd5^{commit}
git restore --source=dcd29ed8b6c9897e412a86e1356421a2d5302fd5 -- current-integration/src/AndroidTools.MyPowerTools current-integration/src/AndroidTools.Runtime
```

If the commit is absent, obtain it from an existing full clone or the repository remote containing this history before restoring. Restoring adds historical source files to the working tree; it does not wire them back into the current build.

The original source-map capture fields remain historical provenance. Its archivedIntegrationSnapshots entries describe the later retirement and point to this document, which stays inside current-integration so source-bundle materialization preserves it.

## Exact file recovery inventory

| Path | Git blob SHA-1 | Physical lines |
| --- | --- | ---: |
| `current-integration/src/AndroidTools.MyPowerTools/AndroidTools.MyPowerTools.csproj` | `e2f630e541ee44d9df97826d3fe5e1c75067dc41` | 21 |
| `current-integration/src/AndroidTools.MyPowerTools/AndroidToolsModules.cs` | `706b00236159e5035c0aeeb4caf5a8b6ef9ccb3b` | 2239 |
| `current-integration/src/AndroidTools.Runtime/AndroidTools.Runtime.csproj` | `5f58d3c3b7add904c843fe45be14284222adac2a` | 31 |
| `current-integration/src/AndroidTools.Runtime/Program.cs` | `ff742479486f8fd9413f1295d2390a584ec857ce` | 535 |
