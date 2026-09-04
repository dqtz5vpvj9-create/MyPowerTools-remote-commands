# Remote Commands

This repository ports the original `powertool/page3.py` workspace into MyPowerTools as an active Avalonia dotnet-surface tool.

## User workflow

- Select a command from the command list.
- Select a saved SSH host. Hosts are added once under **Hosts & settings**, then reused from a drop-down selector.
- Paste the command input. Command definitions can provide meaningful labels and placeholders, so users do not have to infer what “Input 1” means.
- Run the command and inspect or copy its output.
- Double-click a history item to restore its command, host, inputs, second-input state, and output.

Local text transforms hide the SSH selector because they do not contact a remote host. A command with a fixed `host` displays that host and disables the selector for that command.

## Adding commands

Commands remain data-driven in the tool data directory's `commands.yaml`. Existing entries containing `id`, `label`, `command`, `description`, `type`, and optional `host` remain compatible.

The Surface also recognizes these optional user-facing fields:

```yaml
commands:
  - id: analyze_trace
    label: Analyze trace
    command: /opt/tools/analyze_trace.py
    description: Analyze a trace on the selected SSH host.
    type: shell
    input1_label: Trace data
    input1_placeholder: Paste the trace to analyze.
    input2_label: Optional configuration
    input2_placeholder: Paste an optional configuration file.
    show_second_input: false
```

`type` accepts `shell` for SSH execution and `py` for one of the C# text-transform mappings. Command IDs must be unique. The in-app editor validates the command list before saving.

## Repository layout

- `original-source/powertool/page3.py` preserves the original PyQt implementation.
- `original-source/powertool/command_tools.py` and `commands.yaml` preserve the original command workflow and transforms.
- `current-integration/src/RemoteCommands.Surface/` contains the Avalonia Surface, persistence, command parser, SSH executor, and text transforms.
- `current-integration/modules/android-tools-suite/` contains the shared suite module definitions.
- `build.ps1` builds and packs the Surface. The shared `android-tools-suite` runtime package remains owned by the `remote-notifications` submodule.
