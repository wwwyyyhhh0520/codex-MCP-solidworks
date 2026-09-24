# SolidWorks MCP server

This is a thin MCP stdio facade around the existing repository workers. It
does not implement SolidWorks geometry or annotation logic.

Run with the repository virtual environment:

```powershell
.\.mcp-venv\Scripts\python.exe .\solidworks_mcp_server\server.py
```

The Agent registration is kept outside the repository in the user Codex
configuration. The server exposes `get_capabilities`, `inspect_model`,
`plan_drawing`, and `create_drawing`.
