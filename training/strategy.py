"""Search-guided expert iteration. Only completed runs supply terminal value targets."""
from collections import defaultdict,deque
import concurrent.futures
from pathlib import Path
import argparse
import copy
import hashlib
import json
import os
import math
import random
import subprocess
import time
import numpy as np
import torch
from torch import nn
from policy import Policy
from learner import atomic_json,pad_observations,padded,wilson
from persistence import output_writer,append_metrics

VERSION=1

def implementation_hash():
    digest=hashlib.sha256()
    for name in ('strategy.py','policy.py','persistence.py','learner.py'):
        digest.update(Path(__file__).with_name(name).read_bytes())
    return digest.hexdigest()

class StrategyHost:
    def __init__(self,config,output,suite='training'):
        self.config=config;self.output=Path(output);self.output.mkdir(parents=True,exist_ok=True)
        self.model=None;self.device='cpu'
        history=config.get('history',str(Path(config['output'])/'history'))
        command=['dotnet',config['host'],'--strategy',config['rules'],config['knowledge'],str(self.output),str(config['seed']),suite,config.get('user_pool',''),str(config.get('observation_encoding',5)),'no-wiki' if config.get('disable_wiki') else 'wiki',history]
        self.errors=(self.output/'host-errors.log').open('a',encoding='utf-8')
        self.reader=concurrent.futures.ThreadPoolExecutor(max_workers=1)
        self.process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.errors,text=True,encoding='utf-8',creationflags=0x08000000 if os.name=='nt' else 0)
        try:self.ready=self.read()
        except BaseException:self.close();raise
    def read(self):
        while True:
            pending=self.reader.submit(self.process.stdout.readline)
            try:line=pending.result(timeout=self.config.get('host_timeout_seconds',120))
            except concurrent.futures.TimeoutError:
                self.process.kill();self.process.wait(timeout=10);raise TimeoutError('Strategy host timed out; see '+str(self.output/'host-errors.log'))
            if not line:raise RuntimeError('Strategy host exited; see '+str(self.output/'host-errors.log'))
            result=json.loads(line)
            if 'Error' in result:raise RuntimeError(result['Error'])
            if 'Inference' not in result:return result
            if self.model is None:raise RuntimeError('Search requested a missing model.')
            self.send(self.infer(result['Inference']))
    def send(self,value):self.process.stdin.write(json.dumps(value)+'\n');self.process.stdin.flush()
    def request(self,command,**values):self.send(dict(command=command,**values));return self.read()
    def infer(self,requests):
        predictions=[]
        # Shape bucketing limits padding while preserving response order.
        predictions=[None]*len(requests)
        ordered=sorted(range(len(requests)),key=lambda i:len(requests[i]['Observation']))
        for start in range(0,len(ordered),16):
            indices=ordered[start:start+16];rows=[requests[i] for i in indices]
            observations=torch.as_tensor(pad_observations([r['Observation'] for r in rows]),device=self.device)
            actions,mask=padded([r['Actions'] for r in rows],self.device)
            analysis=torch.as_tensor(np.asarray([analysis_input(r['Analysis'],not self.config.get('disable_analysis',False)) for r in rows],np.float32),device=self.device)
            self.model.eval()
            with torch.inference_mode():distribution,value=self.model(observations,actions,mask,analysis)
            probs=distribution.probs.cpu().tolist();values=value.sigmoid().cpu().tolist()
            for index,row,prior,estimate in zip(indices,rows,probs,values):predictions[index]={'Priors':prior[:len(row['Actions'])],'Value':estimate}
        return predictions
    def close(self):
        if hasattr(self,'process'):
            if self.process.poll() is None:
                try:self.send({'command':'stop'});self.process.wait(timeout=5)
                except (OSError,subprocess.TimeoutExpired):self.process.kill();self.process.wait(timeout=10)
            for stream in (self.process.stdin,self.process.stdout):stream.close()
        if hasattr(self,'errors'):self.errors.close()
        if hasattr(self,'reader'):self.reader.shutdown(wait=True,cancel_futures=True)
    def __enter__(self):return self
    def __exit__(self,*args):self.close()

