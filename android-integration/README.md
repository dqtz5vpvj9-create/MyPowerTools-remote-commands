# Remote Commands — Android integration

Android in-process adapter for the existing MyPowerTools **Remote Commands** product
(`tools/remote-commands`). It lets the phone run the *same* `commands.yaml` catalog the desktop
product uses, without `ssh.exe`/`scp.exe` and without a platform `network.ssh` capability.

- module adapter + package template + build script: this directory
- phone surface: `src/MyPowerTools.MobileRemoteCommands` (separate deliverable, same tool)
- shared product sources (linked read-only, never edited here):
  `tools/remote-commands/current-integration/src/RemoteCommands.Surface/Services/`

## 1. Results

`dotnet build` / `dotnet test` (see §6) produce:

| Artifact | Path |
| --- | --- |
| module assembly | `src/RemoteCommands.Android/bin/<config>/net10.0/RemoteCommands.Android.dll` |
| test assembly | `tests/RemoteCommands.Android.Tests/bin/<config>/net10.0/` |
| staged package | `artifacts/package/remote-commands-android/` (git-ignored, mirrored to `modules/remote-commands-android` unless `-NoMirror`) |

The staged package contains `module.json`, `commands.index.json`, `ui/*.json`, the adapter and its
managed SSH dependency closure (`Renci.SshNet.dll`, `BouncyCastle.Cryptography.dll`,
`Microsoft.Extensions.Logging.Abstractions.dll`,
`Microsoft.Extensions.DependencyInjection.Abstractions.dll`).

## 2. Frozen module command contract

Module id / package id / tool id: `remote-commands-android`
(`RemoteCommandsAndroidOptions` is the single source of truth; tests assert it against
`package/module.json` and `package/ui/tool.json`).

| Command | Arguments | Result payload (JSON) |
| --- | --- | --- |
| `remote-commands-android.status` | – | `commandCount`, `commandsError`, `defaultHost`, `knownHosts`, `historyRetention`, `condaExecutable`, `commandTimeoutMinutes`, `hosts[]`, `trustedHostKeys[]`, `activeInvocationId`, `activeStage`, `lastRunState`, `lastRunSummary`, `backgroundAvailable`, `transportAvailable`, `dataDirectory` |
| `remote-commands-android.catalog` | – | `commands[]` (`id`, `label`, `command`, `description`, `type`, `host`, `input1Label`, `input1Placeholder`, `input2Label`, `input2Placeholder`, `showSecondInput`, `usesRemoteHost`), `error`, `commandsPath` |
| `remote-commands-android.catalog.save` | `content` (full YAML document) | `saved`, `contentLength`, `saveError` (rejected only), `commands[]`, `error`, `commandsPath`. Invalid YAML returns a **failed** command whose payload still carries the unchanged catalog and the validation message; the file is never touched. There is no path argument — the target is always the module's canonical `<dataDir>/commands.yaml`. |
| `remote-commands-android.settings.update` | `values` (object, any subset of the five editable keys) | `saved`, `appliedKeys`, `settings` (five editable values + `hostCount`/`trustedHostKeyCount`/`activeRun`), `revision`, `status` (`commandCount`, `commandsError`, `activeStage`, `activeInvocationId`, `lastRunState`, `lastRunSummary`, `transportAvailable`, `backgroundAvailable`), `saveError` (rejected only). Rejected input returns a **failed** command with the unchanged settings in the payload and writes nothing. This is the only settings write path: it calls the same validated `PersistSettingsAsync` the module settings interface uses. |
| `remote-commands-android.run` | `commandId`, `host?`, `input1`, `input2`, `secondInput` | `state` (`succeeded`/`failed`/`cancelled`/`host-key-required`), `message`, `exitCode`, `host`, `alias`, `resolvedHost`, `output`, `transport` (`managed-ssh`/`local`), `pendingHostKey` (`host`, `hostKeyName`, `keyLength`, `fingerprint`) |
| `remote-commands-android.cancel` | `invocationId?` | `cancelled`, `activeInvocationId`, `stage` |
| `remote-commands-android.hosts.list` | – | `hosts[]` (`alias`, `host`, `port`, `username`, `auth`, `credentialConfigured`), `missingAliases[]`, `catalogPath`, `loadFailed` |
| `remote-commands-android.host.add` | `alias`, `hostName`, `port?`, `username`, `auth` (`password`/`privatekey`), `password?`, `privateKey?`, `passphrase?` | same as `hosts.list` |
| `remote-commands-android.host.remove` | `alias` | same as `hosts.list` |
| `remote-commands-android.host-key.status` | – | `keys[]` (`host`, `port`, `hostKeyName`, `fingerprint`, `addedAt`), `storePath` |
| `remote-commands-android.host-key.accept` | `hostName`/`host`, `port?`, `fingerprint`, `hostKeyName?` | same as `host-key.status` |
| `remote-commands-android.host-key.revoke` | `hostName`/`host`, `port?` | same as `host-key.status` |
| `remote-commands-android.history.summary` | – | `count`, `latestTimestamp`, `latestLabel`, `latestHost`, `storePath` |
| `remote-commands-android.history.clear` | – | `count` |
| `remote-commands-android.transform` | `tool`, `input1` | `tool`, `output`, `transport` |

