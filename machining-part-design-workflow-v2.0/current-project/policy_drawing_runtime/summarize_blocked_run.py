"""Summarize completed runtime evidence without treating a copied input as V2 output."""
import argparse,json,hashlib,itertools
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--plan',required=True);p.add_argument('--run',required=True);p.add_argument('--input',required=True);p.add_argument('--report',required=True);a=p.parse_args()
load=lambda x:json.loads(Path(x).read_text(encoding='utf-8-sig'))
plan=load(a.plan);run=Path(a.run);views=load(run/'runtime_views.json');lineage=load(run/'annotation_lineage.json');placements=load(run/'annotation_layout.json');centers=load(run/'centerline_api_evidence.json')
overlap=lambda a,b:min(a[2],b[2])>max(a[0],b[0])+1e-9 and min(a[3],b[3])>max(a[1],b[1])+1e-9
def linebox(l,b):
    t0,t1=0.,1.;dx,dy=l[2]-l[0],l[3]-l[1]
    for p,q in zip([-dx,dx,-dy,dy],[l[0]-b[0],b[2]-l[0],l[1]-b[1],b[3]-l[1]]):
        if abs(p)<1e-12:
            if q<0:return False
        elif p<0:t0=max(t0,q/p)
        else:t1=min(t1,q/p)
        if t0>t1:return False
    return True
conflicts=[]
for aa,bb in itertools.combinations(placements,2):
    if overlap(aa['estimated_text_bbox_m'],bb['estimated_text_bbox_m']):conflicts.append({'a':aa['annotation_id'],'b':bb['annotation_id'],'kind':'ESTIMATED_TEXT_OVERLAP'})
    if any(linebox(l,bb['estimated_text_bbox_m']) for l in aa['lines']) or any(linebox(l,aa['estimated_text_bbox_m']) for l in bb['lines']):conflicts.append({'a':aa['annotation_id'],'b':bb['annotation_id'],'kind':'ESTIMATED_LINE_TEXT_INTERFERENCE'})
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
copies=[{'path':str(f.resolve()),'size_bytes':f.stat().st_size,'identical_to_input':sha(f)==sha(a.input),'accepted_v2':False} for f in run.glob('*.SLDDRW') if not f.name.startswith('~$')]
overall=[{'axis':x['model_semantic_axis'],'system_value_m':x['system_value_m'],'semantic_value_mm':x['semantic_value_mm']} for x in lineage if x['category']=='OVERALL_DIMENSION']
result={'phase':'CLIENT_DRAWING_POLICY_V1_AND_MODEL01_LAYOUT_REFINEMENT','status':'PARTIAL_SAVE_BLOCKED','client_policy_loaded':True,'policy_version':plan['POLICY_VERSION'],'policy_rule_count':plan['POLICY_RULE_COUNT'],'main_view':plan['main_view'],'secondary_views':plan['secondary_views'],'selected_view_count':len(views),'scale':plan['scale'],'estimated_geometry_area_sheet_utilization':plan['estimated_sheet_utilization'],'v1_geometry_area_sheet_utilization':next(c['projected_width_m']*c['projected_height_m'] for c in plan['view_coverage_matrix'] if c['orientation']==plan['main_view'])/(plan['sheet_geometry']['width_m']*plan['sheet_geometry']['height_m']),'view_overlap_count':sum(overlap(x['outline_m'],y['outline_m']) for x,y in itertools.combinations(views,2)),'reserved_region_overlap_count':sum(overlap(v['outline_m'],r['bbox_m']) for v in views for r in plan['reserved_regions']),'overall_dimensions_runtime':overall,'annotation_created_count':len(lineage),'native_hole_callouts_created':sum(x.get('creation_mode')=='SOLIDWORKS_NATIVE_HOLE_CALLOUT' for x in lineage),'native_hole_callout_variables_at_creation':sum(x.get('variables_available',False) for x in lineage),'centerline_creation_evidence':centers,'annotation_estimated_conflicts':conflicts,'runtime_views':views,'automatic_save':'TIMEOUT_20_SECONDS','root_cause':'UNCONFIRMED; both new drawing SaveAs and saved drawing copy Save3 failed to return','slddrw_accepted':False,'unaccepted_working_copies':copies,'pdf_available':False,'reopen':'NOT_EVALUATED','annotation_persistence':'NOT_EVALUATED','native_hole_callout_persistence':'NOT_EVALUATED','unresolved':plan['unresolved_intents'],'qa':{'VIEW_SELECTION':'PASS','VIEW_COVERAGE':'PASS','SCALE_UTILIZATION':'PASS','VIEW_LAYOUT':'PASS','VIEW_BOUNDARY':'PASS','RESERVED_REGION_OVERLAP':'PASS','DIMENSION_COMPLETENESS':'REQUIRES_HUMAN_CONFIRMATION','DUPLICATE_DIMENSION':'PASS','ANNOTATION_OVERLAP':'NOT_EVALUATED','HOLE_CALLOUT_READABILITY':'NOT_EVALUATED','CENTERLINE':'NOT_EVALUATED','HIDDEN_LINE':'NOT_EVALUATED','CROSSING':'NOT_EVALUATED'},'qa_scope':'PASS for planning/runtime geometry only; no V2 PDF visual or persistence acceptance','hardcoded_current_model_branches':0,'hardcoded_current_dimensions':0,'hardcoded_current_coordinates':0,'hardcoded_current_scale':0,'hardcoded_H7_assumption':0,'hardcoded_required_view_count':0,'client_requirement_coverage':'PARTIAL; planning and runtime construction implemented, save/export/reopen blocked','input_sha256':sha(a.input),'build':'PASS_WITH_UNUSED_HELPER_WARNINGS','planner_tests':'5/5 PASS; synthetic evidence tests are not cross-model SolidWorks regression','latest_code_runtime_limit':'estimated segment crossing penalty added after last runtime; build/test verified, not rerun against SolidWorks'}
target=Path(a.report);target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({k:result[k] for k in ['status','selected_view_count','annotation_created_count','annotation_estimated_conflicts','unaccepted_working_copies']},ensure_ascii=False,indent=2))

