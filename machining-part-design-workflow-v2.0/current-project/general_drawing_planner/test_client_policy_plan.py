import copy,itertools,json,unittest
from pathlib import Path
from client_policy_plan import make_policy_plan,load,overlap,_hole_contract,_hole_location_requests
R=Path(__file__).resolve().parents[1]
class PolicyPlannerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.sem=load(R/'generalization_regression/model_01_dispatch/model_semantics.json')
        cls.evidence=load(R/'generalization_regression/model_01_dispatch/policy_v2/view_projection_evidence.json')
        cls.policy=load(R/'config/client_drawing_policy.json')
        cls.profile=load(R/'config/template_profiles/company_landscape.json')
    def plan(self,s=None,e=None):return make_policy_plan(s or self.sem,e or self.evidence,self.policy,self.profile)
    def test_all_axes_covered_once_and_reserved_clear(self):
        p=self.plan();self.assertEqual({i['axis_or_feature'] for i in p['intents'] if i['kind']=='OVERALL'},set('XYZ'))
        self.assertEqual(len({i['semantic_identity'] for i in p['intents']}),len(p['intents']))
        for v in p['views']:
            self.assertFalse(any(overlap(v['reserved_bbox_m'],r['bbox_m']) for r in p['reserved_regions']))
        self.assertFalse(any(overlap(a['reserved_bbox_m'],b['reserved_bbox_m']) for a,b in itertools.combinations(p['views'],2)))
    def test_rename_model_does_not_change_choices(self):
        s=copy.deepcopy(self.sem);s['model_path']='arbitrary-name.SLDPRT'
        self.assertEqual(self.plan()['views'],self.plan(s)['views'])
    def test_larger_geometry_requires_lower_scale(self):
        s=copy.deepcopy(self.sem);e=copy.deepcopy(self.evidence)
        for k in s['bounding_box']:
            if k.endswith('_m'):s['bounding_box'][k]*=10
        for c in e['candidates']:
            old_width=c['projected_width_m'];old_height=c['projected_height_m']
            for key in ('projected_width_m','projected_height_m'):c[key]*=10
            b=c['outline_m'];cx=(b[0]+b[2])/2;cy=(b[1]+b[3])/2
            width=(b[2]-b[0])+9*old_width;height=(b[3]-b[1])+9*old_height
            c['outline_m']=[cx-width/2,cy-height/2,cx+width/2,cy+height/2]
        p=self.plan(s,e);a=self.plan()['scale'];b=p['scale'];self.assertLess(b['numerator']/b['denominator'],a['numerator']/a['denominator'])
    def test_missing_hole_visibility_is_unresolved(self):
        e=copy.deepcopy(self.evidence)
        for c in e['candidates']:
            for h in c['hole_groups']:h['visible_opening_count']=0
        p=self.plan(e=e);self.assertTrue(any(x.startswith('CALLOUT:') for x in p['unresolved_intents']))
    def test_secondary_has_real_additional_coverage(self):
        p=self.plan()
        policy_view=self.policy.get('view_policy',{})
        minimum=int(policy_view.get('minimum_orthographic_view_count', 0))
        self.assertGreaterEqual(p['orthographic_view_count'], minimum)
        roles=[view['orientation'] for view in p['views']]
        self.assertEqual(len(roles), len(set(roles)))

        matrix={row['orientation']:row for row in p['view_coverage_matrix']}
        def plane_family(view):
            axes=matrix[view['orientation']].get('axes',{})
            return ''.join(sorted(axes))

        families=[plane_family(view) for view in p['views']]
        self.assertEqual(len(families), len(set(families)))
        required=set(policy_view.get('required_orthographic_plane_families', []))
        if required:
            self.assertTrue(required.issubset(set(families)))
        self.assertEqual(set(p['orthographic_plane_coverage']), set(families))

        coverage=set(p['views'][0]['coverage'])
        seen_families={families[0]}
        for view,family in zip(p['views'][1:], families[1:]):
            semantic_delta=set(view['coverage'])-coverage
            plane_delta={family}-seen_families
            self.assertTrue(semantic_delta or plane_delta)
            coverage.update(view['coverage'])
            seen_families.add(family)

    def _axis_provenance(self, axis, source):
        return {'axis':axis,'low_coordinate':-1.0,'high_coordinate':2.0,'span':3.0,
                'low_proven':source=='PROVEN_TOPOLOGY','high_proven':source=='PROVEN_TOPOLOGY',
                'coverage_complete':source=='PROVEN_TOPOLOGY','target_source':source,
                'low_support':{'support_kind':'VERTEX','topology_identity_scope':'EXTRACTION_SESSION_ONLY'},
                'high_support':{'support_kind':'VERTEX','topology_identity_scope':'EXTRACTION_SESSION_ONLY'},
                'unproven_reason':None if source=='PROVEN_TOPOLOGY' else 'UNSUPPORTED_CURVE_INTERIOR'}

    def test_axis_provenance_mixed_axes_is_transported_verbatim(self):
        s=copy.deepcopy(self.sem)
        s['bounding_box']['preferred_source']='APPROXIMATE_FALLBACK'
        sources={'X':'PROVEN_TOPOLOGY','Y':'APPROXIMATE_FALLBACK','Z':'PROVEN_TOPOLOGY'}
        s['bounding_box']['axis_evidence']={axis:self._axis_provenance(axis,source) for axis,source in sources.items()}
        overall={intent['axis_or_feature']:intent for intent in self.plan(s)['intents'] if intent['kind']=='OVERALL'}
        self.assertEqual(set(overall),set('XYZ'))
        for axis,source in sources.items():
            self.assertEqual(overall[axis]['axis_provenance'],s['bounding_box']['axis_evidence'][axis])
            self.assertEqual(overall[axis]['axis_provenance']['target_source'],source)

    def test_axis_provenance_all_proven_preserves_support_evidence(self):
        s=copy.deepcopy(self.sem)
        s['bounding_box']['axis_evidence']={axis:self._axis_provenance(axis,'PROVEN_TOPOLOGY') for axis in 'XYZ'}
        overall={intent['axis_or_feature']:intent for intent in self.plan(s)['intents'] if intent['kind']=='OVERALL'}
        for axis in 'XYZ':
            transported=overall[axis]['axis_provenance']
            self.assertEqual(transported,s['bounding_box']['axis_evidence'][axis])
            self.assertEqual(transported['low_support']['topology_identity_scope'],'EXTRACTION_SESSION_ONLY')

    def test_axis_provenance_legacy_semantics_remains_plannable(self):
        s=copy.deepcopy(self.sem)
        s['bounding_box'].pop('axis_evidence',None)
        overall=[intent for intent in self.plan(s)['intents'] if intent['kind']=='OVERALL']
        self.assertEqual({intent['axis_or_feature'] for intent in overall},set('XYZ'))
        self.assertTrue(all('axis_provenance' not in intent for intent in overall))

    def test_holewizard_semantic_creates_callout_contract(self):
        p=self.plan()
        callout=next(item for item in p['intents'] if item['kind']=='CALLOUT')
        contract=callout['hole_contract']
        self.assertEqual(contract['hole_identity'], 'M5 间隙孔1')
        self.assertEqual(contract['quantity'], 2)
        self.assertEqual(contract['specification']['FastenerSize'], 'M5')
        self.assertEqual(callout['binding_contract']['native_api'], 'IDrawingDoc.AddHoleCallout2')

    def test_hole_contract_does_not_infer_missing_specification(self):
        s=copy.deepcopy(self.sem)
        hole=s['holewizard_semantics'][0]
        hole.pop('FastenerSize', None)
        hole['FastenerSize'] = ''
        p=self.plan(s)
        contract=next(item for item in p['intents'] if item['kind']=='CALLOUT')['hole_contract']
        self.assertNotIn('FastenerSize', contract['specification'])

    def test_hole_callout_contract_survives_model_rename(self):
        s=copy.deepcopy(self.sem)
        s['model_path']='renamed-unseen-part.SLDPRT'
        a=next(item for item in self.plan()['intents'] if item['kind']=='CALLOUT')
        b=next(item for item in self.plan(s)['intents'] if item['kind']=='CALLOUT')
        self.assertEqual(a['hole_contract'], b['hole_contract'])

    def test_multiple_hole_contracts_remain_distinct(self):
        s=copy.deepcopy(self.sem)
        extra=copy.deepcopy(s['holewizard_semantics'][0]); extra['feature_name']='SECOND_HOLE'; extra['sketch_points']=[]; extra['sketch_point_count']=0
        s['holewizard_semantics'].append(extra)
        a=_hole_contract(s['holewizard_semantics'][0]); b=_hole_contract(s['holewizard_semantics'][1])
        self.assertNotEqual(a['hole_identity'], b['hole_identity'])

    def test_explicit_hole_location_requests_are_deduplicated(self):
        hole={'location_references':[
            {'reference_id':'DATUM-A','reference_kind':'EDGE','axis':'X','coordinate_m':0.0,'distance_m':0.01},
            {'reference_id':'DATUM-A','reference_kind':'EDGE','axis':'X','coordinate_m':0.0,'distance_m':0.01}]}
        self.assertEqual(len(_hole_location_requests(hole)),1)

    def test_hole_location_requires_real_reference_fields(self):
        hole={'location_references':[{'reference_id':'fake','axis':'X','distance_m':0.01}]}
        self.assertEqual(_hole_location_requests(hole),[])
if __name__=='__main__':unittest.main()