Module events (via `SubscribeEventsAsync`): `run.started`, `run.stage`, `command.output`
(batched, `lines[]`), `run.finished`, `host-key.pending`, `host-key.accepted`,
`host-key.revoked`, `host.updated`, `host.removed`, `catalog.saved`, `history.cleared`,
`module.running`. `catalog.saved` carries `commandCount`/`contentLength` only, never the document
body.

Module settings (`GetSettingsSchemaAsync`/`GetSettingsAsync`/`ApplySettingsAsync`) and the
`settings.update` command share one validated write path:
`defaultHost`, `knownHosts`, `historyRetention` are written to the shared `settings.json`
(desktop property names, PascalCase); `condaExecutable`, `commandTimeoutMinutes` are written to the
Android-only `android-preferences.json` (camelCase); read-only `hostCount`, `trustedHostKeyCount`,
`activeRun` round-trip without being persisted. Secret values are rejected by the settings
interface and by the command on purpose, and out-of-range/invalid input is rejected instead of
being clamped or silently dropped.

Data directory: the module maps `<state>/modules/remote-commands-android/data` to
`<state>/tools/remote-commands-android` (`RemoteCommandsAndroidPaths`), so the surface's
`context.DataDirectory` and the module read/write the same `commands.yaml` / `settings.json` /
`history.json`.

## 3. Frozen surface contract

`package/ui/tool.json` points at:

- assembly: `surface/MyPowerTools.MobileRemoteCommands.dll`
- type: `MyPowerTools.MobileRemoteCommands.RemoteCommandsMobileSurfaceFactory`
  (implements `MyPowerTools.AvaloniaSdk.IMptAvaloniaSurfaceFactory`)

The surface must drive **only** the commands in §2. `tests/RemoteCommands.Android.Tests`
(`PackageManifestTests`) fails if the manifests stop naming that assembly/type.

## 4. Frozen security behaviour

- **No default trust.** A `run` connects only when the presented host key matches the fingerprint
  the user confirmed. First use and rotation both fail with `host-key-required` and the exact
  SHA256 fingerprint; `host-key.accept` stores only the fingerprint the caller passes, and a
  changed key never overwrites the trusted one (revoke first).
- **Aliases are never guessed.** Android has no `~/.ssh/config`, so `android-hosts.json` maps the
  `commands.yaml` alias to a real host/port/user. Without a mapping the run fails with the fields
  the user has to fill in.
- **Credentials only in `secret.store`** (`secret://remote-commands-android/host.<alias>.<field>`).
  Passwords, private keys and passphrases never reach the module JSON files, command results,
  status payloads or events; tests scan the whole data directory for the exact secret strings.
- **User-initiated only.** Nothing connects on initialize/enable/start/settings change. A run holds
  a `background.activity` lease while in flight (released in `finally`), and an explicit
  `cancel` / module disable kills the remote command, drops the connection and disposes the
  session.
- **No extra hashing.** Host key trust stores SSH.NET's own SHA256 fingerprint; no bespoke hash
  mechanism was added.

## 5. Build integration checklist (host/root owner)

1. `MyPowerTools.Android.slnx`: add
   `tools/remote-commands/android-integration/src/RemoteCommands.Android/RemoteCommands.Android.csproj`
   and `src/MyPowerTools.MobileRemoteCommands/MyPowerTools.MobileRemoteCommands.csproj`.
