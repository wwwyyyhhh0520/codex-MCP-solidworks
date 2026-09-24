# Installation and operation

1. 安装 Python 与项目所需依赖。
2. 安装并注册目标 SolidWorks Interop 类型库。
3. 将 `src/sw_worker` 加入任务工作目录，所有输入模型使用绝对路径并记录 SHA256。
4. 先执行离线配置和路径检查，再执行一次性 native probe。
5. 运行结束后保存 stdout、stderr、进程生命周期和 JSON 结果。
6. 对 `PASS`、`REVIEW`、`BLOCKED`、`UNRESOLVED` 分开归档。

MCP 插件配置在 `mcp/sw-thread-audit-local/`；其中的 `.mcp.json` 需要按部署机器调整路径，不要把当前用户目录直接当作通用路径。
