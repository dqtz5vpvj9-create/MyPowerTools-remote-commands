# Remote Commands

This repository preserves the complete Remote Commands source while the product
is paused in the current MyPowerTools delivery plan.

- `original-source/powertool/page3.py` is the original UI entry.
- `original-source/powertool/command_tools.py` and `commands.yaml` contain the
  command workflow.
- `current-integration/` records the current shared AndroidTools adapter state.
- `tool-release.json` declares the paused state and package owner.

The active shared adapter package is currently built by the
`remote-notifications` submodule. A future reactivation must first split the
shared `AndroidTools.MyPowerTools` assembly into an independent package.