2. `src/MyPowerTools.Android/MyPowerTools.Android.csproj`: add the two `ProjectReference`s and an
   `AndroidAsset` glob for the staged package. **Do not exclude `MyPowerTools.*`** — the tool surface
   itself is `ui/surface/MyPowerTools.MobileRemoteCommands.dll` and a wildcard would drop it. Exclude
   only the two host-provided contract assemblies, Avalonia and the metadata JSONs that the
   `modules/**` whitelist already brings in:
   ```xml
   <AndroidAsset Include="../../tools/remote-commands/android-integration/artifacts/package/**/*"
                 Exclude="../../tools/remote-commands/android-integration/artifacts/package/**/*.pdb;../../tools/remote-commands/android-integration/artifacts/package/MyPowerTools.Abstractions.dll;../../tools/remote-commands/android-integration/artifacts/package/MyPowerTools.Platform.Abstractions.dll;../../tools/remote-commands/android-integration/artifacts/package/Avalonia*.dll;../../tools/remote-commands/android-integration/artifacts/package/module.json;../../tools/remote-commands/android-integration/artifacts/package/commands.index.json;../../tools/remote-commands/android-integration/artifacts/package/ui/*.json">
     <Link>modules/remote-commands-android/%(RecursiveDir)%(Filename)%(Extension)</Link>
   </AndroidAsset>
   ```
   Expected staged package content (the `<Link>` puts it under `modules/remote-commands-android/`):
   `RemoteCommands.Android.dll`, `Renci.SshNet.dll`, `BouncyCastle.Cryptography.dll`,
   `Microsoft.Extensions.Logging.Abstractions.dll`,
   `Microsoft.Extensions.DependencyInjection.Abstractions.dll`, `ui/surface/MyPowerTools.MobileRemoteCommands.dll`.
   The `ui/*.json` exclude is shallow on purpose so `ui/surface/` survives.
3. `scripts/build-android.ps1`: run
   `pwsh tools/remote-commands/android-integration/build.ps1 -MyPowerToolsRepoRoot <repo>`
   before the solution build (same step as `tools/file-transfer/build.ps1`).
4. Module mirror: `build.ps1` writes `modules/remote-commands-android/` (git-ignored, like
   `modules/file-transfer`). No repo-root `artifacts/` path is introduced, so
   `scripts/artifacts-policy.json` needs no new entry for the module package itself.
5. NuGet: `SSH.NET 2025.1.0` is a new external dependency of the Android build (restored from
   nuget.org; its `BouncyCastle.Cryptography 2.6.2` dependency matches the version the desktop
   product already pins). If the build environment has no nuget.org access, mirror the package into
   the repo-local feed `artifacts/sdk/nuget` or pass an alternative source.
6. Desktop parity (optional, root decision): the desktop `android-tools.remote-commands` module is
   untouched; the Android module declares its own id so both can coexist in the bundled catalog.

## 6. Verification commands

```bash
# restore (nuget.org is the canonical source; the workspace-local mirror works offline)
dotnet restore tools/remote-commands/android-integration/src/RemoteCommands.Android/RemoteCommands.Android.csproj
dotnet restore tools/remote-commands/android-integration/tests/RemoteCommands.Android.Tests/RemoteCommands.Android.Tests.csproj

# module build + tests (44 tests, all with FakeSshTransport: no socket, no server)
dotnet test tools/remote-commands/android-integration/tests/RemoteCommands.Android.Tests/RemoteCommands.Android.Tests.csproj -c Debug

# package staging
pwsh tools/remote-commands/android-integration/build.ps1 -Configuration Release
# backend-only staging (surface not built yet)
pwsh tools/remote-commands/android-integration/build.ps1 -SkipSurface -NoMirror -Configuration Debug
```

## 7. Not verified here

- **No real SSH was exercised.** Every test uses `FakeSshTransport`; the production
  `SshNetTransport` only compiles against SSH.NET 2025.1.0 in this build. A device test against a
  real server (with real host keys) is still required before this can be called accepted.
- No Android device/emulator install of the module package, and no end-to-end run through the
  Shell surface (the surface is a separate deliverable).
- `Renci.SshNet.dll` staged from this environment came from a package mirror, not from a verified
  nuget.org download; the canonical CI restore is nuget.org.
