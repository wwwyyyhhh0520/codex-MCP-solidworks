# machining-part-design-workflow 2.0

这是可复用工程包。它把 v1.0 的工作流 Skill、提示词、模板和 MCP 契约，与当前项目中的语义提取、工程图规划、SolidWorks 运行时、验证器和 MCP 实现放在同一个可审计目录中。

## 目录

- `skills/machining-part-design-workflow/`：Skill、工作流、提示词、模板和 v1 文档基线。
- `mcp/`：当前 MCP 服务器副本与工具契约。
- `current-project/src/`：语义分析、规划、决策、验证和桥接源码。
- `current-project/general_semantic_extractor/`：强类型语义提取器源码。
- `current-project/general_drawing_planner/`：工程图规划与策略检查源码。
- `current-project/policy_drawing_runtime/`：当前受控 SolidWorks 工程图运行时源码。
- `current-project/experimental/`：当前 DP90 PDF 交付执行器源码快照。
- `current-project/config/`、`docs/`、`tools/`：模板、客户策略、规则、宏和运维材料。

## 推荐执行顺序

1. 读取 Skill 与 `skills/machining-part-design-workflow/workflow/workflow_definition.yaml`。
2. 用 `current-project/src/auto_drawing/pipeline_runner.py` 做需求、语义和验证预检。
3. 用 `general_semantic_extractor` 生成带来源的语义工件。
4. 用 `general_drawing_planner` 生成工程图计划，并先通过质量/契约检查。
5. 只在零 SolidWorks 进程、模板和模型路径均已核验时，显式调用 `policy_drawing_runtime`。
6. 将 PDF、原生尺寸 SystemValue、保存/重开和验证日志作为独立证据归档。

## 构建

C# 项目要求安装 .NET SDK 10.x 和本机 SolidWorks Interop 类型库；Python 工具的依赖见各自 `requirements.txt`。本包不携带 `bin`、`obj`、Python 缓存或临时运行输出，交付机器应从源码重新构建。

## 证据边界

PDF 存在只证明文件生成；原生尺寸、孔标注、保存重开和制造可发布性必须分别以对应验证记录证明。没有真实几何绑定的尺寸或孔标注保持未解决，不用截图或历史值补齐。

