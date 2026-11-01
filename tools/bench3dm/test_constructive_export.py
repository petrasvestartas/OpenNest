import argparse
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent))
from export_constructive import export
from validate_constructive_csv import convert
from test_constructive import part, instance


class NativeExportTests(unittest.TestCase):
    def options(self, **kw):
        values=dict(rotations=8,allow_mirror=False,sheets=1,beam=2,branch=2,positions=8,
                    restarts=1,workers=1,seed=100,smart=True,conservative_proxies=True,
                    seconds=1,scan_step=10,scale=100000)
        values['stock_guard']=0
        values.update(kw)
        return values

    def test_export_explicit_restrictions_and_implicit_smart(self):
        parts=[part('explicit',2,2),part('implicit',2,2)]
        parts[0]['allowed_angles']=[.25,.75]
        del parts[1]['rotations']
        inst=instance(parts)
        with tempfile.TemporaryDirectory() as tmp:
            source=Path(tmp)/'instance.json';source.write_text(json.dumps(inst))
            target=Path(tmp)/'job.ncb'
            export(inst,target,argparse.Namespace(instance=str(source),out=str(target),**self.options()))
            lines=target.read_text().splitlines()
            self.assertEqual(lines[2].split()[-1],'3')
            self.assertEqual(lines[5],'1 0 0 2')
            self.assertEqual(lines[6],'1 0 2 0')
            self.assertEqual(json.loads(target.with_suffix('.ncb.json').read_text())['part_ids'],['explicit','implicit'])

    def test_original_geometry_rejects_quantization_undershoot(self):
        inst=instance([part(0,4.000004,2)],4,2)
        rows=[dict(part=0,copy=0,sheet=0,mirrored=0,tx=0,ty=0,angle=0)]
        _,metrics=convert(inst,rows,self.options())
        self.assertFalse(metrics['geometry_valid_partial'])
        self.assertTrue(metrics['solve_violations'])

    def test_csv_checks_all_copies_and_restricted_angles(self):
        inst=instance([part(9,2,2,qty=2)])
        row=dict(part=0,copy=0,sheet=0,mirrored=0,tx=0,ty=0,angle=0)
        with self.assertRaisesRegex(ValueError,'every requested'):
            convert(inst,[row],self.options())
        other=dict(row,copy=1,angle=.3)
        with self.assertRaisesRegex(ValueError,'angle'):
            convert(inst,[row,other],self.options())

    def test_guard_shrinks_stock_moves_defects_and_restores_translation(self):
        inst=instance([part(0,2,2)],4,4)
        inst['solve_sheet']['holes']=[[[3,3],[3.5,3],[3.5,3.5],[3,3.5]]]
        with tempfile.TemporaryDirectory() as tmp:
            source=Path(tmp)/'instance.json';source.write_text(json.dumps(inst))
            target=Path(tmp)/'job.ncb'
            data=export(inst,target,argparse.Namespace(instance=str(source),out=str(target),**self.options(stock_guard=.01)))
            self.assertEqual(list(map(float,target.read_text().splitlines()[1].split())),[3.98,3.98])
            self.assertAlmostEqual(data['xy'][8],2.99)
            self.assertEqual(data['xy'][0],0)
            meta=json.loads(target.with_suffix('.ncb.json').read_text())
            self.assertEqual(meta['native_to_solve_translation'],[.01,.01])
        rows=[dict(part=0,copy=0,sheet=0,mirrored=0,tx=0,ty=0,angle=0)]
        doc,metrics=convert(inst,rows,self.options(stock_guard=.01))
        self.assertEqual(doc['instances'][0]['tx'],.01)
        self.assertTrue(metrics['geometry_valid_partial'])

    def test_guard_protects_source_geometry_beyond_solve_outline(self):
        inst=instance([part(0,2,2)],4,4)
        # Source lies .0008 beyond its approximate solve ring at the left edge.
        inst['parts'][0]['outer']=[[-.0008,0],[2,0],[2,2],[-.0008,2]]
        rows=[dict(part=0,copy=0,sheet=0,mirrored=0,tx=0,ty=0,angle=0)]
        self.assertFalse(convert(inst,rows,self.options())[1]['geometry_valid_partial'])
        self.assertTrue(convert(inst,rows,self.options(stock_guard=.01))[1]['geometry_valid_partial'])


if __name__=='__main__':
    unittest.main()
