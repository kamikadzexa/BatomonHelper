import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import time
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import MagicMock
import numpy as np
import torch
from learner import Host,Policy,LegacyPolicy,PopulationInference,ACTION_WIDTH,padded,advantages,optimize,optimizer_for,pack_rollout
from policy import greedy_actions
from evaluation import EvaluationRunner,weights_hash

torch.set_num_threads(1)

class RelationalTests(unittest.TestCase):
    def observations(self,n):
        rows=np.zeros((n,288+64),np.float32);rows[:,287]=1;rows[:,288]=10;rows[:,289]=-1
        return rows
    def test_kind_probability_is_independent_of_candidate_count_and_greedy_matches_hierarchy(self):
        model=Policy(287,3)
        for value in model.parameters():torch.nn.init.zeros_(value)
        model.kind.bias.data[3]=.5
        row=np.zeros((91,40),np.float32);row[:90,3]=1;row[90,8]=1
        features,mask=padded([row],'cpu');distribution,_=model(torch.tensor(self.observations(1)),features,mask)
        self.assertAlmostEqual(distribution.probs[0,:90].sum().item(),torch.sigmoid(torch.tensor(.5)).item(),places=6)
        self.assertLess(greedy_actions(distribution,features,mask).item(),90)
        # Joint argmax would pick the singleton Finish despite Move being the most likely kind.
        self.assertEqual(distribution.probs.argmax(-1).item(),90)
    def test_core_only_observations_have_a_valid_empty_token_fallback(self):
        model=Policy(287,3);actions=np.zeros((1,40),np.float32);actions[0,8]=1
        features,mask=padded([actions],'cpu')
        distribution,value=model(torch.zeros(1,287),features,mask)
        self.assertEqual(distribution.probs.tolist(),[[1.]])
        self.assertTrue(torch.isfinite(value).all())
    def test_relation_effects_change_values_and_batched_population_preserves_assignment(self):
        torch.manual_seed(8);models=[Policy(287,3) for _ in range(4)]
        obs=self.observations(7);obs[:,16]=np.arange(7)%3+1
        actions=[]
        for i in range(7):
            row=np.zeros((i+1,40),np.float32);row[:,0]=1;row[:,9]=1/11;row[:,10]=1/11;actions.append(row)
        with torch.no_grad():
            distribution,values=PopulationInference(models)(obs,actions)
            for i in range(7):
                f,m=padded([actions[i]],'cpu');expected,value=models[i%4](torch.tensor(obs[i:i+1]),f,m)
                torch.testing.assert_close(distribution.probs[i,:i+1],expected.probs[0],atol=1e-5,rtol=1e-5)
                torch.testing.assert_close(values[i],value[0],atol=1e-5,rtol=1e-5)
            changed=obs.copy();changed[:,288]=6;changed[:,292]=.7
            _,different=PopulationInference(models)(changed,actions)
            self.assertFalse(torch.equal(values,different))
    def test_battle_trace_credit_crosses_shop_decisions_without_reset_bootstrap(self):
        rewards=np.array([[0],[0],[1]],np.float32);terminals=np.array([[0],[0],[1]],np.float32)
        values=np.zeros_like(rewards);battles=np.array([[False],[True],[True]])
        _,returns=advantages(rewards,terminals,values,np.array([100],np.float32),lam=.95,battles=battles)
        np.testing.assert_allclose(returns[:,0],[.95,.95,1])
    def test_bucketed_rollout_retains_candidates_and_updates_actor(self):
        torch.manual_seed(1);model=Policy(287,3);transitions=[]
        for t in range(3):
            obs=np.zeros((4,288+64*(t+1)),np.float32);obs[:,287]=t+1;obs[:,288]=10
            actions=[]
            for i in range(4):
                row=np.zeros((i+t+1,40),np.float32);row[:,0]=1;actions.append(row)
            f,m=padded(actions,'cpu')
            with torch.no_grad():dist,values=model(torch.tensor(obs),f,m)
            chosen=np.zeros(4,np.int64)
            transitions.append((obs,actions,chosen,dist.log_prob(torch.tensor(chosen)).numpy(),values.numpy()))
        data=pack_rollout(transitions,np.arange(12,dtype=np.float32).reshape(3,4),np.ones((3,4),np.float32),1,4,'cpu',287)
        self.assertEqual(sum(len(bucket[0]) for bucket in data),3)
        self.assertEqual(sum(bucket[2].sum().item() for bucket in data),9)
        metrics=optimize(model,optimizer_for(model,'cpu'),data,dict(minibatch=2,epochs=1),.01)
        self.assertTrue(np.isfinite(metrics['loss']))
    def test_scenario_sampling_is_independent_of_batch_order(self):
        from policy import scenario_sample
        probability=torch.tensor([[.2,.3,.5],[.7,.2,.1],[.1,.1,.8]])
        dist=torch.distributions.Categorical(probs=probability)
        original=scenario_sample(dist,69,[1,2,3],[5,8,9],[2,3,4])
        order=[2,0,1]
        reordered=scenario_sample(torch.distributions.Categorical(probs=probability[order]),69,[3,1,2],[9,5,8],[4,2,3])
        self.assertEqual(original[order].tolist(),reordered.tolist())
    def test_host_timeout_kills_the_owned_process(self):
        host=Host.__new__(Host);host.timeout=.005;host._reader=ThreadPoolExecutor(max_workers=1);host.errors=deque()
        host.process=MagicMock();host.process.stdout.readline.side_effect=lambda:time.sleep(.02) or ''
        try:
            with self.assertRaises(TimeoutError):host._read()
            host.process.kill.assert_called_once();host.process.wait.assert_called_once()
        finally:host._reader.shutdown(wait=True)
    def test_cache_identity_changes_with_initial_seed_encoding_and_weights(self):
        snapshot=dict(encoding=5,model_version=4,weights=None)
        self.assertNotEqual(weights_hash(snapshot,1),weights_hash(snapshot,2))
        self.assertNotEqual(weights_hash(snapshot,1),weights_hash(dict(snapshot,baseline=True),1))
        snapshot['weights']=[Policy(287,3).state_dict()]
        original=weights_hash(snapshot,1);changed=copy.deepcopy(snapshot);changed['weights'][0]['value.bias']+=.1
        self.assertNotEqual(original,weights_hash(changed,1))

class HostIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root=Path(__file__).resolve().parent.parent;cls.host=cls.root/'tools/BatomonHelper.TrainingHost/bin/Release/net10.0/BatomonHelper.TrainingHost.dll'
        if not cls.host.exists():raise unittest.SkipTest('Build the Release training host first.')
        cls.temp=tempfile.TemporaryDirectory();cls.output=Path(cls.temp.name);cls.rules=cls.output/'rules.json'
        subprocess.run(['dotnet',str(cls.host),'--fixture',str(cls.rules)],check=True,capture_output=True)
        cls.config=dict(output=str(cls.output),host=str(cls.host),rules=str(cls.rules),seed=69,device='cpu',workers=2,environments=4,
            auto_tune_workers=False,auto_tune_environments=False,curriculum=False,user_pool=str(cls.output/'user-sessions'))
        cls.checkpoint=cls.output/'checkpoint';cls.checkpoint.mkdir()
        with_host=Host(cls.config,cls.output)
        try:with_host.request('checkpoint',path=str(cls.checkpoint/'host.json'))
        finally:with_host.close()
        cls.manifest=json.loads(cls.rules.read_text())
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def test_scenarios_are_identical_across_batch_sizes_and_workers_with_exact_quotas(self):
        snapshot=dict(label='baseline',update=-1,generation=-1,encoding=5,model_version=4,weights=None,baseline=True)
        reports=[]
        for count,workers in ((4,1),(12,3)):
            config=dict(self.config,evaluation_environments=count,workers=workers)
            with EvaluationRunner(config,self.output,self.checkpoint,self.manifest,16) as runner:
                runner.progress_context=dict(status='Evaluating');report=runner.evaluate(snapshot);reports.append(report)
            self.assertEqual(report['runs'],16);self.assertTrue(report['completed'])
            self.assertEqual([x['runs'] for x in report['policies']],[4]*4)
            # Force actual execution next time rather than hiding a scheduling difference with cache.
            (self.output/'evaluation-cache'/f"{report['cache_key']}.json").unlink()
        self.assertEqual(reports[0]['scenarios'],reports[1]['scenarios'])
        self.assertEqual(reports[0]['rank_bands'],reports[1]['rank_bands'])
    def test_completed_cache_is_reused_and_cancelled_results_are_not_cached(self):
        snapshot=dict(label='baseline',update=-1,generation=-1,encoding=5,model_version=4,weights=None,baseline=True)
        with EvaluationRunner(self.config,self.output,self.checkpoint,self.manifest,4) as runner:
            runner.progress_context=dict(status='Evaluating');first=runner.evaluate(snapshot);second=runner.evaluate(snapshot)
            self.assertTrue(first['completed']);self.assertTrue(second['cached'])
            (self.output/'control.json').write_text(json.dumps(dict(command='stop')))
            cancelled=runner.evaluate(dict(snapshot,label='new',encoding=4,model_version=3))
            self.assertFalse(cancelled['completed']);self.assertEqual(cancelled['reason'],'cancelled')
            self.assertFalse((self.output/'evaluation-cache'/f"{cancelled['cache_key']}.json").exists())
    def test_stale_publication_and_host_failure_are_reported(self):
        host=Host(self.config,self.output)
        try:
            host.batch();host.request('step',actions=[0]*4)
            with self.assertRaisesRegex(RuntimeError,'Stale batch'):host.request('step',actions=[0]*4)
        finally:host.close()
        with self.assertRaisesRegex(RuntimeError,'Simulation host'):
            Host(dict(self.config,host=str(self.output/'missing-host.dll')),self.output)

if __name__=='__main__':unittest.main()
