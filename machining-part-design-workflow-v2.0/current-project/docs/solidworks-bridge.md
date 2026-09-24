# SolidWorks 内部桥接方案

## 背景

当前外部自动化路径已经验证：

- pywin32 动态 COM 可以连接 SolidWorks，也能在第一个样件上通过 `OpenDoc` 回退读取 Feature Tree。
- pywin32 `OpenDoc6` 普通参数与 by-ref 参数都返回类型不匹配。
- PowerShell COM `[ref]` 调用 `OpenDoc6` 返回 `TYPE_E_ELEMENTNOTFOUND (0x8002802B)`。
- 第二样件在旧 `OpenDoc` 回退路径上会长时间不返回。

因此，继续把关键读取逻辑压在外部动态 COM 上，风险较高。下一层方案是让 SolidWorks 内部 VBA 宏或 .NET 插件调用同一套 API，再把结果以 JSON 写回 `working`。

## 目标

- 只读打开 `.SLDPRT`。
- 不保存、不修改原始模型。
- 读取 Feature Tree。
- 对 Hole Wizard 特征尝试读取定义对象和常见字段。
- 输出桥接诊断 JSON。

## MVP 路径

1. 在 SolidWorks 中新建宏。
2. 导入或复制 `tools/solidworks_macro_bridge.bas`。
3. 修改宏顶部的 `SOURCE_FILE` 与 `OUTPUT_FILE`。
4. 运行 `Main`。
5. 将输出 JSON 纳入 `hole_analysis` 或 `part_analysis` 的可靠来源候选。

## 数据边界

宏输出仍要经过审图器验证。

- `feature_type=HoleWzd` 是可靠事实。
- 成功读取的 Feature Definition 字段可作为可靠参数候选。
- 特征名文本只能作为弱线索。
- 宏中没有读到的公差、配合、GD&T、粗糙度、热处理、功能基准，不得自动生成。

## 后续方向

- VBA 宏仍可用于人工触发的内部桥接。
- 已新增 `tools/SolidWorksTypedBridge.cs`：独立 C# EXE，直接引用 `SolidWorks.Interop.sldworks.dll` 和 `SolidWorks.Interop.swconst.dll`，可强类型调用 `OpenDoc6`，并在第二样件上验证成功。其输出可由 `typed_bridge_importer` 进入 `PartAnalysis`。
- 强类型桥接已读取 Feature Tree、包围盒、实体、PMI、Hole Wizard 定义及每个 Hole Wizard 的独立圆柱面拓扑。`FastenerSize`、`ThreadDepth` 与 `EndCondition` 可作为 API 事实进入孔分析；螺距、配合、公差、GD&T、粗糙度和功能基准仍须有独立可靠来源。
- 批量回归时，每个零件必须仍由 `pipeline_runner` 控制超时与报告收敛。
