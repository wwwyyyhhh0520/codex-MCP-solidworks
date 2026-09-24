# SolidWorks 自动出工程图系统：第一阶段设计

## 1. 项目目标

系统以 `.SLDPRT` 为只读事实源，面向规则型单实体机械加工零件，生成可审查的工程图计划，并在后续阶段通过 SolidWorks API 创建 `.SLDDRW`，按需输出 PDF/DWG。第一阶段不修改零件模型，不虚构尺寸、公差、基准、材料、粗糙度或处理要求。

核心解耦链路：

```text
SLDPRT -> Reader -> Feature/Geometry Recognizer -> PartAnalysis
                                      |
                                      v
                               Rule Engine -> DrawingPlan
                                      |
                                      v
                         SolidWorks Executor -> SLDDRW/PDF/DWG
                                      |
                                      v
                                Reviewer -> report
```

## 2. 可确定性边界

### 可从模型直接确定

- 文件、配置、单位、实体数量、包围盒、质量属性；
- 自定义属性中的图号、名称、材料、版本等；
- Feature Tree 中的拉伸、旋转、切除、圆角、倒角、阵列；
- Hole Wizard 的孔径、类型、深度、螺纹规格、数量和阵列关系；
- 可由拓扑和几何测量得到的长度、直径、角度、圆角半径、槽宽；
- PMI/DimXpert 中已有的尺寸、公差、GD&T、粗糙度和基准。

### 可按明确规则推导，但必须带理由与置信度

- 零件类别：轴类、板类、CNC 铣削块类；
- 主视方向候选及其评分；
- 必要投影视图、剖视图、局部放大图；
- 基准候选的排序；
- 尺寸分组、视图归属和排版位置；
- 标准图幅和标准比例。

### 不得自动猜测

- 未写入模型或项目规则的功能意图；
- 尺寸公差、配合、公差带、GD&T、粗糙度；
- 材料牌号、热处理、表面处理；
- 功能基准 A/B/C；
- 普通切除是否“设计意图上”是螺纹孔、标准孔或配合孔。

这些内容进入 `uncertain_items`，必要时以 `needs_human_confirmation` 阻止完成判定。

## 3. 模块职责

| 模块 | 职责 | 第一阶段状态 |
|---|---|---|
| `SolidWorksPartReader` | 只读打开、重建检查、读取属性/Feature/拓扑 | 接口 |
| `FeatureRecognizer` | 识别孔、槽、轴、板、圆角、倒角、腔体等 | 接口与规则 |
| `PartAnalyzer` | 汇总事实、候选方向、基准候选、风险 | 数据结构 |
| `DrawingPlanner` | 按规则选择视图、尺寸、标注和布局约束 | 数据结构与接口 |
| `SolidWorksDrawingExecutor` | 将已批准计划映射到 API 原子操作 | 接口，暂不连接 SW |
| `DrawingReviewer` | 检查完整性、重复、越界、遗漏和不确定项 | 接口与规则 |
| `OutputExporter` | 保存 SLDDRW，导出 PDF/DWG，检查返回值 | 接口 |

## 4. 规则化决策

- 主视方向：按可见关键特征数、隐藏线惩罚、基准可标注性、典型加工姿态评分；轴类优先轴线水平，板类优先最大主要平面。分数接近时保留候选并人工确认。
- 投影视图：只有在暴露新结构、减少隐藏线或完成尺寸定义时才增加；不机械生成六视图。
- 剖视图：内部孔/腔/螺纹/内槽导致隐藏线复杂时优先；剖切路径必须命中目标特征。
- 局部视图：小槽、细小倒角/圆角、特殊孔口或标注拥挤时使用，并使用标准放大比例。
- 尺寸：功能/定位/孔槽台阶/总体/几何补全依次处理；检测到闭合尺寸链时保留受控尺寸并将冗余项设为参考或报告错误。
- 孔标注：优先使用 Hole Wizard/Feature 参数；普通几何孔无法证明规格时只输出可测几何事实并标记不确定。
- 公差/GD&T/粗糙度：只复制模型 PMI、属性或明确项目规则，不由经验自动补齐。
- 排版：先调整标注位置和视图间距，再更换标准比例或图幅，禁止缩小到不可读。

## 5. 执行安全边界

执行器只接受经过 schema 校验的 `DrawingPlan`，并提供 `CreateDrawing`、`CreateBaseView`、`CreateProjectedView`、`CreateSectionView`、`CreateDetailView`、`AddDimension`、`AddHoleCallout`、`AddDatum`、`AddGeometricTolerance`、`AddSurfaceFinish`、`SaveDrawing`、`ExportPDF`、`ExportDWG` 等受限动作。所有 API 返回值、文档状态和异常必须写入 `execution_log.md`。

## 6. 第一阶段验收

本阶段验收对象是：规则和边界可追踪，`part_analysis` 与 `drawing_plan` 可序列化、可校验，执行层与审图层接口明确；不以生成真实 SolidWorks 工程图为验收条件。
