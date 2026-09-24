from pathlib import Path
import hashlib,json
r=Path(__file__).resolve().parent
s=r.parent/'general_annotation_executor'/'Program.cs'
t=s.read_text(encoding='utf-8-sig');h=t[t.index('static void ExecuteCategory('):]
needle='    SelectSingle(model, entity, definition.AnnotationId);\n    var note = model.InsertNote(FormatHoleSemantics(geometry.Hole)) as Note;'
assert h.count(needle)==1
h=h.replace(needle,'    if (definition.Creation.TryGetProperty("native_only", out var n) && n.GetBoolean()) throw new InvalidOperationException("NATIVE_ONLY_CALLOUT_FAILED:"+nativeFailure);\n'+needle)
(r/'Program.cs').write_text((r/'Runner.cs.txt').read_text(encoding='utf-8-sig')+'\n'+h,encoding='utf-8')
(r/'policy_drawing_runtime.csproj').write_text((s.parent/'general_annotation_executor.csproj').read_text(),encoding='utf-8')
(r/'source_provenance.json').write_text(json.dumps({'source':str(s),'sha256':hashlib.sha256(s.read_bytes()).hexdigest(),'adaptation':'native_only fallback guard; policy runner invokes existing helpers'},indent=2))
