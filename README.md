# codex-MCP-solidworks

SolidWorks 自动化与工程设计工具包。

## 包含内容

- `machining-part-design-workflow-v2.0/`：语义提取、工程图规划、SolidWorks 工程图运行时、工程图 MCP、PDF 与原生尺寸验证源码。
- `machining-thread-detection-workflow-v2.0/`：Hole Wizard 与螺纹参数检测、孔几何归属、螺纹问题定位、螺纹检测 MCP、PowerShell/Python 探针。

同时提供两个对应 ZIP 发布包。

## 重要说明

本仓库按项目授权保留公司内容，不做脱敏。源码包不包含 `bin`、`obj`、`__pycache__`、SolidWorks 安装程序或构建二进制；使用前需要在目标机器安装 Windows、SolidWorks、.NET SDK、Python 依赖和对应 Interop 环境。

详细目录、安装方式、证据边界和运行约束见两个子包中的 `README.md` 与 `docs/`。
