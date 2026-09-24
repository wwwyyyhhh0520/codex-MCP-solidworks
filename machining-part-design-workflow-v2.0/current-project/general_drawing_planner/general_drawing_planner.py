"""CLI adapter for the existing conservative PartAnalysis planner."""
import argparse
import json
import sys
import traceback
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from src.auto_drawing.models import PartAnalysis
from src.auto_drawing.planner import make_plan
from src.auto_drawing.dimension_planner import enrich_dimensions

def transport_hole_facts(plan, semantics, feature_history=None):
    """Attach only source-backed hole and reversible-topology facts to plan groups."""
    holes = {h.get('feature_name'): h for h in semantics.get('holewizard_semantics', [])}
    deltas = (feature_history or {}).get('deltas', [])
    relevant = (feature_history or {}).get('relevant_feature_indices', [])
    for item in plan.get('hole_callouts', []):
        name = item.get('feature_name')
        hole = holes.get(name)
        if hole is None:
            continue
        feature_index = hole.get('feature_index')
        item['source_feature_identity'] = {
            'feature_name': name,
            'feature_index': feature_index,
            'feature_type': hole.get('feature_type'),
            'source': 'semantic_output.holewizard_semantics',
        }
        item['semantic_group_id'] = name
        item['placement_members'] = hole.get('sketch_points', [])
        matches = [d for index, d in enumerate(deltas)
                   if d.get('boundary_feature_position') == feature_index or
                   (index < len(relevant) and relevant[index] == feature_index)]
        openings = [o for d in matches for o in d.get('added_openings', [])]
        item['physical_instances'] = openings
        item['physical_instance_count'] = len(openings)
        item['origin_proven'] = bool(openings)
        item['bindable_edge_evidence'] = [o.get('boundary_circular_edge_signature', []) for o in openings]
        item['physical_opening_signature'] = [o.get('geometry_signature') for o in openings]
        item['owner_view_proven'] = False
        item['owner_view_evidence'] = []
    plan['hole_fact_transport'] = {
        'source_semantics': str(semantics.get('model_path', semantics.get('source_file', ''))),
        'source_feature_history': bool(feature_history),
        'owner_view_proven': False,
        'physical_instance_accounting': any(item.get('physical_instance_count', 0) for item in plan.get('hole_callouts', [])),
    }
    return plan


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--semantics', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--log')
    parser.add_argument('--client-policy')
    parser.add_argument('--projection-evidence')
    parser.add_argument('--template-profile')
    parser.add_argument('--feature-history')
    args = parser.parse_args()
    messages = []
    stage = 'SEMANTICS_LOAD'
    try:
        source = Path(args.semantics).resolve()
        data = json.loads(source.read_text(encoding='utf-8-sig'))
        if not isinstance(data, dict):
            raise ValueError('SEMANTICS_OBJECT_REQUIRED')
        messages.append('semantics_loaded=true')
        feature_history = json.loads(Path(args.feature_history).read_text(encoding='utf-8')) if args.feature_history else None
        if args.client_policy:
            from client_policy_plan import make_policy_plan, load
            if not args.projection_evidence or not args.template_profile:
                raise ValueError('CLIENT_POLICY_REQUIRES_PROJECTION_EVIDENCE_AND_TEMPLATE_PROFILE')
            result = make_policy_plan(data, load(args.projection_evidence), load(args.client_policy), load(args.template_profile))
            result = transport_hole_facts(result, data, feature_history)
            target = Path(args.output).resolve(); target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
            messages.append(f"CLIENT_POLICY_LOADED=true\nPOLICY_VERSION={result['POLICY_VERSION']}\nPOLICY_RULE_COUNT={result['POLICY_RULE_COUNT']}\nselected_views={[v['orientation'] for v in result['views']]}\nscale={result['scale']}")
            return 0
        stage = 'EXISTING_PLANNER'
        candidates = []
        for index, candidate in enumerate(data.get('view_candidates', []), start=1):
            width = float(candidate.get('projected_width_m', 0) or 0)
            height = float(candidate.get('projected_height_m', 0) or 0)
            if width <= 0 or height <= 0:
                continue
            candidates.append({**candidate, 'candidate_id': candidate.get('candidate_id', f'VIEW_{index:03d}'),
                'score': float(candidate.get('geometry_score', candidate.get('visible_geometry_score', 0)) or 0),
                'confidence': candidate.get('confidence', 'GEOMETRY_BBOX')})
        analysis = PartAnalysis(
            source_file=data.get('model_path', data.get('source_file', '')),
            bounding_box=data.get('bounding_box', {}),
            view_candidates=candidates,
            features=[{'name': f.get('feature_name'), 'type': f.get('feature_type')}
                      for f in data.get('feature_tree', [])],
        )
        plan = enrich_dimensions(analysis.to_dict(), make_plan(analysis).to_dict())
        plan = transport_hole_facts(plan, data, feature_history)
        # Preserve unresolved candidates; never relabel them as selected views.
        missing = []
        if not analysis.bounding_box:
            missing.append('bounding_box: min_x/max_x/min_y/max_y/min_z/max_z in meters')
        if not analysis.view_candidates:
            missing.append('view_candidates: geometry-evidenced orientation, score and confidence')
        plan['source_semantics'] = str(source)
        plan['provenance'] = {'implementation': ['src.auto_drawing.planner.make_plan',
            'src.auto_drawing.dimension_planner.enrich_dimensions'], 'adapter_only': True}
        plan['missing_input_contracts'] = missing
        plan['execution_ready'] = False
        plan['selected_view_count'] = 1 if candidates and plan.get('views') else 0
        plan['selected_view_roles'] = [plan['views'][0].get('orientation')] if plan['selected_view_count'] else []
        if candidates:
            chosen = candidates[0]
            width = float(chosen.get('projected_width_m', 0) or 0)
            height = float(chosen.get('projected_height_m', 0) or 0)
            bbox = analysis.bounding_box
            extents = [abs(float(bbox.get(f'max_{axis}_m', 0)) - float(bbox.get(f'min_{axis}_m', 0))) for axis in ('x','y','z')]
            model_extent = max(extents, default=max(width, height))
            # Generic policy envelope: placement is derived from the selected
            # projection and annotation reserve; no fixed model coordinates.
            margin = max(model_extent * 0.05, 1e-6)
            plan['placement'] = {'x_m': margin + width / 2.0, 'y_m': margin + height / 2.0}
            available_extent = 0.2
            denominator = max(1, int(__import__('math').ceil(model_extent / available_extent)))
            plan['scale'] = {'numerator': 1, 'denominator': denominator}
            if plan.get('views'):
                plan['views'][0]['placement'] = plan['placement']
                plan['views'][0]['scale'] = plan['scale']
        else:
            plan['placement'] = None
            plan['scale'] = None
        plan['feature_location_intents_status'] = 'NOT_EVALUATED'
        plan['hole_pitch_intents_status'] = 'NOT_EVALUATED'
        plan['status'] = 'BLOCKED_MISSING_GEOMETRY_EVIDENCE' if missing else 'PLANNED_WITH_GEOMETRY_EVIDENCE'
        if plan['placement'] is None or plan['scale'] is None:
            missing.append('execution_fields: placement and scale')
        elif (not all(__import__('math').isfinite(float(plan['placement'][key])) for key in ('x_m', 'y_m')) or
              float(plan['scale']['numerator']) <= 0 or float(plan['scale']['denominator']) <= 0):
            missing.append('execution_fields: finite placement and positive scale')
        stage = 'PLAN_WRITE'
        output = Path(args.output).resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding='utf-8')
        json.loads(output.read_text(encoding='utf-8'))
        messages.append('drawing_plan_written=true\ndrawing_plan_parseable=true')
        if missing:
            stage = 'PLANNER_INPUT_CONTRACT'
            raise ValueError('MISSING_PLANNER_EVIDENCE: ' + '; '.join(missing))
        messages.append(f"status={plan['status']}")
        return 0
    except Exception as exc:
        messages.append(f'stage={stage}\nexception_type={type(exc).__name__}\nmessage={exc}\nstack_trace={traceback.format_exc()}')
        return 2
    finally:
        text = '\n'.join(messages)
        print(text)
        if args.log:
            log = Path(args.log); log.parent.mkdir(parents=True, exist_ok=True)
            log.write_text(text, encoding='utf-8')

if __name__ == '__main__':
    raise SystemExit(main())