def analysis_input(features,enabled=True):
    present=enabled and any(features)
    return ([1.]+list(features)) if present else [0.]*29

def balanced_sample(replay,count):
    groups=defaultdict(list)
    for row in replay:groups[(row['trainer'],min(2,(row['day']-1)//4),row['outcome'])].append(row)
    keys=list(groups)
    return [random.choice(groups[random.choice(keys)]) for _ in range(count)]

def optimize(model,optimizer,replay,device,steps=8,batch=32,disable_analysis=False):
    if not replay:return {}
    model.train();losses=[]
    for _ in range(steps):
        rows=balanced_sample(replay,min(batch,len(replay)))
        # Never silently truncate observations or legal candidates.
        obs=torch.as_tensor(pad_observations([r['observation'] for r in rows]),device=device)
        actions,mask=padded([r['actions'] for r in rows],device)
        targets=torch.zeros(mask.shape,device=device)
        for i,row in enumerate(rows):targets[i,:len(row['visits'])]=torch.tensor(row['visits'],device=device)
        targets=targets/targets.sum(-1,keepdim=True).clamp(min=1e-8)
        damage=torch.as_tensor(np.stack([r['analysis'] for r in rows]),dtype=torch.float32,device=device)
        # Mask analysis on half the examples so auxiliary prediction cannot merely copy its input.
        provided=torch.tensor([analysis_input(r['analysis'],not disable_analysis and random.random()<.5) for r in rows],dtype=torch.float32,device=device)
        distribution,value=model(obs,actions,mask,provided)
        imitation=-(targets*distribution.logits.masked_fill(~mask,0)).sum(-1).mean()
        outcome=torch.tensor([r['outcome'] for r in rows],dtype=torch.float32,device=device)
        value_loss=nn.functional.binary_cross_entropy_with_logits(value,outcome)
        battle=torch.tensor([r.get('battle',0.) for r in rows],dtype=torch.float32,device=device)
        battle_loss=nn.functional.binary_cross_entropy_with_logits(model.predicted_battle,battle)
        available=(damage.abs().sum(-1)>0).float()
        auxiliary=((model.predicted_analysis-damage).square().mean(-1)*available).sum()/available.sum().clamp(min=1)
        loss=imitation+value_loss+.2*battle_loss+(.1*auxiliary if not disable_analysis else 0)
        optimizer.zero_grad(set_to_none=True);loss.backward();nn.utils.clip_grad_norm_(model.parameters(),1);optimizer.step()
        losses.append(float(loss.detach()))
    return {'loss':sum(losses)/len(losses),'steps':steps}

def default_config(config):
    root=Path(__file__).resolve().parent.parent
    defaults=dict(engine='strategy',seed=69,device='auto',workers=8,environments=8,minibatch=32,minutes=0,resume=False,
        host=str(root/'tools/BatomonHelper.TrainingHost/bin/Release/net10.0/BatomonHelper.TrainingHost.dll'),rules=str(root/'assets/simulation/rules.json'),
        knowledge=str(root/'assets/simulation/strategy-knowledge.json'),output=str(root/'work/strategy-training'),user_pool=str(root/'work/user-sessions'),
        search_seconds=.15,search_simulations=32,search_batch=8,evaluation_search_seconds=2.,evaluation_search_simulations=256,
        warmup_runs=32,replay_capacity=50000,optimization_steps=8,batch=32,checkpoint_runs=4,analysis_samples=4)
    result=dict(defaults,**config)
    for name in ('search_seconds','evaluation_search_seconds'):
        if not math.isfinite(result[name]) or not 0<result[name]<=30:raise ValueError(name+' must be positive and at most 30 seconds.')
    for name in ('search_simulations','evaluation_search_simulations','search_batch','batch','optimization_steps','replay_capacity','checkpoint_runs'):
        if not isinstance(result[name],int) or result[name]<1:raise ValueError(name+' must be a positive integer.')
    if result['warmup_runs']<0 or result['minutes']<0:raise ValueError('Warmup runs and minutes cannot be negative.')
    if result['seed']<0 or result['seed']>=2**64:raise ValueError('Seed must fit an unsigned 64-bit integer.')
    return result

def save(path,value):
    path=Path(path);staged=path.with_suffix('.tmp');torch.save(value,staged)
    for attempt in range(8):
        try:os.replace(staged,path);return
        except PermissionError:
            if attempt==7:raise
            time.sleep(.02*(attempt+1))

def load(path):return torch.load(path,map_location='cpu',weights_only=False)

def tensor_hash(weights):
    digest=hashlib.sha256()
    for name,tensor in sorted(weights.items()):
        digest.update(name.encode());digest.update(str(tuple(tensor.shape)).encode());digest.update(tensor.detach().cpu().contiguous().numpy().tobytes())
    return digest.hexdigest()

def weight_snapshot(output,snapshot=None):
    if snapshot:return Path(snapshot)
    output=Path(output);manifest=json.loads((output/'checkpoint.json').read_text())
    light=output/f"strategy-{manifest['update']}.pt"
    return light if light.exists() else output/'strategy-checkpoint.pt'

def control(output):
    try:return json.loads((Path(output)/'control.json').read_text(encoding='utf-8')).get('command','run')
    except (OSError,json.JSONDecodeError):return 'run'

def train(config):
    config=default_config(config);output=Path(config['output']);output.mkdir(parents=True,exist_ok=True)
    random.seed(config['seed']);np.random.seed(config['seed']);torch.manual_seed(config['seed'])
    torch.set_num_threads(min(8,os.cpu_count() or 1))
    device='cuda' if config['device']!='cpu' and torch.cuda.is_available() else 'cpu'
    if config['device']=='cuda' and device!='cuda':raise RuntimeError('CUDA was requested but is unavailable.')
    atomic_json(output/'configuration.json',config)
    atomic_json(output/'progress.json',dict(status='Preparing strategic training'))
    with StrategyHost(config,output/'host') as host:
        model=Policy(host.ready['ObservationWidth'],host.ready['Species'],version=6).to(device)
        optimizer=torch.optim.AdamW(model.parameters(),lr=3e-4);host.model=model;host.device=device
        replay=deque(maxlen=config['replay_capacity']);trajectory=[];runs=0;update=0;successes=0;wins=0;forced=0;battles=0;decisions=0;recent=deque(maxlen=2000);trainer_stats={};generation_stats={};deployed=0;bench=0
        if config.get('resume'):
            saved=load(output/'strategy-checkpoint.pt')
            if saved['identity']!=host.ready['Identity'] or saved.get('code')!=implementation_hash():raise RuntimeError('Rules, code, knowledge or opponent suite changed; start a fresh output directory.')
            model.load_state_dict(saved['model']);optimizer.load_state_dict(saved['optimizer']);replay.extend(saved['replay']);trajectory=saved['trajectory']
            runs=saved['runs'];update=saved['update'];successes=saved['successes'];wins=saved['wins'];recent.extend(saved['recent']);decisions=saved['decisions'];forced=saved['forced'];battles=saved['battles']
            trainer_stats=saved.get('trainers',{});generation_stats=saved.get('generations',{});deployed=saved.get('deployed',0);bench=saved.get('bench',0)
            random.setstate(saved['random']);np.random.set_state(saved['numpy']);torch.set_rng_state(saved['torch'])
            if device=='cuda' and saved.get('cuda') is not None:torch.cuda.set_rng_state_all(saved['cuda'])
            host.request('restore',checkpoint=saved['host'])
        elif (output/'strategy-checkpoint.pt').exists():raise RuntimeError('Output contains a strategy checkpoint; resume it or choose a new directory.')
        state=host.request('observe');started=time.monotonic();launch_decisions=decisions;last_save_runs=runs;last_progress=0.;last_loss={}
        def checkpoint():
            saved=dict(version=VERSION,model_version=6,code=implementation_hash(),identity=host.ready['Identity'],model=copy.deepcopy(model.state_dict()),optimizer=optimizer.state_dict(),
                replay=list(replay),trajectory=trajectory,runs=runs,update=update,successes=successes,wins=wins,recent=list(recent),decisions=decisions,forced=forced,battles=battles,trainers=trainer_stats,generations=generation_stats,deployed=deployed,bench=bench,
                random=random.getstate(),numpy=np.random.get_state(),torch=torch.get_rng_state(),cuda=torch.cuda.get_rng_state_all() if device=='cuda' else None,
                host=host.request('checkpoint'),config=config,width=host.ready['ObservationWidth'],species=host.ready['Species'])
            save(output/'strategy-checkpoint.pt',saved)
            # Old generation files stay separate; a snapshot is an actual completed training update.
            save(output/f'strategy-{update}.pt',dict((k,v) for k,v in saved.items() if k not in ('replay','trajectory','optimizer','host')))
            for old in output.glob('strategy-*.pt'):
                if old.stem[9:].isdigit() and int(old.stem[9:])<update-19:old.unlink()
            atomic_json(output/'checkpoint.json',dict(engine='strategy',update=update,runs=runs,path=str(output/'strategy-checkpoint.pt')))
        while True:
            command=control(output)
            if command=='stop' or config['minutes']>0 and time.monotonic()-started>=config['minutes']*60:break
            if command=='pause':
                checkpoint();atomic_json(output/'progress.json',dict(status='Paused',runs=runs,update=update))
                while control(output)=='pause':time.sleep(.25)
                continue
            if runs<config['warmup_runs']:
                action=host.request('teacher')['Action']
                if action not in state['Actions']:action=host.request('baseline')['Action']
                if action not in state['Actions']:action=next((a for a in state['Actions'] if a['Kind']==8),state['Actions'][0])
                index=state['Actions'].index(action)
                features=host.request('analysis',samples=config['analysis_samples'])['Features'] if any(a['Kind']==8 for a in state['Actions']) else [0.]*28
                visits=[float(i==index) for i in range(len(state['Actions']))]
                row=dict(observation=state['Observation'],actions=state['Features'],analysis=features,visits=visits)
            else:
                plan=host.request('plan',neural=True,options=dict(Seconds=config['search_seconds'],Simulations=config['search_simulations'],BatchSize=config['search_batch'],Seed=config['seed']+decisions,AnalysisSamples=config['analysis_samples']))
                action=plan['Action'];row=dict(observation=plan['Observation'],actions=plan['Actions'],analysis=plan['Analysis'],visits=plan['Visits'])
            for key in ('observation','actions','analysis','visits'):row[key]=np.asarray(row[key],dtype=np.float32)
            row.update(day=state['Day'],trainer=state['Trainer']);trajectory.append(row)
            limit=state['Decisions']>=64;result=host.request('step',action=action);decisions+=1;state=result['State']
            if result['Battle'] is not None:
                battles+=1;forced+=int(limit);deployed+=result['Battle']['Deployed'];bench+=result['Battle']['UnusedBench']
                for pending in reversed(trajectory):
                    if 'battle' in pending:break
                    pending['battle']=float(result['Battle']['PlayerWon'])
            if result['Terminal']:
                outcome=float(result['Reward']);runs+=1;successes+=int(outcome);wins+=state['Wins'];recent.append((outcome,state['Wins']))
                for bucket in (trainer_stats.setdefault(state['Trainer'],dict(runs=0,successes=0,wins=0)),generation_stats.setdefault(str(update),dict(runs=0,successes=0,wins=0))):
                    bucket['runs']+=1;bucket['successes']+=int(outcome);bucket['wins']+=state['Wins']
                for pending in trajectory:pending['outcome']=outcome;replay.append(pending)
                trajectory=[]
                last_loss=optimize(model,optimizer,replay,device,config['optimization_steps'],config['batch'],config.get('disable_analysis',False));update+=1
                host.request('generation',value=update);state=host.request('reset')
                if runs-last_save_runs>=config['checkpoint_runs']:checkpoint();last_save_runs=runs
            if time.monotonic()-last_progress>1:
                last_progress=time.monotonic();elapsed=time.monotonic()-started
                progress=dict(status='Strategic training',engine='strategy',update=update,runs=runs,successes=successes,success_rate=successes/max(1,runs),average_wins=wins/max(1,runs),
                    recent_runs=len(recent),recent_success_rate=sum(r[0] for r in recent)/max(1,len(recent)),recent_average_wins=sum(r[1] for r in recent)/max(1,len(recent)),
                    decisions=decisions,decisions_per_second=(decisions-launch_decisions)/max(1,elapsed),limited_turn_rate=forced/max(1,battles),replay=len(replay),warmup=runs<config['warmup_runs'],device=device,optimization=last_loss,
                    trainers=trainer_stats,generations=generation_stats,average_deployed=deployed/max(1,battles),unused_deployable_bench=bench/max(1,battles),
                    checkpoint=str(output/'strategy-checkpoint.pt') if (output/'strategy-checkpoint.pt').exists() else 'Not saved yet',
                    model_version=6,diagnostic_fixture=json.loads(Path(config['rules']).read_text(encoding='utf-8-sig'))['Build']=='fixture')
                atomic_json(output/'progress.json',progress);append_metrics(output,progress)
        checkpoint();atomic_json(output/'progress.json',dict(status='Stopped and saved',engine='strategy',runs=runs,update=update,success_rate=successes/max(1,runs),
            recent_runs=len(recent),recent_success_rate=sum(r[0] for r in recent)/max(1,len(recent)),recent_average_wins=sum(r[1] for r in recent)/max(1,len(recent)),
            trainers=trainer_stats,generations=generation_stats,average_deployed=deployed/max(1,battles),unused_deployable_bench=bench/max(1,battles),limited_turn_rate=forced/max(1,battles),checkpoint=str(output/'strategy-checkpoint.pt')))

def evaluate(config,runs=128,suite='development',variant='search',snapshot=None):
    config=default_config(config);output=Path(config['output']);saved=load(weight_snapshot(output,snapshot))
    device='cuda' if config['device']!='cpu' and torch.cuda.is_available() else 'cpu';torch.set_num_threads(min(8,os.cpu_count() or 1))
    torch.backends.cuda.matmul.allow_tf32=False;torch.backends.cudnn.allow_tf32=False
    model=Policy(saved['width'],saved['species'],version=6).to(device);model.load_state_dict(saved['model']);model.eval()
    legacy=None;legacy_transfer=False
    if variant.startswith('legacy-'):
        legacy_path=Path(config['legacy_checkpoint']);legacy_root=legacy_path if legacy_path.is_dir() else legacy_path.parent.parent
        if legacy_path.is_dir():
            manifest=json.loads((legacy_path/'checkpoint.json').read_text());legacy_path=legacy_path/manifest['directory']/'learner.pt'
        legacy=load(legacy_path);config=dict(config,observation_encoding=legacy['encoding'])
        if legacy['rules_hash']!=saved['identity']['Rules']:
            archive_rules=legacy_root/'history'/f"rules-{legacy['rules_hash']}.json"
            if not archive_rules.exists():raise RuntimeError('Legacy rules differ and their archived identity mapping is unavailable.')
            old=json.loads(archive_rules.read_text(encoding='utf-8-sig'));current=json.loads(Path(config['rules']).read_text(encoding='utf-8-sig'))
            for category in ('Species','Items','Trinkets','Trainers'):
                if [x['Id'] for x in old[category]]!=[x['Id'] for x in current[category]]:raise RuntimeError('Legacy identity mapping changed: '+category)
            legacy_transfer=True
    results=[];decisions=0;forced=0;battles=0;deployed=0;bench=0;started=time.monotonic()
    with StrategyHost(config,output/f'evaluation-{suite}-{variant}',suite) as host:
        if host.ready['Identity']['Suite']!=suite:raise RuntimeError('Host launched the wrong evaluation suite.')
        if saved['identity']['Rules']!=host.ready['Identity']['Rules'] or saved['identity']['Knowledge']!=host.ready['Identity']['Knowledge']:raise RuntimeError('Evaluation rules or knowledge differ from model.')
        host.model=model;host.device=device
        host.request('generation',value=saved['update'])
        model_hash=tensor_hash(legacy['policies'][int(variant.split('-')[1])] if legacy is not None else saved['model'])
        cache_identity=dict(identity=host.ready['Identity'],model=model_hash,variant=variant,runs=runs,code=implementation_hash(),
            seconds=config['evaluation_search_seconds'],simulations=config['evaluation_search_simulations'],analysis=config['analysis_samples'],disable_analysis=config.get('disable_analysis',False),device=device,precision='float32-no-tf32',torch=torch.__version__,cuda=torch.version.cuda)
        cache=output/'strategy-evaluation-cache';cache.mkdir(exist_ok=True)
        cache_path=cache/(hashlib.sha256(json.dumps(cache_identity,sort_keys=True).encode()).hexdigest()+'.json')
        if cache_path.exists():
            cached=json.loads(cache_path.read_text());cached['cached']=True
            atomic_json(output/f'evaluation-{suite}-{variant}.json',cached);atomic_json(output/'evaluation.json',cached)
            atomic_json(output/'progress.json',dict(cached,status='Evaluated',recent_runs=cached['runs'],recent_success_rate=cached['success_rate'],recent_average_wins=cached['average_wins']));return cached
        if legacy is not None:
            from legacy_policy import LegacyPolicy
            legacy_model=(LegacyPolicy(host.ready['ObservationWidth'],host.ready['Species']) if legacy['encoding']==4 else Policy(host.ready['ObservationWidth'],host.ready['Species'],version=legacy['model_version'])).to(device)
            legacy_model.load_state_dict(legacy['policies'][int(variant.split('-')[1])]);legacy_model.eval()
        for scenario in range(runs):
            if control(output)=='stop':break
            state=host.request('reset',episode=scenario);scenario_decisions=0;remaining=30.
            while state['Actions']:
                if control(output)=='stop':break
                if legacy is not None:
                    from policy import greedy_actions
                    obs=torch.tensor([state['Observation']],dtype=torch.float32,device=device);features,mask=padded([state['Features']],device)
                    with torch.inference_mode():distribution,_=legacy_model(obs,features,mask)
                    index=int(greedy_actions(distribution,features,mask,legacy=legacy['encoding']==4)[0]);action=state['Actions'][index]
                elif variant=='baseline':action=host.request('baseline')['Action']
                elif variant=='policy' or remaining<=.01:
                    features=host.request('analysis',samples=config['analysis_samples'])['Features']
                    prediction=host.infer([dict(Observation=state['Observation'],Actions=state['Features'],Analysis=features)])[0]
                    action=state['Actions'][int(np.argmax(prediction['Priors']))]
                else:
                    plan=host.request('plan',neural=variant!='scripted-search',options=dict(Seconds=min(remaining,config['evaluation_search_seconds']),Simulations=config['evaluation_search_simulations'],Seed=config['seed']+scenario*100003+scenario_decisions,AnalysisSamples=config['analysis_samples']))
                    action=plan['Action'];remaining=max(0,remaining-plan['Seconds'])
                limited=state['Decisions']>=64;result=host.request('step',action=action);state=result['State'];decisions+=1;scenario_decisions+=1
                if result['Battle'] is not None:battles+=1;forced+=int(limited);remaining=30.;deployed+=result['Battle']['Deployed'];bench+=result['Battle']['UnusedBench']
                if result['Terminal']:
                    results.append(dict(scenario=scenario,success=result['Reward']>0,wins=state['Wins'],trainer=state['Trainer'],days=state['Day']));break
            atomic_json(output/'progress.json',dict(status='Evaluating',snapshot=variant,runs=len(results),target=runs,elapsed_seconds=time.monotonic()-started,runs_per_second=len(results)/max(.001,time.monotonic()-started)))
        successes=sum(r['success'] for r in results)
        trainer_stats={}
        for row in results:
            totals=trainer_stats.setdefault(row['trainer'],dict(runs=0,successes=0,wins=0));totals['runs']+=1;totals['successes']+=int(row['success']);totals['wins']+=row['wins']
        report=dict(label=variant,suite=suite,identity=host.ready['Identity'],model_hash=model_hash,code=implementation_hash(),trained_code=saved.get('code'),runs=len(results),target=runs,completed=len(results)==runs,
            legacy_rules_transfer=legacy_transfer,legacy_rules_hash=legacy['rules_hash'] if legacy is not None else None,matchups=host.request('matchups'),
            trainers=trainer_stats,average_deployed=deployed/max(1,battles),unused_deployable_bench=bench/max(1,battles),
            success_rate=successes/max(1,len(results)),confidence_interval_95=wilson(successes,len(results)),average_wins=sum(r['wins'] for r in results)/max(1,len(results)),limited_turn_rate=forced/max(1,battles),scenarios=results,
            search_seconds=config['evaluation_search_seconds'],search_simulations=config['evaluation_search_simulations'],decisions=decisions,seconds=time.monotonic()-started)
    atomic_json(output/f'evaluation-{suite}-{variant}.json',report);atomic_json(output/'evaluation.json',report)
    if report['completed']:atomic_json(cache_path,report)
    atomic_json(output/'progress.json',dict(report,status='Evaluated' if report['completed'] else 'Cancelled',recent_runs=len(results),recent_success_rate=report['success_rate'],recent_average_wins=report['average_wins']))
    return report

def export(config,snapshot=None):
    config=default_config(config);output=Path(config['output']);saved=load(weight_snapshot(output,snapshot));destination=output/'export';destination.mkdir(exist_ok=True)
    save(destination/'strategy-policy.pt',dict(model=saved['model'],model_version=6,width=saved['width'],species=saved['species']))
    for source,name in ((config['rules'],'rules.json'),(config['knowledge'],'knowledge.json')):
        import shutil
        if Path(source).exists():shutil.copy2(source,destination/name)
    atomic_json(destination/'manifest.json',dict(version=1,model_version=6,encoding=5,analysis_version=1,identity=saved['identity'],code=saved.get('code'),config=config,
        inference='training/strategy.py with Policy(version=6); action candidates are scored jointly',analysis_features=28,analysis_input='presence flag followed by 28 features',
        analysis_schema=dict(checkpoints=[0,7.5,15,22.5,30],per_checkpoint=['log1p(DPS)/10','log1p(damage)/10','log1p(Burn)/10','log1p(Poison)/10','log1p(Shock)/10'],tail=['reach probability','median successful seconds/30 (1 if none)','log1p(damage30/targetHP)'])))

def rank(config,runs=128,suite='development'):
    output=Path(config['output']);rows=[]
    snapshots=sorted((p for p in output.glob('strategy-*.pt') if p.stem[9:].isdigit()),key=lambda p:int(p.stem[9:]))
    for snapshot in snapshots:
        if control(output)=='stop':break
        report=evaluate(config,runs,suite,snapshot=snapshot);update=int(snapshot.stem[9:])
        rows.append(dict(report,label=f'Strategic update {update}',update=update,generation=update,archive=None,baseline=False,
            average_deployed=report['average_deployed'],unused_deployable_bench=report['unused_deployable_bench']))
        ordered=sorted(rows,key=lambda r:r['confidence_interval_95'][0],reverse=True)
        for index,row in enumerate(ordered):row['rank']=index+1
        atomic_json(output/'generation-ranking.json',dict(status='Ranking generations',snapshots=ordered,completed=len(rows),total=len(snapshots)))
    status='Ranked' if len(rows)==len(snapshots) else 'Cancelled'
    atomic_json(output/'generation-ranking.json',dict(status=status,snapshots=ordered if rows else [],completed=len(rows),total=len(snapshots)))
    atomic_json(output/'progress.json',dict(status=status,snapshots=ordered if rows else [],completed=len(rows),total=len(snapshots)))

def advise(config,position,result_path,seconds=30):
    config=default_config(dict(config,history=str(Path(result_path).parent/'advisor-history')));saved=load(weight_snapshot(config['output']))
    device='cuda' if config['device']!='cpu' and torch.cuda.is_available() else 'cpu'
    torch.set_num_threads(min(8,os.cpu_count() or 1));model=Policy(saved['width'],saved['species'],version=6).to(device);model.load_state_dict(saved['model'])
    with StrategyHost(config,Path(result_path).parent/'advisor-host','development') as host:
        if saved['identity']['Rules']!=host.ready['Identity']['Rules'] or saved['identity']['Knowledge']!=host.ready['Identity']['Knowledge']:raise RuntimeError('Advisor rules or knowledge differ from model.')
        host.model=model;host.device=device
        host.request('position',observation=json.loads(Path(position).read_text(encoding='utf-8-sig')))
        result=host.request('plan',neural=True,options=dict(Seconds=seconds,Simulations=100000,BatchSize=8,Seed=config['seed'],AnalysisSamples=4))
        atomic_json(Path(result_path),result)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);parser.add_argument('--evaluate',action='store_true');parser.add_argument('--export',action='store_true')
    parser.add_argument('--runs',type=int,default=128);parser.add_argument('--suite',choices=['development','final'],default='development');parser.add_argument('--variant',choices=['search','policy','baseline','scripted-search','legacy-0','legacy-1','legacy-2','legacy-3'],default='search');parser.add_argument('--snapshot')
    parser.add_argument('--rank-generations',action='store_true');parser.add_argument('--export-update',type=int);parser.add_argument('--export-archive');parser.add_argument('--advise');parser.add_argument('--result');parser.add_argument('--thinking-seconds',type=float,default=30)
    args=parser.parse_args();config=default_config(json.loads(Path(args.config).read_text(encoding='utf-8-sig')))
    if args.runs<1:parser.error('--runs must be positive')
    if args.advise:
        if not args.result:parser.error('--advise requires --result')
        if not 0<args.thinking_seconds<=30:parser.error('Thinking time must be between 0 and 30 seconds.')
        advise(config,args.advise,args.result,args.thinking_seconds);return
    if args.export_archive:raise RuntimeError('Open the legacy archive output directory and select Legacy PPO to export that model.')
    if args.export_update is not None:args.snapshot=str(Path(config['output'])/f'strategy-{args.export_update}.pt')
    with output_writer(config['output']):
        try:
            if args.rank_generations:rank(config,args.runs,args.suite)
            elif args.evaluate:evaluate(config,args.runs,args.suite,args.variant,args.snapshot)
            elif args.export:export(config,args.snapshot)
            else:train(config)
        except Exception as error:atomic_json(Path(config['output'])/'progress.json',dict(status='Failed',error=str(error)));raise

if __name__=='__main__':main()
