# 人工决策数据约定

`manual_decision_request.json` 的 `read_only_model_facts` 不可编辑。每个决策都必须填写 `value` 与 `source`，且 `source` 必须属于该决策列出的 `allowed_sources`。

主视确认：

```json
{"orientation": "front"}
```

孔公差确认：专用公差使用 `{"mode":"specific","tolerance":{"plus_mm":0.1,"minus_mm":0.1}}`；明确授权模板一般线性公差使用 `{"mode":"general_linear","explicitly_applied":true}`。此决定只作用于已有可靠孔几何，不能补出螺纹、配合或深度。

模板技术要求范围：不采用模板条文使用 `{"scope":"none"}`；采用明确条目使用 `{"scope":"named_requirements","requirement_ids":["REQ-001"]}`。第一阶段只记录范围，不自动复制条文。
