# Paused: Revit agent bridge

Work stopped before this compiled or was tested. No pull request.

Branch: `cursor/revit-agent-bridge-5039`

## Done

- Existing add-in reviewed: shared C# under `FloorPatternFlattener/src/FloorPatternFlattener.Core`, per-year projects 2023–2027, ribbon, flatten commands, and `Editability` for worksharing.
- Command list drafted in `FloorPatternFlattener/agent/commands.json` (model info, query, parameters, select, create wall/door/level/grid/room/tag, delete with dry-run default, flatten, plus ping and list types). Save, sync, reload, relinquish, close, and undo are forbidden there.
- Protocol code (no Revit types) started under `FloorPatternFlattener/src/FloorPatternFlattener.Core/Bridge/Protocol/`: small JSON parser, argument checks, session-file shape, token check, localhost HTTP listener skeleton (`127.0.0.1` only).
- Revit-side sketches started under `Bridge/Execution/`: element ids per Revit year, worksharing borrow/report, named transactions that swallow warnings, length units, and part of the command handler (ping, model info, query, list types, get parameters, select).

## Left

- Finish the command handler: category/level/element helpers, set parameter, create wall/door/level/grid/room/tag, delete (dry-run), and flatten via the existing floor-pattern service. `CommandExecutor.cs` still calls helpers that are not written, so it will not compile.
- Ribbon toggle (off by default), show/copy the session token, write `%USERPROFILE%\.revit-agent\bridge.json`, run commands on Revit’s thread with `ExternalEvent`, stop the listener on shutdown. Never save, sync, or close.
- Point each year’s project at the Nice3point Revit API NuGet packages (so it builds without Revit installed), embed `commands.json`, and keep `net48` / `net8.0-windows` / `net10.0-windows`.
- Python MCP server and `revit-cmd` CLI (Python and PowerShell) that read the session file and call `127.0.0.1`.
- Unit tests for JSON, validation, the HTTP bridge, and the MCP server.
- Plain-language README and install steps, plus a CI zip of the add-in per Revit year and the MCP/CLI files.
- Compile all five year projects, run tests, then open the pull request.

## Not verified

Revit is not available here. Nothing has been compiled or run.
