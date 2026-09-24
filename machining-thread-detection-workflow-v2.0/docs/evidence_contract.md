# Evidence contract

每个检测结果至少记录：

- source model full path and SHA256
- SolidWorks/process ownership state
- probe/runtime version
- feature or Hole Wizard source
- physical opening/cylinder signature
- axis, diameter, depth and units where available
- semantic group and attribution evidence
- unresolved/blocker reason
- output paths and validation status

`None`、空数组和缺失字段都不能自动解释为“没有孔”或“检测成功”。
