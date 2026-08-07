# Remote Commands

Remote Commands is an Avalonia `dotnet-surface` for MyPowerTools. It replaces the
fixed PyQt `page3.py` workspace with a versioned command catalog, dynamic input
forms, SSH and local-process execution, built-in text transforms, and persisted
execution history.

## Repository layout

- `original-source/powertool/` preserves the PyQt implementation and its original
  command catalog.
- `current-integration/src/RemoteCommands.Surface/` contains the active Avalonia
  Surface.
- `current-integration/modules/android-tools-suite/` contains the tool manifests
  used by the shared Android Tools suite package.
- `build.ps1` builds and packs the Surface. The shared suite runtime remains owned
  by the `remote-notifications` submodule and receives this Surface during the
  two-phase MyPowerTools tool build.

## Command catalog

The user catalog is stored as `commands.yaml` in the tool data directory. Schema 2
keeps tool additions declarative. A new SSH or local tool normally requires only a
catalog entry; the Avalonia form is generated from `inputs`.

```yaml
schema: 2
defaults:
  timeout_seconds: 300

commands:
  - id: analyze_trace
    label: Analyze trace
    group: Analysis
    description: Run the analyzer on the configured SSH destination.
    runner: ssh
    command: /opt/tools/analyze.py
    arguments:
      - --trace
      - "{{input:trace:file}}"
      - --mode
      - "{{input:mode:text}}"
    inputs:
      - id: trace
        label: Trace
        kind: multiline
        placeholder: Paste the trace
        required: true
      - id: mode
        label: Mode
        kind: text
        default: summary
        required: true
    tags: [trace, performance]
```

### Runners

| Runner | Behavior |
|---|---|
| `ssh` | Creates a unique remote temporary directory, uploads only inputs referenced with `:file`, runs through OpenSSH, streams output, enforces a timeout, and performs bounded cleanup. |
| `local` | Starts a local executable with `ProcessStartInfo.ArgumentList`. A catalog-only shell command is also allowed when `arguments` is omitted and no input placeholders are present. This supports external Python, PowerShell, Rust, or native tools without a C# change. |
| `transform` | Invokes one of the bundled C# text transforms retained from `command_tools.py`. |

`{{input:id:text}}` inserts the corresponding value. Shell runners quote the
value. `{{input:id:file}}` writes the value to a temporary file and inserts its
local or remote path.

A command can also declare:

- `host` to lock one command to a specific SSH destination
- `defaults.host` to provide a catalog-level fallback; a user-entered session host takes precedence
- `timeout_seconds` from 1 through 86400
- `working_directory`
- `environment`
- `group` and `tags` for catalog search
- any number of `text` or `multiline` inputs

Catalog validation rejects duplicate identifiers, unsupported runners, invalid
input references, malformed placeholders, and unknown built-in transforms.

## Legacy catalogs

The original entries continue to load:

```yaml
commands:
  - label: Legacy analyzer
    command: /opt/tools/analyze.py
    type: shell
    host: r743
```

Legacy `shell` maps to `ssh` and retains the original `--file1` and `--file2`
contract. Legacy `py` maps to a bundled transform. External scripts should use
`runner: local` or `runner: ssh` so adding them does not require application code.

## Persistence and execution safety

Settings, history, and catalog writes use same-directory atomic replacement.
Corrupt settings or history files are preserved with a `.corrupt-*` suffix before
defaults are loaded. History records command IDs, all declared input values, exit
status, duration, host, and output, while retaining the first-port JSON fields for
migration.

SSH destinations are restricted to an SSH alias, host name, `user@host`, or a
bracketed IPv6 address. OpenSSH runs in batch mode with a connection timeout. A
nonzero process exit is recorded as a failed run.
