# codex-MCP-solidworks

SolidWorks 自动化与 AI Agent 工程设计工具包。

本仓库记录并交付一套 **Agent + Skill + MCP + SolidWorks API** 的机械设计自动化方案。

目标不是单纯编写 CAD 脚本，而是将机械工程知识、语义分析、工程图规划和 CAD 原生执行流程沉淀为可复用工程能力。

---

# 项目架构

整体流程：

```
User Requirement
        ↓
Agent
        ↓
Skill Workflow
        ↓
MCP Tool Layer
        ↓
SolidWorks Runtime
        ↓
CAD / Drawing / PDF Delivery
```

## Agent

负责：

- 理解工程任务
- 调度工作流
- 调用工具
- 汇总结果

## Skill

负责封装工程知识：

- 流程定义
- 输入模板
- Prompt规则
- 输出格式
- 人工确认节点

## MCP

负责 Agent 与 SolidWorks 工具之间的连接。

MCP 不包含工程判断逻辑，而提供标准化工具调用接口。

---

# Repository Contents

## 1. machining-part-design-workflow-v2.0

机加件数字设计工作流。

包含：

- Semantic Extraction
- Engineering Facts
- Drawing Planning
- SolidWorks Drawing Runtime
- MCP Integration
- PDF Delivery
- Native Dimension Validation

核心能力：

```
3D Model
 ↓
Semantic Facts
 ↓
Drawing Plan
 ↓
Native Annotation Creation
 ↓
Validation
```

---

## 2. machining-thread-detection-workflow-v2.0

螺纹与孔特征检测工作流。

包含：

- Hole Wizard 检测
- Thread Parameter Analysis
- Hole Geometry Mapping
- Issue Localization
- Detection MCP
- PowerShell/Python Probe

---

# Engineering Design Approach

## Semantic Layer

系统不直接依赖简单几何枚举，而建立工程语义层：

- SemanticExecutionFacts
- HoleSemantics
- PhysicalOpenings
- FrameEvidence
- DatumEvidence

目标：

让 AI 根据工程事实执行，而不是根据几何猜测。

---

# Drawing Generation Pipeline

架构演进：

```
Inline Implementation
        ↓
Annotation Router
        ↓
Execution Context
        ↓
Reusable Annotation Pipeline
```

关键组件：

- AnnotationExecutionRequest
- ResolvedAnnotationExecutionContext
- OverallExecutionContext
- Probe / Commit Lifecycle

设计原则：

- 单一算法来源
- 不复制候选搜索逻辑
- 保留原生验证流程
- Fail Closed

---

# Skill Package

可复用 Skill：

```
machining-part-design-workflow

├── SKILL.md
├── workflow
├── templates
├── prompts
├── examples
├── docs
└── mcp
```

用途：

其他工程团队可以基于该 Skill：

1. 导入工作流
2. 配置 MCP 工具
3. 输入 CAD 与需求
4. 执行自动化流程
5. 输出工程交付包

---

# Delivery Package

最终交付包括：

- Workflow 文档
- Skill Package
- MCP 接口说明
- CAD 自动化源码
- 工程模板
- 验证日志
- PDF 输出流程

---

# Known Engineering Issues

当前验证过程中记录：

## SolidWorks Native COM Call

部分 Native API 调用存在同步 COM 生命周期风险。

例如：

```
AddHoleCallout2
CALL_ENTER = YES
CALL_RETURN = NO
```

处理策略：

- 保留诊断日志
- Fail Closed
- 避免生成错误工程结果

---

# Version

Current delivery baseline:

```
v2.0
```

项目状态：

- Architecture: Completed
- Workflow: Completed
- Skill Package: Completed
- Documentation: Completed
- Native runtime edge cases: Documented
