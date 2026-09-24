# Current project integration

## Python

建议在交付机器建立独立虚拟环境，并按 `current-project/solidworks_mcp_server/requirements.txt` 或各模块自身说明安装依赖。离线语义、计划和审图工具可以先运行；原生 SolidWorks 阶段需要 Windows、已安装的 SolidWorks Interop 和一个可确认归属的独占 SolidWorks 会话。

## C#

- `current-project/general_semantic_extractor/general_semantic_extractor.csproj`
- `current-project/policy_drawing_runtime/policy_drawing_runtime.csproj`

使用 .NET SDK 10.x 从源码构建。构建产物留在交付机器的 `bin/obj`，不要把它们当作源码包的一部分。

## MCP

`mcp/current-solidworks-mcp/` 保存当前 MCP 实现的源码快照；服务器注册、环境变量、SolidWorks 连接和权限由部署机器配置。离线工具契约位于 `skills/machining-part-design-workflow/mcp/`。

## 路径迁移

包内不依赖原工作区的绝对路径。运行时输入（模型、模板、语义工件、计划和输出目录）应通过 CLI 或配置显式传入，并在日志中写入来源路径与哈希。
