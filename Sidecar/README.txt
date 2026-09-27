StationGod MCP sidecar
======================

The sidecar is the MCP server your AI client starts. It talks to the game mod
through the StationGodMCP named pipe (local game) or over TCP (dedicated server).

Choose one archive:

  StationGodMCP.Server-win-x64.zip
    Self-contained Windows executable. No .NET installation is required.
    Configure the MCP client to run StationGodMCP.Server.exe.

  StationGodMCP.Server-portable.zip
    Portable DLL that needs the .NET 8 runtime.
    Configure the MCP client to run:
      dotnet StationGodMCP.Server.dll

Extract it to a stable folder outside every Stationeers mod and Workshop
folder, for example %LOCALAPPDATA%\StationGodMCP\server. Extracted inside a mod
folder, its DLLs would be picked up as game plugins.

A local game is found automatically while it hosts a loaded save.

For a remote dedicated server, add:

  --host SERVER_ADDRESS --port 8765

and give the shared secret in the STATIONGODMCP_SECRET environment variable.

The self-contained executable is unsigned and may show a Windows SmartScreen
"unrecognized app" prompt. The portable DLL runs through the Microsoft-signed
dotnet host.

Full setup for Claude Code, Codex and dedicated servers: the mod's README.
