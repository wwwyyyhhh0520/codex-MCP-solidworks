"""Policy-driven minimum semantic coverage and physical sheet composition.
Requires actual projected candidate evidence; does not infer datums or fits.
"""
import argparse, itertools, json, math
from pathlib import Path

def load(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def overlap(a,b): return min(a[2],b[2])>max(a[0],b[0])+1e-9 and min(a[3],b[3])>max(a[1],b[1])+1e-9
def leaves(v): return sum(leaves(x) for x in v.values()) if isinstance(v,dict) else 1

def _step_evidence(candidates):
    """Return profile candidates whose measured vertices contain interior levels."""
    found=[]
    for c in candidates:
        axes=list(c.get('axes',{}))
        vertices=c.get('vertices',[])
        if len(axes)!=2 or len(vertices)<4: continue
        levels={a:sorted({round(float(v[{'X':0,'Y':1,'Z':2}[a]]),12) for v in vertices}) for a in axes}
        interior={a:[b-a for a,b in zip(vals,vals[1:]) if b-a>1e-9] for a,vals in levels.items()}
        if any(len(levels[a])>=3 and interior[a] for a in axes):
            found.append({**c,'step_levels':levels,'step_gaps':interior,
                          'step_score':float(c.get('visible_vertex_count',0) or 0)+sum(len(levels[a])-2 for a in axes)})
    return found

def _overall_axis_provenance(semantics, axis):
    """Transport extractor axis evidence without interpreting or altering it."""
    evidence = semantics.get('bounding_box', {}).get('axis_evidence')
    if not isinstance(evidence, dict):
        return None
    candidate = evidence.get(axis)
    return candidate if isinstance(candidate, dict) else None

def _hole_contract(hole):
    """Carry only Hole Wizard facts that are present in semantic evidence."""
    known = {
        'hole_identity': hole.get('feature_name', hole.get('semantic_ref')),
        'hole_group': hole.get('semantic_ref', hole.get('feature_name')),
        'feature_type': hole.get('feature_type'),
        'definition_status': hole.get('definition_status'),
        'required_callout': True,
        'quantity': hole.get('sketch_point_count') if hole.get('sketch_point_count', 0) > 0 else None,
        'quantity_status': 'SEMANTIC_PLACEMENT_COUNT' if hole.get('sketch_point_count', 0) > 0 else 'UNKNOWN',
        'specification': {
            key: hole[key] for key in (
                'Standard', 'Standard2', 'FastenerType', 'FastenerType2', 'FastenerSize',
                'Diameter', 'HoleDiameter', 'ThruHoleDiameter', 'Depth', 'HoleDepth',
                'ThruHoleDepth', 'ThreadDepth', 'ThreadClass', 'EndCondition',
                'CounterBoreDiameter', 'CounterBoreDepth', 'CounterSinkDiameter',
                'CounterSinkDepth', 'CounterSinkAngle'
            ) if key in hole and hole[key] not in (None, '', 0, -1)
        },
        'placement_count': len(hole.get('sketch_points', [])),
        'placement_source': 'HoleWizard.sketch_points' if hole.get('sketch_points') else None,
    }
    return {key: value for key, value in known.items() if value is not None}

def _hole_location_requests(hole):
    """Accept explicit semantic reference facts; never derive a datum here."""
    requests = []
    for ref in hole.get('location_references', []):
        if not isinstance(ref, dict):
            continue
        axis = str(ref.get('axis', '')).strip().upper()
        reference_id = ref.get('reference_id', ref.get('id'))
        reference_kind = ref.get('reference_kind', ref.get('entity_kind'))
        distance = ref.get('distance_m', ref.get('expected_distance_m'))
        coordinate = ref.get('coordinate_m', ref.get('coordinate'))
        if axis not in {'X', 'Y', 'Z'} or not reference_id or not reference_kind:
            continue
        try:
            distance = float(distance)
            coordinate = float(coordinate)
        except (TypeError, ValueError):
            continue
        if not (math.isfinite(distance) and math.isfinite(coordinate) and distance > 0):
            continue
        requests.append({
            'reference_id': str(reference_id),
            'reference_kind': str(reference_kind),
            'axis': axis,
            'coordinate_m': coordinate,
            'distance_m': distance,
            'placement_role': ref.get('placement_role', 'FIRST'),
            'selection_authority': ref.get('selection_authority', 'SEMANTIC_TOPOLOGY'),
            'source': ref.get('source', 'HoleWizard_location_evidence'),
        })
    unique = {}
    for request in requests:
        unique[(request['reference_id'], request['axis'], request['placement_role'])] = request
    return list(unique.values())

def make_policy_plan(sem,evidence,policy,profile):
    pp=policy['planning']; sw,sh=evidence['sheet_width_m'],evidence['sheet_height_m']; margin=profile['margin_mm']/1000
    usable=[x*(sw if i%2==0 else sh) for i,x in enumerate(profile['usable_normalized_bbox'])]
    usable=[usable[0]+margin,usable[1]+margin,usable[2]-margin,usable[3]-margin]
    reserved=[{'name':r['name'],'bbox_m':[x*(sw if i%2==0 else sh) for i,x in enumerate(r['normalized_bbox'])]} for r in profile['reserved_regions']]
    candidates=evidence['candidates']; required={'OVERALL:'+a for a in 'XYZ' if sem['bounding_box']['size_'+a.lower()+'_m']>0}
    hole_location_requests=[]
    for h in sem.get('holewizard_semantics', []):
        required.add('CALLOUT:'+h['feature_name'])
        if h['sketch_point_count']>1: required.add('PATTERN:'+h['feature_name'])
        for request in _hole_location_requests(h):
            request=dict(request, hole_identity=h['feature_name'])
            hole_location_requests.append(request)
            required.add(f"HOLE_LOCATION:{h['feature_name']}:{request['reference_id']}")
    max_area=max(c['projected_width_m']*c['projected_height_m'] for c in candidates)
    max_vertices=max(c['visible_vertex_count'] for c in candidates) or 1
    weights=pp['main_score_weights'];matrix=[]
    for c in candidates:
        cov={'OVERALL:'+a for a in c['axes']}
        for h in c['hole_groups']:
            if h['visible_opening_count']>0:cov.add('CALLOUT:'+h['semantic_ref'])
            if h['visible_opening_count']>=h['semantic_placement_count']>1:cov.add('PATTERN:'+h['semantic_ref'])
            for request in hole_location_requests:
                if request['hole_identity'] == h['semantic_ref'] and h['visible_opening_count'] > 0 and request['axis'] in c.get('axes', {}):
                    cov.add(f"HOLE_LOCATION:{request['hole_identity']}:{request['reference_id']}")
        area=c['projected_width_m']*c['projected_height_m']/max_area
        geom=c['visible_vertex_count']/max_vertices
        hole=sum(h['visible_opening_count'] for h in c['hole_groups'])/max(1,sum(h['semantic_placement_count'] for h in c['hole_groups']))
        score=weights['area']*area+weights['visible_geometry']*geom+weights['hole_visibility']*hole+weights['semantic_coverage']*len(cov)/len(required)+weights['complexity']*geom
        matrix.append(dict(c,coverage=sorted(cov),score=score))
    main=max(matrix,key=lambda c:c['score']); reachable=required & set().union(*(set(c['coverage']) for c in matrix))
    vp=policy.get('view_policy',{}); min_views=int(vp.get('minimum_orthographic_view_count',1)); max_views=int(vp.get('maximum_orthographic_view_count',len(matrix)))
    def family(c):
        o=c.get('orientation','').upper(); return 'XY' if o in ('FRONT','BACK') else 'XZ' if o in ('TOP','BOTTOM') else 'YZ' if o in ('LEFT','RIGHT') else o
    others=[c for c in matrix if c is not main];selected=None
    for n in range(len(others)+1):
        fits=[(main,)+cs for cs in itertools.combinations(others,n) if reachable<=set().union(set(main['coverage']),*(set(c['coverage']) for c in cs))]
        fits=[cs for cs in fits if len(cs)+1>=min_views and len(cs)+1<=max_views and len({family(c) for c in (main,)+cs})>=min(min_views,3)]
        if fits:
            selected=max(fits,key=lambda cs:sum(c['score'] for c in cs)-pp['redundancy_penalty']*(sum(len(c['coverage']) for c in cs)-len(reachable)));break
    pad=pp['annotation_padding_mm']/1000; gap=pp['inter_view_spacing_mm']/1000
    frames=profile['standard_orientation_frames'];frame=frames[main['orientation']]
    dot=lambda a,b:sum(x*y for x,y in zip(a,b))
    trials=[];layout=None
    for num,den in sorted(pp['allowed_scales'],key=lambda s:s[0]/s[1],reverse=True):
        if num<=0 or den<=0:raise ValueError('INVALID_SCALE_POLICY')
        scale=num/den; boxes=[];positions=[]
        for i,c in enumerate(selected):
            # Probe outline includes view padding. Keep its measured overhead as an additional reserve.
            ow=c['outline_m'][2]-c['outline_m'][0]-c['projected_width_m']
            oh=c['outline_m'][3]-c['outline_m'][1]-c['projected_height_m']
            w=c['projected_width_m']*scale+max(0,ow)+2*pad;h=c['projected_height_m']*scale+max(0,oh)+2*pad
            if not boxes:x=y=0
            else:
                normal=frames[c['orientation']]['normal'];sign=-1 if pp['projection']=='first_angle' else 1
                horizontal=dot(normal,frame['right'])*sign;vertical=dot(normal,frame['up'])*sign
                if abs(horizontal)>0:
                    x=(max(b[2] for b in boxes)+gap+w/2) if horizontal>0 else (min(b[0] for b in boxes)-gap-w/2);y=0
                elif abs(vertical)>0:
                    x=0;y=(max(b[3] for b in boxes)+gap+h/2) if vertical>0 else (min(b[1] for b in boxes)-gap-h/2)
                else:x=max(b[2] for b in boxes)+gap+w/2;y=0
            boxes.append([x-w/2,y-h/2,x+w/2,y+h/2]);positions.append([x,y])
        union=[min(b[0] for b in boxes),min(b[1] for b in boxes),max(b[2] for b in boxes),max(b[3] for b in boxes)]
        dx=(usable[0]+usable[2]-union[0]-union[2])/2;dy=(usable[1]+usable[3]-union[1]-union[3])/2
        boxes=[[b[0]+dx,b[1]+dy,b[2]+dx,b[3]+dy] for b in boxes];positions=[[p[0]+dx,p[1]+dy] for p in positions]
        valid=all(b[0]>=usable[0] and b[1]>=usable[1] and b[2]<=usable[2] and b[3]<=usable[3] for b in boxes) and not any(overlap(b,r['bbox_m']) for b in boxes for r in reserved) and not any(overlap(a,b) for a,b in itertools.combinations(boxes,2))
        trials.append({'numerator':num,'denominator':den,'fits':valid})
        if valid:layout=(num,den,boxes,positions);break
    if layout is None:raise ValueError('NO_ALLOWED_SCALE_FITS_RESERVED_VIEW_ENVELOPES')
    num,den,boxes,positions=layout
    views=[dict(orientation=c['orientation'],scale={'numerator':num,'denominator':den},placement={'x_m':p[0],'y_m':p[1]},reserved_bbox_m=b,coverage=c['coverage'],selection_reason='MAXIMUM_INFORMATION_MAIN_VIEW' if c is main else 'ADDS_MISSING_SEMANTIC_COVERAGE',added_coverage=sorted(set(c['coverage'])-set(main['coverage'])) if c is not main else c['coverage']) for c,b,p in zip(selected,boxes,positions)]
    intents=[]
    for intent in sorted(reachable):
        owners=[c for c in selected if intent in c['coverage']];owner=max(owners,key=lambda c:c['score'])
        kind,key=intent.split(':',1);value=sem['bounding_box']['size_'+key.lower()+'_m'] if kind=='OVERALL' else None
        location_request=None
        if kind=='HOLE_LOCATION':
            hole_name,reference_id=key.split(':',1)
            location_request=next((r for r in hole_location_requests if r['hole_identity']==hole_name and r['reference_id']==reference_id),None)
            value=location_request['distance_m'] if location_request else None
        if kind=='OVERALL':
            def owner_score(c):
                axes=set(c.get('axes',[])); fam=family(c)
                axis_bonus=2.0 if key in axes else 0.0
                profile_bonus=1.5 if key=='Z' and fam=='YZ' else 0.0
                return axis_bonus+profile_bonus+c['score']
            owner=max(owners,key=owner_score)
        planned_intent={'semantic_identity':intent,'kind':kind,'axis_or_feature':key,'owner':owner['orientation'],'expected_value_m':value,'value_authority':'SEMANTIC_MODEL_FRAME','candidate_owner_views':[c['orientation'] for c in owners],'candidate_scores':{c['orientation']:c['score'] for c in owners},'ownership_reason':'SIDE_PROFILE_DIRECT_THICKNESS_PROJECTION' if kind=='OVERALL' and key=='Z' and family(owner)=='YZ' else 'MAXIMUM_INFORMATIVE_PROJECTION','duplicate_suppressed':False}
        if kind == 'CALLOUT':
            hole = next((item for item in sem.get('holewizard_semantics', []) if item.get('feature_name') == key), None)
            if hole is not None:
                planned_intent['hole_contract'] = _hole_contract(hole)
                planned_intent['binding_contract'] = {
                    'entity_kind': 'VISIBLE_HOLE_OPENING',
                    'matching_authority': 'HOLEWIZARD_PLACEMENT_TO_CORRESPONDING_CIRCLE',
                    'native_api': 'IDrawingDoc.AddHoleCallout2',
                    'safe_failure': 'HOLE_CALLOUT_UNRESOLVED',
                }
        elif kind=='HOLE_LOCATION' and location_request is not None:
            planned_intent['hole_location']={
                'hole_identity':location_request['hole_identity'],
                'reference_id':location_request['reference_id'],
                'reference_kind':location_request['reference_kind'],
                'axis':location_request['axis'],
                'coordinate_m':location_request['coordinate_m'],
                'placement_role':location_request['placement_role'],
                'distance_m':location_request['distance_m'],
                'selection_authority':location_request['selection_authority'],
                'source':location_request['source'],
            }
            planned_intent['binding_contract']={
                'entity_kind':'HOLE_CENTER_TO_REFERENCE_GEOMETRY',
                'native_api':'AddHorizontalDimension2|AddVerticalDimension2',
                'safe_failure':'HOLE_LOCATION_UNRESOLVED',
            }
        # Provenance is a semantic contract owned by the extractor.  The planner
        # neither derives geometry nor changes source, span, or support fields.
        if kind=='OVERALL':
            axis_provenance=_overall_axis_provenance(sem,key)
            if axis_provenance is not None: planned_intent['axis_provenance']=axis_provenance
        intents.append(planned_intent)
    # Step intents require actual projected profile levels.  They are derived
    # from measured vertices and kept separate from overall extents.
    step_candidates=_step_evidence(candidates)
    selected_steps=[c for c in step_candidates if c['orientation'] in {v['orientation'] for v in views}]
    if selected_steps:
        owner=max(selected_steps,key=lambda c:(sum(len(g) for g in c['step_gaps'].values()),c['step_score']))
        axes=list(owner['axes'])
        vertical='Z' if 'Z' in axes else axes[-1]
        horizontal=next((a for a in axes if a!=vertical), axes[0])
        for role,axis,definition_role in (
            ('STEP_HEIGHT',vertical,'height'),
            ('STEP_DEPTH',horizontal,'depth'),
            ('STEP_OFFSET',horizontal,'offset')):
            gaps=owner['step_gaps'].get(axis,[])
            if not gaps: continue
            value=(max(gaps) if definition_role=='offset' and len(gaps)>1 else min(gaps))
            identity=f'STEP:{role}:{axis}'
            intents.append({'semantic_identity':identity,'kind':'STEP','axis_or_feature':axis,
                'definition_role':definition_role,'owner':owner['orientation'],'expected_value_m':value,
                'value_authority':'PROJECTED_PROFILE_VERTEX_LEVELS','candidate_owner_views':[c['orientation'] for c in selected_steps],
                'candidate_scores':{c['orientation']:c['step_score'] for c in selected_steps},
                'ownership_reason':'EXPLICIT_PROJECTED_STEP_CONTOUR','duplicate_suppressed':False,
                'step_axis':axis,'step_evidence':owner['step_levels'][axis]})
    return {'status':'PLANNED_WITH_GEOMETRY_EVIDENCE','CLIENT_POLICY_LOADED':True,'POLICY_VERSION':policy['policy_version'],'POLICY_RULE_COUNT':leaves(policy),'orthographic_view_count':len(views),'selected_view_roles':[v['orientation'] for v in views],'orthographic_plane_coverage':sorted({family(c) for c in selected}),'minimum_view_policy_pass':len(views)>=min_views,'sheet_geometry':{'width_m':sw,'height_m':sh},'usable_sheet_bbox_m':usable,'reserved_regions':reserved,'views':views,'main_view':main['orientation'],'secondary_views':[c['orientation'] for c in selected[1:]],'view_coverage_matrix':matrix,'intents':intents,'step_feature_evidence':[{k:v for k,v in c.items() if k in ('orientation','step_levels','step_gaps','step_score')} for c in step_candidates],'unresolved_intents':sorted(required-reachable)+['FEATURE_LOCATION:REQUIRES_DATUM_CONFIRMATION'],'hole_location_requests':hole_location_requests,'scale_candidates':trials,'scale':{'numerator':num,'denominator':den},'scale_reason':'LARGEST_ALLOWED_SCALE_FITTING_ALL_RESERVED_VIEW_ENVELOPES','estimated_sheet_utilization':sum(c['projected_width_m']*c['projected_height_m']*(num/den)**2 for c in selected)/(sw*sh),'planned_view_overlap_count':0,'planned_reserved_overlap_count':0,'opposite_ambiguity':'Symmetric coverage does not prove shape or occlusion equivalence; equal-score ties retain evidence order','policy':policy,'template_profile':profile,'dimension_strategy':{'sparse':'direct/baseline when explicit datum exists; otherwise location unresolved and permitted representative pitch','symmetry':'no design symmetry inferred','duplicate_key':'intent+semantic endpoints/features+axis+semantic value authority'},'hidden_line':'NOT_EVALUATED','centerline':'RUNTIME_VERIFICATION_REQUIRED'}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--semantics',required=True);p.add_argument('--evidence',required=True);p.add_argument('--policy',required=True);p.add_argument('--profile',required=True);p.add_argument('--output',required=True);a=p.parse_args()
    result=make_policy_plan(load(a.semantics),load(a.evidence),load(a.policy),load(a.profile));Path(a.output).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:result[k] for k in ['CLIENT_POLICY_LOADED','POLICY_VERSION','POLICY_RULE_COUNT','main_view','secondary_views','scale','estimated_sheet_utilization','unresolved_intents']},ensure_ascii=False,indent=2))
