# 人工元数据接口

当 SLDPRT 几何、Feature、PMI 或 SolidWorks API 不能可靠给出设计意图时，允许使用人工元数据补充。

## 原则

- 每条人工元数据必须有 `source`。
- `source` 必须指向人工确认、客户图纸、标准件表、PLM、ERP、检验规范或其他可追溯资料。
- 不接受 `feature_name_text`、`filename`、`visual_guess` 作为孔、螺纹、公差、粗糙度或基准的来源。
- 人工元数据只能补充缺失设计意图，不得覆盖模型已可靠读取的几何事实，除非显式声明变更原因。

## 当前 MVP 支持字段

- `hole_callouts`
- `material`
- `general_tolerances`
- `surface_finish`
- `datums`
- `notes`

## 孔标注最小要求

孔标注若要从 `blocked` 进入 `planned_from_human_metadata`，至少需要：

- `feature_name`
- `source`
- `parameters.diameter` 或 `parameters.thread_specification`
- 若为盲孔或螺纹孔，还需要可靠深度/有效深度来源

公差、螺距、螺纹等级若没有来源，必须保持空值。
