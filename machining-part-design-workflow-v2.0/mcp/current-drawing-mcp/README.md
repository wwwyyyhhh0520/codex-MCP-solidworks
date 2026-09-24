# Current drawing MCP

这是当前工程图/语义分析侧的 MCP 服务源码副本，来源于 `current-project/solidworks_mcp_server/`。

- 服务入口：`server.py`
- 依赖：`requirements.txt`（如果部署版本提供）
- 工程图与语义工具契约：`../skills/machining-part-design-workflow/mcp/`

部署到另一台机器时，需要重新配置本机路径、Python 环境、SolidWorks 连接方式和 MCP 注册信息；不要直接沿用原工作区绝对路径。
