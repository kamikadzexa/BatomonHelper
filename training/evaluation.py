"""One frozen suite, exact scenario quotas, incremental progress, and reusable evaluation host."""
from pathlib import Path
import hashlib
import json
import time
import uuid
import numpy as np
import torch

def weights_hash(snapshot,seed):
    h=hashlib.sha256()
    h.update(f"{snapshot['encoding']}:{snapshot['model_version']}:{snapshot.get('baseline',False)}".encode())
    if snapshot['weights'] is None:h.update(f'initial:{snapshot.get("initial_seed",seed)}'.encode())
    else:
        for policy in snapshot['weights']:
            for name,value in sorted(policy.items()):
                h.update(name.encode());h.update(str(value.shape).encode());h.update(value.detach().cpu().contiguous().numpy().tobytes())
    return h.hexdigest()

class EvaluationRunner:
    def __init__(self,config,output,checkpoint,rules,runs):
        from learner import Host,atomic_json,configure_compute
        if runs<4 or runs%4:raise ValueError('Runs must be a positive multiple of four.')
        self.config=config;self.output=Path(output);self.rules=rules;self.runs=runs;self.batch_id=uuid.uuid4().hex[:12]
        self.last_boards=[];self.progress_context={};self.started=time.monotonic();self.last_progress=0.;self.host=None
        self.cache=self.output/'evaluation-cache';self.cache.mkdir(parents=True,exist_ok=True)
        configure_compute(dict(config,tf32=config.get('evaluation_tf32',False)))
        host_path=Path(config['host']);assembly=host_path.parent/'BatomonHelper.Simulation.dll'
        identity=hashlib.sha256()
        for path in (checkpoint/'host.json',host_path,assembly):identity.update(path.read_bytes())
        for name in ('evaluation.py','policy.py','legacy_policy.py','learner.py'):identity.update(Path(__file__).with_name(name).read_bytes())
        pool=Path(config.get('user_pool',Path(config['rules']).parent.parent.parent/'work'/'user-sessions'))
        for path in sorted(pool.glob('*.json')):identity.update(path.name.encode());identity.update(path.read_bytes())
        if config.get('evaluation_states'):identity.update(Path(config['evaluation_states']).read_bytes())
        identity.update(json.dumps(dict(seed=config.get('evaluation_seed',config['seed']+1000000007),rules=rules['Hash'],suite_version=2),sort_keys=True).encode())
        self.suite_hash=identity.hexdigest()
        atomic_json(self.output/'control.json',dict(command='evaluate'))
        atomic_json(Path(config.get('progress_output',self.output))/'progress.json',dict(status='Preparing evaluation',runs=0,target=runs))
        evaluation=dict(config,seed=config.get('evaluation_seed',config['seed']+1000000007),evaluation=True,environments=max(4,(min(config.get('evaluation_environments',128),runs)//4)*4),
            auto_tune_workers=False,auto_tune_environments=False,auto_calibrate=False,history=str(self.output/'history'),encoding=5,
            evaluation_batch=self.batch_id,session='eval-'+self.batch_id,curriculum=False)
        target=self.output/'evaluation';target.mkdir(exist_ok=True)
        self.host=Host(evaluation,target)
        try:self.host.request('league_restore',path=str(checkpoint/'host.json'))
        except BaseException:self.host.close();raise
        self.device='cuda' if config['device']!='cpu' and torch.cuda.is_available() else 'cpu'
    def __enter__(self):return self
    def __exit__(self,*args):
        if self.host is not None:self.host.close()
    @property
    def cancelled(self):
        path=Path(self.config.get('cancel_control',self.output/'control.json'))
        try:return path.exists() and json.loads(path.read_text()).get('command')=='stop'
        except (OSError,json.JSONDecodeError):return False
    def progress(self,snapshot,completed,decisions,start,force=False):
        from learner import atomic_json
        now=time.monotonic()
        if not force and now-self.last_progress<1:return
        self.last_progress=now;elapsed=now-start;rate=completed/max(.001,elapsed)
        row=dict(self.progress_context,snapshot=snapshot['label'],runs=completed,target=self.runs,elapsed_seconds=elapsed,
            overall_elapsed_seconds=now-self.started,runs_per_second=rate,decisions=decisions,
            remaining_seconds=(self.runs-completed)/rate if rate else None,evaluation_batch=self.batch_id)
        atomic_json(Path(self.config.get('progress_output',self.output))/'progress.json',row)
    def evaluate(self,snapshot):
        from policy import scenario_sample
        from learner import Policy,LegacyPolicy,PopulationInference,load_policy,padded,greedy_actions,wilson,atomic_json
        model_hash=weights_hash(snapshot,self.config['seed'])
        key=dict(model=model_hash,suite=self.suite_hash,rules=self.rules['Hash'],runs=self.runs,
            sampling=self.config.get('evaluation_sampling',False),decision_limit=self.config.get('evaluation_decision_limit',self.runs*4000),
            shop_limit=self.config.get('shop_decision_limit',64),device=self.device,tf32=self.config.get('evaluation_tf32',False),runner_version=2)
        cache_key=hashlib.sha256(json.dumps(key,sort_keys=True).encode()).hexdigest();path=self.cache/(cache_key+'.json')
        start=time.monotonic();self.progress(snapshot,0,0,start,True)
        if path.exists() and not self.cancelled:
            boards_path=path.with_name(path.stem+'-boards.json');self.last_boards=json.loads(boards_path.read_text()) if boards_path.exists() else []
            cached=json.loads(path.read_text());cached.update(label=snapshot['label'],update=snapshot['update'],generation=snapshot['generation'],cached=True,archive=snapshot.get('archive'),baseline=snapshot.get('baseline',False))
            return cached
        seed=self.config.get('evaluation_seed',self.config['seed']+1000000007)
        self.host.request('evaluation_reset',runs=self.runs,update=snapshot['update'],generation=snapshot['generation'],encoding=snapshot['encoding'],
                          initial=snapshot['weights'] is None,session='eval-'+self.batch_id+'-'+model_hash[:10],states=self.config.get('evaluation_states'))
        torch.manual_seed(snapshot.get('initial_seed',self.config['seed']) if snapshot['weights'] is None else seed)
        cls=LegacyPolicy if snapshot['encoding']==4 else Policy
        policies=[(cls(self.host.ready['ObservationWidth'],len(self.rules['Species'])) if snapshot['encoding']==4 else cls(self.host.ready['ObservationWidth'],len(self.rules['Species']),snapshot['model_version'])).to(self.device) for _ in range(4)]
        if snapshot['weights'] is not None:
            for model,weights in zip(policies,snapshot['weights']):load_policy(model,weights)
        for model in policies:model.eval()
        torch.manual_seed(seed)
        with torch.inference_mode():population=PopulationInference(policies)
        results=[];decisions=0;stats=[dict(battles=0,deployed=0,bench=0,limited=0) for _ in range(4)];opponents={};step={}
        limit=key['decision_limit'];active=np.arange(self.host.ready['Count'])<self.runs
        while len(results)<self.runs and decisions<limit and not self.cancelled:
            observation,actions=self.host.batch()
            if snapshot.get('baseline'):
                selected=self.host.request('baseline_actions')['Actions']
            else:
                with torch.inference_mode():
                    distribution,_=population(observation,actions)
                    features,mask=padded(actions,self.device)
                    selected=(scenario_sample(distribution,seed,self.host.scenarios,self.host.days,self.host.ordinals) if key['sampling'] else greedy_actions(distribution,features,mask,snapshot['encoding']==4)).cpu().tolist()
            for i in np.flatnonzero(~active):selected[i]=-1
            step=self.host.request('step',actions=selected);decisions+=int(active.sum());active=np.array(step['Active'],bool)
            results.extend(row['Summary'] for row in step['Runs'])
            for row in step['PolicyBattleStats']:
                target=stats[row['Policy']]
                for a,b in (('battles','Battles'),('deployed','Deployed'),('bench','DeployableBench'),('limited','LimitedTurns')):target[a]+=row[b]
            for row in step.get('Opponents',[]):
                target=opponents.setdefault(f"{row['Kind']}:generation-{row['Generation']}",dict(battles=0,wins=0));target['battles']+=1;target['wins']+=int(row['Won'])
            self.progress(snapshot,len(results),decisions,start)
        scores=[]
        for i in range(4):
            subset=[x for x in results if x['Policy']==i];successes=sum(x['Success'] for x in subset);n=len(subset);battles=max(1,stats[i]['battles'])
            scores.append(dict(policy=i,runs=n,success_rate=successes/max(1,n),confidence_interval_95=wilson(successes,n),
                average_wins=sum(x['Wins'] for x in subset)/max(1,n),average_deployed=stats[i]['deployed']/battles,
                unused_deployable_bench=stats[i]['bench']/battles,limited_turn_rate=stats[i]['limited']/battles))
        successes=sum(x['Success'] for x in results);n=len(results);interval=wilson(successes,n);battles=max(1,sum(x['battles'] for x in stats))
        trainers={}
        for row in results:
            target=trainers.setdefault(row['Trainer'],dict(runs=0,successes=0,wins=0));target['runs']+=1;target['successes']+=int(row['Success']);target['wins']+=row['Wins']
        report=dict(label=snapshot['label'],update=snapshot['update'],generation=snapshot['generation'],encoding=snapshot['encoding'],archive=snapshot.get('archive'),baseline=snapshot.get('baseline',False),
            scenarios=sorted([dict(scenario=x.get('ScenarioId'),policy=x['Policy'],wins=x['Wins'],lives=x['Lives'],days=x['Days'],success=x['Success'],starting_rank=x.get('StartingRank',1),additional_wins=x['Wins']-x.get('StartingWins',0)) for x in results],key=lambda x:x['scenario']),
            runs=n,target=self.runs,completed=n==self.runs and all(x['runs']==self.runs//4 for x in scores),
            success_rate=successes/max(1,n),confidence_interval_95=interval,score=interval[0],average_wins=sum(x['Wins'] for x in results)/max(1,n),
            average_deployed=sum(x['deployed'] for x in stats)/battles,unused_deployable_bench=sum(x['bench'] for x in stats)/battles,
            limited_turn_rate=sum(x['limited'] for x in stats)/battles,policies=scores,opponents=opponents,trainers=trainers,
            rank_bands=step.get('RankBands',{}),average_additional_wins=sum(x['Wins']-x.get('StartingWins',0) for x in results)/max(1,n),seconds=time.monotonic()-start,decisions=decisions,cache_key=cache_key,model_hash=model_hash,
            suite_hash=self.suite_hash,evaluation_batch=self.batch_id,cached=False,selection='sample' if key['sampling'] else 'greedy',
            diagnostic_fixture=self.rules['Build']=='fixture',reason='cancelled' if self.cancelled else 'decision_limit' if n<self.runs else None)
        self.progress(snapshot,n,decisions,start,True)
        if report['completed']:
            atomic_json(path,report)
            if not snapshot.get('baseline'):
                self.last_boards=self.host.request('evaluation_boards')['Boards'];atomic_json(path.with_name(path.stem+'-boards.json'),self.last_boards)
        return report
