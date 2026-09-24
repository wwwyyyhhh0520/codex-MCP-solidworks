# machining-thread-detection-workflow 2.0

这是螺纹/孔语义检测交付包，独立于工程图尺寸与 PDF 生成包。它整理了现有 SolidWorks 几何探针、Hole Wizard 参数读取、螺纹孔归属分析、主孔/伙伴孔判断、覆盖审计、比较报告和 MCP 入口。

## 能力边界

- 从当前零件或装配读取 Hole Wizard / thread 参数与几何证据。
- 以圆柱轴线、深度、拓扑和特征历史建立孔的归属证据。
- 区分直接孔、伙伴孔、镜像/派生孔和无法证明归属的孔。
- 生成可追溯的 JSON、审计报告、覆盖报告和比较表。
- 通过本地 MCP 暴露探针入口。

## 推荐入口

- `src/sw_worker/run_thread_audit_v4.py`：主审计编排。
- `src/sw_worker/run_thread_audit_v4_deliverable.py`：交付形态编排。
- `src/sw_worker/mcp_thread_audit_server.py`：MCP 服务。
- `src/sw_worker/v4_probe_external_partner_fastener_evidence.py`：伙伴紧固件证据探针。
- `src/sw_worker/v4_locate_thread_issue_in_sw.py`：螺纹问题定位。
- `src/sw_worker/v4_check_thread_holes_against_checklist.py`：检查表核验。
- `src/sw_worker/probe_hole_wizard_thread_parameters_earlybound_v4.ps1`：强类型 Hole Wizard 参数探针。

## 证据规则

只接受来自 SolidWorks API、特征历史、几何签名和明确来源工件的证据。不能证明孔的工程归属时，结果保持 `UNRESOLVED` 或 `REVIEW`；禁止用孔名、近邻距离、截图或复制的历史尺寸补齐结论。

## 环境

需要 Windows、已安装的 SolidWorks、可用的 Python 环境和对应 COM/Interop 权限。运行前必须确认没有不属于本次任务的 SolidWorks 进程；本包不会自动关闭未知进程。

## 输出

建议每个任务使用独立目录保存：输入路径、模型 SHA256、探针版本、原始 stdout/stderr、语义 JSON、验证 JSON、审计报告和运行时间。不要覆盖历史证据。

