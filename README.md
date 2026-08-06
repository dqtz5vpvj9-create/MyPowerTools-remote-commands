# Remote Commands

This repository ports the original Remote Commands page (`powertool/page3.py`)
into MyPowerTools as an active dotnet-surface tool.

- `original-source/powertool/page3.py` is the original UI entry.
- `original-source/powertool/command_tools.py` and `commands.yaml` contain the
  command workflow and text transforms.
- `current-integration/src/RemoteCommands.Surface/` contains the Avalonia Surface
  that reproduces the page3 workspace: command catalog, host/inputs/output,
  SSH execution, text-transform tools, history, settings, and commands.yaml
  editing.
- `current-integration/modules/android-tools-suite/` records the shared
  AndroidTools suite module definitions, with the Remote Commands tool routed to
  the Surface.
- `build.ps1` builds and packs the Surface. The shared android-tools-suite
  runtime package is still produced by the `remote-notifications` submodule;
  `scripts/build-all-tools.ps1` stages both Surfaces into the same suite package
  in registry order.
- `tool-release.json` declares the active state and package owner.
