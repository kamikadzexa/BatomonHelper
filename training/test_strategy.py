import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import torch
from policy import Policy
from persistence import output_writer,append_metrics
from strategy import analysis_input,balanced_sample,optimize
from validate_strategy import paired

class StrategyTests(unittest.TestCase):
    def test_analysis_presence_and_ablation_are_explicit(self):
        self.assertEqual(analysis_input([0.]*28),[0.]*29)
        self.assertEqual(analysis_input([1.]*28),[1.]*29)
        self.assertEqual(analysis_input([1.]*28,False),[0.]*29)

    def test_joint_model_masks_and_learns_search_and_outcome_targets(self):
        torch.set_num_threads(1);torch.manual_seed(69)
        width=300;model=Policy(width,3,version=6)
        observation=[0.]*width+[1.]+[0.]*64
        actions=[[0.]*40 for _ in range(2)];actions[0][0]=1;actions[1][8]=1
        row=dict(observation=observation,actions=actions,analysis=[.1]*28,visits=[0.,1.],outcome=1.,battle=1.,trainer='fixture',day=1)
        obs=torch.tensor([observation]);features=torch.tensor([actions]);mask=torch.tensor([[True,True]])
        with torch.no_grad():before=model(obs,features,mask,torch.zeros(1,29))[0].probs[0,1].item()
        result=optimize(model,torch.optim.Adam(model.parameters(),lr=.001),[row],'cpu',steps=8,batch=1)
        with torch.no_grad():after=model(obs,features,mask,torch.zeros(1,29))[0].probs[0,1].item()
        self.assertGreater(after,before);self.assertTrue(result['loss']>0)
        with torch.no_grad():distribution,_=model(obs,features,torch.tensor([[True,False]]),torch.zeros(1,29))
        self.assertEqual(distribution.probs[0,1].item(),0)

    def test_output_has_exclusive_writer_ownership(self):
        with tempfile.TemporaryDirectory() as directory:
            with output_writer(directory):
                with self.assertRaises(RuntimeError):
                    with output_writer(directory):pass
            with output_writer(directory):pass

    def test_locked_metrics_spool_without_losing_learning(self):
        with tempfile.TemporaryDirectory() as directory:
            original=Path.open
            def locked(path,*args,**kwargs):
                if path.name=='metrics.jsonl':raise PermissionError('locked')
                return original(path,*args,**kwargs)
            with patch.object(Path,'open',locked),patch('persistence.time.sleep'):
                self.assertFalse(append_metrics(directory,{'update':1}))
            self.assertTrue(append_metrics(directory,{'update':2}))
            rows=[json.loads(line) for line in (Path(directory)/'metrics.jsonl').read_text().splitlines()]
            self.assertEqual([r['update'] for r in rows],[1,2])

    def test_replay_balances_rare_training_groups(self):
        common=dict(trainer='a',day=1,outcome=0)
        rare=dict(trainer='b',day=13,outcome=1)
        sample=balanced_sample([common]*1000+[rare],200)
        self.assertGreater(sum(r['trainer']=='b' for r in sample),40)

    def test_paired_comparison_rejects_changed_opponents_or_partial_results(self):
        baseline=dict(identity=dict(Rules='a',Suite='development',Seed=69,Code='b',Opponents='c'),completed=True,scenarios=[dict(scenario=0,success=False)])
        candidate=dict(baseline,scenarios=[dict(scenario=0,success=True)])
        self.assertEqual(paired(candidate,baseline)['improvement'],1.)
        with self.assertRaises(ValueError):paired(dict(candidate,completed=False),baseline)
        with self.assertRaises(ValueError):paired(dict(candidate,identity=dict(candidate['identity'],Opponents='other')),baseline)

if __name__=='__main__':unittest.main()
