"""Standalone population PPO; .NET owns gameplay and numeric batch publication."""
from __future__ import annotations
import argparse
from collections import deque
import copy
import json
import os
from pathlib import Path
import random
import re
import subprocess
import time
import uuid
import hashlib
import concurrent.futures
import threading
import shutil

import numpy as np
import torch
from torch import nn
from torch.distributions import Categorical

PROTOCOL = 2
ENCODING = 5
ACTION_WIDTH = 40
TOKEN_WIDTH = 64
MODEL_VERSION = 5
OBJECTIVE_VERSION = 4

def atomic_json(path, value):
    path = Path(path)
    staged = path.with_name(path.name + '.tmp')
    staged.write_text(json.dumps(value), encoding='utf-8')
    for attempt in range(6):
        try:os.replace(staged,path);break
        except PermissionError:
            if attempt==5:raise
            time.sleep(.01*(attempt+1))

class Host:
    def __init__(self, config, output):
        self.keep_buffer=config.get('keep_numeric_buffer',False)
        self.output = Path(output);self.output.mkdir(parents=True,exist_ok=True)
        self.buffer = self.output / 'numeric-batch.bin'
        self.process = subprocess.Popen(['dotnet', config['host'], '--serve', config['rules'], str(self.buffer),
                                         str(config['environments']), str(config['workers']), str(config['seed']),
                                         str(config.get('history', self.output / 'history')), 'evaluation-initial' if config.get('initial_evaluation') else 'evaluation' if config.get('evaluation') else 'training',
                                         'tune' if config.get('auto_tune_workers',True) else 'fixed',
                                         'tune-environments' if config.get('auto_tune_environments',True) and not config.get('resume') else 'fixed-environments',config.get('session','legacy'),
                                         str(config.get('shop_decision_limit',64)),str(config.get('snapshot_update',-1)),str(config.get('evaluation_batch','none')),
                                         str(config.get('snapshot_generation',-1)),str(config.get('encoding',ENCODING)),
                                         'curriculum' if config.get('curriculum',True) else 'normal',str(config.get('user_pool',Path(config['rules']).parent.parent.parent/'work'/'user-sessions'))], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=0x08000000 if os.name == 'nt' else 0)
        self.timeout=float(config.get('host_timeout_seconds',120))
        self._reader=concurrent.futures.ThreadPoolExecutor(max_workers=1)
        self.errors=deque(maxlen=80)
        def drain_errors():
            for line in self.process.stderr:self.errors.append(line.rstrip())
        self._error_thread=threading.Thread(target=drain_errors,daemon=True);self._error_thread.start()
        try:
            self.ready=self._read()
            if self.ready.get("Encoding")!=config.get("encoding",ENCODING):raise RuntimeError("Incompatible numeric protocol encoding.")
            if self.ready.get('TrainingObjective')!=OBJECTIVE_VERSION:raise RuntimeError('Rebuild the training host for the current learning objective.')
            self.map=np.memmap(self.buffer,mode='r',dtype=np.uint8)
        except BaseException:self.close(force=True);raise
    def _read(self):
        future=self._reader.submit(self.process.stdout.readline)
        try:line=future.result(timeout=self.timeout)
        except concurrent.futures.TimeoutError:
            self.process.kill();self.process.wait(timeout=10)
            raise TimeoutError(f'Simulation host timed out after {self.timeout:g}s. '+"\n".join(self.errors))
        if not line:raise RuntimeError(f'Simulation host exited ({self.process.poll()}). '+"\n".join(self.errors))
        try:result=json.loads(line)
        except json.JSONDecodeError:raise RuntimeError('Simulation host returned invalid protocol: '+line.rstrip()+"\n"+"\n".join(self.errors))
        if 'Error'  in result:
            raise RuntimeError(result['Error'])
        return result
    def request(self, command, **values):
        if command=='step':values.setdefault('revision',self.revision)
        self.process.stdin.write(json.dumps(dict(command=command, **values)) + '\n')
        self.process.stdin.flush()
        return self._read()
    def batch(self):
        shape = self.request('batch');self.revision=shape['Revision'];self.scenarios=shape.get('ScenarioIds');self.days=shape.get('Days');self.ordinals=shape.get('DecisionOrdinals')
        count=shape['Count']
        if shape.get('Layout')!='ragged':raise RuntimeError('Rebuild the host for ragged publication protocol 2.')
        lengths=np.frombuffer(self.map,np.int32,count).copy();offset=count*4
        flat_obs=np.frombuffer(self.map,np.float32,int(lengths.sum()),offset=offset).copy();offset+=len(flat_obs)*4
        observations=[];start=0
        for n in lengths:observations.append(flat_obs[start:start+n]);start+=n
        counts=np.frombuffer(self.map,np.int32,count,offset=offset).copy();offset+=count*4
        # Copy the packed publication once; row slices remain zero-copy views of this owned array.
        flat = np.frombuffer(self.map, np.float32, int(counts.sum()) * ACTION_WIDTH, offset=offset).reshape(-1, ACTION_WIDTH).copy()
        actions = []
        start = 0
        for n in counts:
            actions.append(flat[start:start + n])
            start += n
        return observations, actions
    def close(self,force=False):
        try:
            if self.process.poll() is None and not force:
                self.request('stop');self.process.wait(timeout=10)
        finally:
            if self.process.poll() is None:self.process.kill();self.process.wait(timeout=10)
            if hasattr(self,'map'):self.map._mmap.close()
            self._reader.shutdown(wait=True,cancel_futures=True)
            self._error_thread.join(timeout=2)
            for stream in (self.process.stdin,self.process.stdout,self.process.stderr):stream.close()
            if not self.keep_buffer and self.buffer.exists():self.buffer.unlink()


def pad_observations(rows):
    width=max(map(len,rows));result=np.zeros((len(rows),width),np.float32)
    for i,row in enumerate(rows):result[i,:len(row)]=row
    return result

def padded_arrays(actions):
    maximum=max(map(len,actions));values=np.zeros((len(actions),maximum,ACTION_WIDTH),dtype=np.float32);mask=np.zeros((len(actions),maximum),dtype=np.bool_)
    for i,row in enumerate(actions):values[i,:len(row)]=row;mask[i,:len(row)]=True
    return values,mask

def padded(actions, device):
    values,mask=padded_arrays(actions)
    return torch.as_tensor(values, device=device), torch.as_tensor(mask, device=device)

from legacy_policy import LegacyPolicy
from policy import Policy, greedy_actions

class PopulationInference:
    """One device transfer and batched forward for four independent policies."""
    def __init__(self,policies):
        self.device=next(policies[0].parameters()).device
        params,self.buffers=torch.func.stack_module_state(policies)
        self.params={name:value.detach() for name,value in params.items()}
        self.model=policies[0]
        self.token_width=12 if isinstance(self.model,LegacyPolicy) else TOKEN_WIDTH
        def forward(params,buffers,obs,features,mask):
            distribution,value=torch.func.functional_call(self.model,(params,buffers),(obs,features,mask))
            return distribution.logits,value,distribution.kind_logits if hasattr(distribution,'kind_logits') else torch.zeros(len(obs),9,device=obs.device)
        self.forward=torch.vmap(forward,in_dims=(0,0,0,0,0))

    def __call__(self,observations,actions):
        prepared,_,_=self.prepare(observations,actions)
        return self.infer(prepared)

    def prepare(self,observations,actions):
        packing_start=time.perf_counter();count=len(observations);maximum=max(map(len,actions));bins={}
        core=self.model.core_width
        for i,row in enumerate(actions):
            tokens=int(observations[i][core]) if len(observations[i])>core else 0
            key=(0,0) if count<=128 else (max(1,len(row)).bit_length()//2,max(1,tokens).bit_length()//2)
            bins.setdefault(key,[]).append(i)
        batches=[];transfer_seconds=0.
        for members in bins.values():
            groups=[[i for i in members if i%4==p] for p in range(4)];size=max(map(len,groups))
            indices=[groups[p][j] if j<len(groups[p]) else -1 for j in range(size) for p in range(4)]
            token_count=max((int(observations[i][core]) for i in members),default=0) if len(observations[0])>core else 0
            width=core+1+token_count*self.token_width if len(observations[0])>core else core
            rows=np.zeros((size*4,width),np.float32);candidate_rows=[]
            for j,i in enumerate(indices):
                if i>=0:rows[j,:min(width,len(observations[i]))]=observations[i][:width];candidate_rows.append(actions[i])
                else:
                    dummy=np.zeros((1,ACTION_WIDTH),np.float32);dummy[0,8]=1;candidate_rows.append(dummy)
                    if width>core+1:rows[j,core]=1
            features,mask=padded_arrays(candidate_rows);n=features.shape[1];transfer_start=time.perf_counter()
            obs=torch.as_tensor(rows,device=self.device).reshape(size,4,-1).transpose(0,1)
            tensor=torch.as_tensor(features,device=self.device).reshape(size,4,n,ACTION_WIDTH).transpose(0,1)
            valid=torch.as_tensor(mask,device=self.device).reshape(size,4,n).transpose(0,1)
            transfer_seconds+=time.perf_counter()-transfer_start
            batches.append((obs,tensor,valid,indices,n))
        return (batches,count,maximum),time.perf_counter()-packing_start-transfer_seconds,transfer_seconds

    def infer(self,prepared):
        batches,count,maximum=prepared
        result=torch.full((count,maximum),-torch.inf,device=self.device);values=torch.zeros(count,device=self.device);kind_result=torch.zeros(count,9,device=self.device)
        for obs,features,mask,indices,n in batches:
            logits,value,kind=self.forward(self.params,self.buffers,obs,features,mask)
            logits=logits.transpose(0,1).reshape(-1,n);value=value.transpose(0,1).reshape(-1);kind=kind.transpose(0,1).reshape(-1,9)
            local=[j for j,i in enumerate(indices) if i>=0];original=[i for i in indices if i>=0]
            result[original,:n]=logits[local];values[original]=value[local];kind_result[original]=kind[local]
        distribution=Categorical(logits=result,validate_args=False);distribution.kind_logits=kind_result
        return distribution,values


def load_policy(policy, weights):
    missing, unexpected=policy.load_state_dict(weights,strict=False)
    allowed={f'context.{layer}.{field}' for layer in (0,2) for field in ('weight','bias')} if isinstance(policy,LegacyPolicy) else set()
    if unexpected or set(missing)-allowed:
        raise ValueError(f'Incompatible policy weights: missing={missing}, unexpected={unexpected}')

def optimizer_for(policy, device, learning_rate=3e-4):
    return torch.optim.Adam(policy.parameters(),lr=learning_rate,eps=1e-5,fused=device=='cuda')

def configure_compute(config):
    torch.set_num_threads(1)
    torch.set_float32_matmul_precision('high' if config.get('tf32',True) else 'highest')

def pack_rollout(transitions, advantage, returns, policy, environments, device,core_width=None):
    examples=[(t,j) for t in range(len(transitions)) for j in range(policy,environments,4)]
    if core_width is not None:
        adv=np.array([advantage[t,j] for t,j in examples],np.float32);adv=(adv-adv.mean())/(adv.std()+1e-8)
        buckets={}
        for index,(t,j) in enumerate(examples):
            tokens=int(transitions[t][0][j][core_width]);candidates=len(transitions[t][1][j])
            buckets.setdefault((max(1,tokens).bit_length()//2,max(1,candidates).bit_length()//2),[]).append(index)
        packed=[]
        for members in buckets.values():
            chosen=[examples[k] for k in members];tokens=max(int(transitions[t][0][j][core_width]) for t,j in chosen)
            width=core_width+1+tokens*TOKEN_WIDTH
            obs=torch.as_tensor(pad_observations([transitions[t][0][j][:width] for t,j in chosen]),device=device)
            features,mask=padded([transitions[t][1][j] for t,j in chosen],device)
            selected=torch.as_tensor(np.array([transitions[t][2][j] for t,j in chosen]),device=device)
            old_log=torch.as_tensor(np.array([transitions[t][3][j] for t,j in chosen]),device=device)
            targets=torch.as_tensor(np.array([returns[t,j] for t,j in chosen],np.float32),device=device)
            packed.append((obs,features,mask,selected,old_log,torch.as_tensor(adv[members],device=device),targets))
        return packed
    obs=torch.as_tensor(pad_observations([transitions[t][0][j] for t,j in examples]),device=device)
    features,mask=padded([transitions[t][1][j] for t,j in examples],device)
    selected=torch.as_tensor(np.array([transitions[t][2][j] for t,j in examples]),device=device)
    old_log=torch.as_tensor(np.array([transitions[t][3][j] for t,j in examples]),device=device)
    adv=np.array([advantage[t,j] for t,j in examples],np.float32)
    adv=(adv-adv.mean())/(adv.std()+1e-8)
    targets=np.array([returns[t,j] for t,j in examples],np.float32)
    return obs,features,mask,selected,old_log,torch.as_tensor(adv,device=device),torch.as_tensor(targets,device=device)

def exploration_entropy(distribution,features,mask):
    # Raw candidate entropy rewards choosing an action kind simply because it has
    # many slots. Regularize kind exploration plus bounded exploration within it.
    kinds=features[:,:,:9]*mask[:,:,None];probability=distribution.probs
    group=(kinds*probability[:,:,None]).sum(1)
    kind_entropy=-(group*group.clamp(min=1e-8).log()).sum(-1)
    selected_group=(kinds*group[:,None,:]).sum(-1).clamp(min=1e-8)
    denominator=(kinds*kinds.sum(1).clamp(min=2).log()[:,None,:]).sum(-1).clamp(min=1e-8)
    conditional=(probability*(selected_group.log()-probability.clamp(min=1e-8).log())/denominator).sum(-1)
    return kind_entropy+.1*conditional

def optimize(policy, optimizer, data, config, entropy):
    losses=[];kls=[];entropies=[];clips=[];weights=[];epochs=0
    buckets=data if isinstance(data,list) else [data];device=buckets[0][0].device
    for _ in range(config.get('epochs',4)):
        epoch_kls=[];epoch_weights=[]
        for bucket_index in torch.randperm(len(buckets)).tolist():
            bucket=buckets[bucket_index];size=len(bucket[0]);permutation=torch.randperm(size,device=device)
            for offset in range(0,size,config['minibatch']):
                indices=permutation[offset:offset+config['minibatch']]
                obs,features,mask,selected,old_log,adv,target=(x[indices] for x in bucket)
                distribution,value=policy(obs,features,mask)
                log_ratio=distribution.log_prob(selected)-old_log
                ratio=log_ratio.exp();clip=config.get('clip',.2)
                actor=-torch.minimum(ratio*adv,ratio.clamp(1-clip,1+clip)*adv).mean()
                uncertainty=exploration_entropy(distribution,features,mask).mean()
                loss=actor+.5*(value-target).square().mean()-entropy*uncertainty
                optimizer.zero_grad(set_to_none=True);loss.backward()
                nn.utils.clip_grad_norm_(policy.parameters(),.5,error_if_nonfinite=True);optimizer.step()
                losses.append(loss.detach());entropies.append(uncertainty.detach());weights.append(len(indices))
                epoch_kls.append(((ratio-1)-log_ratio).mean().detach());epoch_weights.append(len(indices))
                clips.append(((ratio-1).abs()>clip).float().mean().detach())
        epochs+=1
        w=torch.tensor(epoch_weights,device=device,dtype=torch.float32)
        epoch_kl=(torch.stack(epoch_kls)*w).sum()/w.sum();kls.extend(epoch_kls)
        if float(epoch_kl.cpu())>config.get('target_kl',.02):break
    w=torch.tensor(weights,device=device,dtype=torch.float32)
    summary=torch.stack([(torch.stack(x)*w).sum()/w.sum() for x in (losses,kls,entropies,clips)]).cpu().numpy()
    if not np.isfinite(summary).all():raise RuntimeError('Non-finite PPO metrics; checkpoint retained for diagnosis.')
    return dict(loss=float(summary[0]),kl=float(summary[1]),entropy=float(summary[2]),clip_fraction=float(summary[3]),epochs=epochs)


def device_benchmark(model, observation, actions):
    observation=pad_observations(observation)
    measurements = {}
    for device in ['cpu'] + (['cuda'] if torch.cuda.is_available() else []):
        candidate = copy.deepcopy(model).to(device)
        optimizer=optimizer_for(candidate,device)
        obs = torch.as_tensor(observation, device=device)
        action, mask = padded(actions, device)
        for _ in range(2):
            distribution, value = candidate(obs, action, mask)
            (value.mean() + distribution.entropy().mean()).backward()
            optimizer.step();optimizer.zero_grad(set_to_none=True)
        if device == 'cuda':
            torch.cuda.synchronize()
        start = time.perf_counter()
        for _ in range(5):
            # Include host -> device conversion and transfer in this end-to-end microbenchmark.
            obs = torch.as_tensor(observation.copy(), device=device)
            action, mask = padded(actions, device)
            distribution, value = candidate(obs, action, mask)
            (value.mean() + distribution.entropy().mean()).backward()
            optimizer.step();optimizer.zero_grad(set_to_none=True)
        if device == 'cuda':
            torch.cuda.synchronize()
        measurements[device] = (time.perf_counter() - start) / 5
        del candidate
    return min(measurements, key=measurements.get), measurements

def calibrate_minibatch(model, observation, actions, device, ceiling):
    """Benchmark disposable model copies; never advances training state or RNG."""
    observation=pad_observations(observation)
    candidates=sorted({n for n in (256,512,1024,2048,ceiling) if 32<=n<=ceiling})
    rows=[]
    for size in candidates:
        candidate=None
        try:
            candidate=copy.deepcopy(model).to(device);optimizer=optimizer_for(candidate,device)
            repeats=(size+len(observation)-1)//len(observation)
            obs=np.concatenate([observation]*repeats)[:size]
            selected_actions=(actions*repeats)[:size]
            features,mask=padded(selected_actions,device);tensor=torch.as_tensor(obs,device=device)
            if device=='cuda':torch.cuda.reset_peak_memory_stats()
            for _ in range(2):
                distribution,value=candidate(tensor,features,mask);loss=value.square().mean()-distribution.entropy().mean()*.001
                optimizer.zero_grad(set_to_none=True);loss.backward();optimizer.step()
            if device=='cuda':torch.cuda.synchronize()
            start=time.perf_counter();iterations=0
            # Long enough to smooth Windows scheduling and GPU boost-state noise;
            # four normal candidates keep a new-session calibration near 30 seconds.
            while iterations<3 or time.perf_counter()-start<7.5:
                distribution,value=candidate(tensor,features,mask);loss=value.square().mean()-distribution.entropy().mean()*.001
                optimizer.zero_grad(set_to_none=True);loss.backward();optimizer.step()
                iterations+=1
            if device=='cuda':torch.cuda.synchronize()
            trial_seconds=time.perf_counter()-start;seconds=trial_seconds/iterations
            peak=torch.cuda.max_memory_reserved() if device=='cuda' else 0
            total=torch.cuda.get_device_properties(0).total_memory if device=='cuda' else 0
            accepted=not total or peak<=total*.9
            rows.append(dict(minibatch=size,seconds=seconds,trial_seconds=trial_seconds,iterations=iterations,samples_per_second=size/seconds,peak_reserved=peak,accepted=accepted))
        except (torch.OutOfMemoryError,RuntimeError) as error:
            if not isinstance(error,torch.OutOfMemoryError) and 'out of memory' not in str(error).lower():raise
            rows.append(dict(minibatch=size,error='CUDA out of memory',accepted=False))
            if device=='cuda':torch.cuda.empty_cache()
        finally:
            del candidate
    selected=select_calibration(rows,min(ceiling,512))
    return selected,rows

def select_calibration(rows, fallback):
    accepted=[row for row in rows if row.get('accepted')]
    return max(accepted,key=lambda row:row['samples_per_second'])['minibatch'] if accepted else fallback

def ranking_key(row):
    return row['score'],row['average_wins'],-row['unused_deployable_bench']

def advantages(rewards, terminals, values, tail, gamma=1., lam=.95, battles=None):
    result = np.zeros_like(rewards)
    carry = np.zeros(rewards.shape[1], dtype=np.float32)
    for t in reversed(range(len(rewards))):
        next_value = tail if t == len(rewards) - 1 else values[t + 1]
        live = 1 - terminals[t]
        delta = rewards[t] + gamma * next_value * live - values[t]
        trace=lam if battles is None else np.where(battles[t],lam,1.)
        carry = delta + gamma * trace * live * carry
        result[t] = carry
    return result, result + values

def wilson(successes, total):
    if total==0: return [0.,1.]
    p=successes/total;z=1.96;denominator=1+z*z/total
    center=(p+z*z/(2*total))/denominator
    radius=z*np.sqrt(p*(1-p)/total+z*z/(4*total*total))/denominator
    return [max(0.,float(center-radius)),min(1.,float(center+radius))]

def training_device(requested, automatic, saved_config=None):
    # Keep Auto resumes on the saved device: a close startup benchmark should not switch numerical execution.
    if requested != 'auto': return requested
    return (saved_config or {}).get('resolved_device', automatic)

def save_checkpoint(output, host, policies, optimizers, update, config, metrics):
    name = f'checkpoint-{update}-{uuid.uuid4().hex[:8]}'
    directory = output / name
    directory.mkdir()
    host.request('checkpoint', path=str(directory / 'host.json'))
    torch.save(dict(protocol=PROTOCOL,encoding=ENCODING,model_version=MODEL_VERSION,objective_version=OBJECTIVE_VERSION,action_width=ACTION_WIDTH, rules_hash=host.ready['RulesHash'], update=update, config=config,
                    policies=[p.state_dict() for p in policies], optimizers=[o.state_dict() for o in optimizers],
                    torch_rng=torch.get_rng_state(), cuda_rng=torch.cuda.get_rng_state_all() if torch.cuda.is_available() else [],
                    numpy_rng=(lambda s: (s[0],s[1].tolist(),s[2],s[3],s[4]))(np.random.get_state()), python_rng=random.getstate(), metrics=metrics), directory / 'learner.pt')
    atomic_json(output / 'checkpoint.json', dict(directory=name, update=update, rules_hash=host.ready['RulesHash']))
    # Publish the pointer only after both halves are durable. Retain two committed checkpoints.
    committed = sorted((p for p in output.glob('checkpoint-*') if re.fullmatch(r'checkpoint-\d+-[0-9a-f]{8}',p.name)
                        and p.resolve().parent==output and p.is_dir()
                        and {m.name for m in p.iterdir()}<={'host.json','learner.pt'}),key=lambda p:p.stat().st_mtime,reverse=True)
    for old in committed[2:]:
        if any(member.resolve().parent!=old.resolve() for member in old.iterdir()):continue
        for member in old.iterdir():
            member.unlink()
        old.rmdir()
    return name

def resume_environment_count(requested,saved):
    actual=saved['environments'];original=saved.get('requested_environments',actual)
    if requested not in (actual,original):raise ValueError('Environment limit differs from checkpoint.')
    return actual

def run(config):
    output = Path(config['output']).resolve()
    output.mkdir(parents=True, exist_ok=True)
    rules = json.loads(Path(config['rules']).read_text(encoding='utf-8-sig'))
    if rules['Issues'] or not rules['Parameters'].get('Usable',rules['Parameters']['Verified'] or rules['Parameters'].get('AllowGuesses',False)):
        raise RuntimeError(f"Training blocked by {len(rules['Issues'])} unresolved game rules. See rules coverage in the UI.")
    random.seed(config['seed']); np.random.seed(config['seed']); torch.manual_seed(config['seed'])
    configure_compute(config)
    config.setdefault('gae_lambda',.95);config.setdefault('entropy_coefficients',[.005,.01,.02,.04]);config['encoding']=ENCODING;config.setdefault('curriculum',True)
    config.setdefault('probe_interval',5);config.setdefault('probe_runs',32);config.setdefault('promotion_runs',128)
    config.setdefault('shop_decision_limit',64);config.setdefault('target_kl',.02)
    # A CPU-only startup probe cannot judge GPU batch efficiency. Keep the
    # requested environment budget for CUDA/Auto; CPU may tune it down.
    config.setdefault('auto_calibrate',not config.get('resume',False))
    config.setdefault('auto_tune_environments',config['auto_calibrate'] and not config.get('resume',False))
    saved_checkpoint = checkpoint_data(config) if config.get('resume') else None
    if saved_checkpoint and (saved_checkpoint[2].get('encoding')!=ENCODING or saved_checkpoint[2].get('model_version')!=MODEL_VERSION):raise ValueError('Legacy models are comparison-only. Start a fresh session.')
    if not config.get('resume') and (output/'checkpoint.json').exists():raise ValueError('This directory already contains a session. Resume it or choose an empty output directory.')
    if saved_checkpoint:
        saved_config=saved_checkpoint[2]['config']
        config['environments']=resume_environment_count(config['environments'],saved_config)
        if saved_config['seed']!=config['seed']:raise RuntimeError('Seed differs from checkpoint.')
    config["session"]=(saved_checkpoint[2]["config"].get("session") if saved_checkpoint else None) or uuid.uuid4().hex[:12]
    host = Host(config, output)
    config.setdefault("requested_environments",config["environments"]);config["environments"]=host.ready["Count"]
    policies = [Policy(host.ready['ObservationWidth'], len(rules['Species'])) for _ in range(4)]
    observation, actions = host.batch()
    automatic, timings = device_benchmark(policies[0], observation, actions)
    device = training_device(config['device'],automatic,saved_checkpoint[2]['config'] if saved_checkpoint else None)
    config['resolved_device'] = device
    if device == 'cuda' and not torch.cuda.is_available():
        raise RuntimeError('CUDA was explicitly selected but is unavailable.')
    calibration=None
    if config.get('auto_calibrate') and not saved_checkpoint:
        calibration_start=time.perf_counter()
        ceiling=int(config.get('minibatch',2048));selected,rows=calibrate_minibatch(policies[0],observation,actions,device,ceiling)
        config['requested_minibatch']=ceiling;config['minibatch']=selected
        worker_rows=host.ready['WorkerMilliseconds'];environment_rows=host.ready.get('EnvironmentMillisecondsPerRun',{})
        fastest_worker=min(worker_rows.values()) if worker_rows else 1.;worker_ceiling=max(1,host.ready['Workers'])
        projections=[]
        for environment,milliseconds in environment_rows.items():
            for worker,worker_milliseconds in (worker_rows or {worker_ceiling:fastest_worker}).items():
                simulation_rate=1000/max(1e-9,milliseconds)*(fastest_worker/max(1e-9,worker_milliseconds))
                for row in rows:
                    gpu_rate=row.get('samples_per_second',0);accepted=row.get('accepted',False)
                    projected=1/(1/simulation_rate+1/gpu_rate) if accepted and gpu_rate else 0
                    projections.append(dict(environments=int(environment),workers=int(worker),minibatch=row['minibatch'],accepted=accepted,projected_decisions_per_second=projected))
        calibration=dict(selected_device=device,selected_workers=host.ready['Workers'],selected_environments=host.ready['Count'],selected_minibatch=selected,
                         requested_workers=config['workers'],requested_environments=config['requested_environments'],requested_minibatch=ceiling,
                         calibration_seconds=time.perf_counter()-calibration_start,device_seconds=timings,worker_milliseconds=worker_rows,
                         environment_milliseconds_per_run=environment_rows,minibatches=rows,candidates=projections)
        atomic_json(output/'calibration.json',calibration)
    policies = [p.to(device) for p in policies]
    optimizers = [optimizer_for(p,device,config.get('learning_rate',3e-4)) for p in policies]
    update = 0; metrics = dict(runs=0, successes=0, total_wins=0, trainers={})
    if saved_checkpoint:
        _, directory, saved, _ = saved_checkpoint
        if saved['rules_hash'] != host.ready['RulesHash'] or saved['config']['environments'] != config['environments'] or saved['config']['seed'] != config['seed']:
            raise RuntimeError('Rules hash, seed or environment count differs from checkpoint.')
        for i in range(4):
            load_policy(policies[i],saved['policies'][i])
            if saved.get('model_version')==MODEL_VERSION and saved.get('objective_version')==OBJECTIVE_VERSION:
                optimizers[i].load_state_dict(saved['optimizers'][i])
            else:
                # Retain the actor, but its old value estimates and Adam moments
                # describe a different return scale and parameter set.
                nn.init.zeros_(policies[i].value.weight);nn.init.zeros_(policies[i].value.bias)
        host.request('restore', path=str(directory / 'host.json'))
        torch.set_rng_state(saved['torch_rng'].cpu())
        ns=saved['numpy_rng']; np.random.set_state((ns[0],np.array(ns[1],np.uint32),ns[2],ns[3],ns[4])); random.setstate(saved['python_rng'])
        if saved['cuda_rng'] and torch.cuda.is_available(): torch.cuda.set_rng_state_all(saved['cuda_rng'])
        update, metrics = saved['update'], saved['metrics']
        candidates=[]
        evaluation_path=output/'evaluation.json'
        if evaluation_path.exists():candidates.append(json.loads(evaluation_path.read_text()))
        ranking_path=output/'generation-ranking.json'
        if ranking_path.exists():
            ranking=json.loads(ranking_path.read_text())
            if ranking.get('rules_hash')==host.ready['RulesHash'] and ranking.get('best'):candidates.append(dict(ranking['best'],rules_hash=ranking['rules_hash']))
        for candidate in candidates:
            if candidate.get('rules_hash')!=host.ready['RulesHash'] or not candidate.get('completed') or candidate.get('baseline'):continue
            board_path=output/'evaluation-cache'/f"{candidate.get('cache_key','missing')}-boards.json"
            if not board_path.exists():continue
            winner=max(candidate['policies'],key=lambda row:(row['confidence_interval_95'][0],row['average_wins'],-row.get('unused_deployable_bench',0)))
            entries=[entry for entry in json.loads(board_path.read_text()) if entry['Policy']==winner['policy']]
            if entries:host.request('champion_boards',entries=entries)
            metrics['ranked_champion']=dict(generation=candidate['generation'],policy=winner['policy'],update=candidate['update'],score=winner['confidence_interval_95'][0])
    atomic_json(output / 'configuration.json', config)
    start = time.monotonic(); checkpoint_at = start; decisions = 0; last_saved = None
    battles=deployed=deployable_bench=limited_turns=deployment_improving=deployment_reversing=finish_with_gap=0
    action_counts={};opponent_mix={};recent=deque(maxlen=2000)
    gpu_start=torch.cuda.Event(enable_timing=True) if device=='cuda' else None
    gpu_end=torch.cuda.Event(enable_timing=True) if device=='cuda' else None
    stage_totals={key:0. for key in ('batch','python_packing','host_to_device','cuda_inference','device_to_host','inference','step','encoding','buffer','simulation','history','opponent_lookup','league','synchronization_wait')}
    initial_metrics={key:metrics[key] for key in ('runs','successes','total_wins')}
    try:
        # A failure before the first five-minute save must still leave a valid
        # paired learner/host checkpoint. Do not snapshot a partially failed step.
        last_saved=save_checkpoint(output,host,policies,optimizers,update,config,metrics)
        checkpoint_at=time.monotonic()
        probe_suite=output/'probe-suite';probe_suite.mkdir(exist_ok=True)
        if not (probe_suite/'host.json').exists():
            candidate=config.get('evaluation_suite_checkpoint')
            if not candidate:
                archives=config.get('comparison_archives',[])
                if archives:
                    archive=Path(archives[0]);pointer=json.loads((archive/'checkpoint.json').read_text());candidate=str(archive/pointer['directory']/'host.json')
            shutil.copyfile(candidate or output/last_saved/'host.json',probe_suite/'host.json')
        while not config.get('updates') or update < config['updates']:
            control = json.loads((output / 'control.json').read_text()) if (output / 'control.json').exists() else {}
            if control.get('command') == 'stop' or config.get('minutes', 0) > 0 and time.monotonic() - start >= config['minutes'] * 60:
                break
            if control.get('command') == 'pause':
                last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics)
                atomic_json(output / 'progress.json', dict(status='Paused', update=update, checkpoint=last_saved, **metrics))
                while json.loads((output / 'control.json').read_text()).get('command') == 'pause': time.sleep(.2)
                continue
            transitions = [];rollout_start=time.monotonic()
            with torch.inference_mode():population=PopulationInference(policies)
            for _ in range(config['rollout']):
                batch_start=time.monotonic()
                observation, actions = host.batch()
                stage_totals['batch']+=time.monotonic()-batch_start
                selected = np.zeros(config['environments'], dtype=np.int64)
                log_prob = np.zeros_like(selected, dtype=np.float32); values = log_prob.copy()
                inference_start=time.perf_counter()
                with torch.inference_mode():
                    prepared,packing_seconds,transfer_seconds=population.prepare(observation,actions)
                    stage_totals['python_packing']+=packing_seconds;stage_totals['host_to_device']+=transfer_seconds
                    forward_start=time.perf_counter()
                    if gpu_start:gpu_start.record()
                    distribution,value=population.infer(prepared);choice=distribution.sample()
                    device_numeric=torch.stack([choice.float(),distribution.log_prob(choice),value],dim=1)
                    if gpu_end:gpu_end.record()
                    enqueue_seconds=time.perf_counter()-forward_start
                    wait_start=time.perf_counter()
                    if gpu_end:gpu_end.synchronize()
                    stage_totals['synchronization_wait']+=time.perf_counter()-wait_start
                    transfer_back_start=time.perf_counter();numeric=device_numeric.cpu().numpy()
                    copy_wall=time.perf_counter()-transfer_back_start
                    gpu_seconds=gpu_start.elapsed_time(gpu_end)/1000 if gpu_start else enqueue_seconds
                    stage_totals['cuda_inference']+=gpu_seconds
                    stage_totals['device_to_host']+=copy_wall
                stage_totals['inference']+=time.perf_counter()-inference_start
                selected=numeric[:,0].astype(np.int64);log_prob=numeric[:,1];values=numeric[:,2]
                step_start=time.monotonic()
                result = host.request('step', actions=selected.tolist())
                stage_totals['step']+=time.monotonic()-step_start
                transitions.append((observation, actions, selected, log_prob, values, np.array(result['Rewards'], np.float32), np.array(result['Terminals'], np.float32),np.array(result['BattleBoundaries'],np.bool_)))
                decisions += config['environments']
                battles+=result['Battles'];deployed+=result['Deployed'];deployable_bench+=result['DeployableBench'];limited_turns+=result['LimitedTurns']
                deployment_improving+=result.get('DeploymentImproving',0);deployment_reversing+=result.get('DeploymentReversing',0);finish_with_gap+=result.get('FinishWithDeployableBench',0)
                for key,value in result.get('ActionCounts',{}).items():action_counts[key]=action_counts.get(key,0)+value
                for opponent in result.get('Opponents',[]):opponent_mix[opponent['Kind']]=opponent_mix.get(opponent['Kind'],0)+1
                for key,value in result.get('HostStages',{}).items():stage_totals[key]=stage_totals.get(key,0.)+value/1000
                metrics['rank_bands']=result.get('RankBands',{})
                metrics['episode_starts']=result.get('EpisodeStarts',[0,0,0])
                metrics['repeated_states_blocked']=result.get('RepeatedStatesBlocked',0)
                for key,source in (('opponent_lookup','OpponentLookupMilliseconds'),('league','LeagueMilliseconds'),('encoding','EncodingMilliseconds'),('buffer','BufferMilliseconds'),('simulation','SimulationMilliseconds'),('history','HistoryMilliseconds')):stage_totals[key]+=result.get(source,0)/1000
                for record in result['Runs']:
                    summary = record['Summary']; metrics['runs'] += 1; metrics['successes'] += int(summary['Success']); metrics['total_wins'] += summary['Wins']
                    recent.append((int(summary['Success']),summary['Wins']))
                    trainer = metrics['trainers'].setdefault(summary['Trainer'], dict(runs=0, successes=0, wins=0))
                    trainer['runs'] += 1; trainer['successes'] += int(summary['Success']); trainer['wins'] += summary['Wins']
                    cohort=metrics.setdefault('generations',{}).setdefault(str(summary['Generation']),dict(runs=0,successes=0,wins=0))
                    cohort['runs']+=1;cohort['successes']+=int(summary['Success']);cohort['wins']+=summary['Wins']
            observation, actions = host.batch(); tail = np.zeros(config['environments'], np.float32)
            with torch.inference_mode():_,value=population(observation,actions);tail=value.cpu().numpy()
            del population
            rollout_seconds=time.monotonic()-rollout_start;optimization_start=time.monotonic()
            advantage, returns = advantages(np.stack([x[5] for x in transitions]), np.stack([x[6] for x in transitions]), np.stack([x[4] for x in transitions]), tail,lam=config['gae_lambda'],battles=np.stack([x[7] for x in transitions]))
            optimization=[]
            for i in range(4):
                if i>=config['environments']:continue
                data=pack_rollout(transitions,advantage,returns,i,config['environments'],device,policies[i].core_width)
                optimization.append(optimize(policies[i],optimizers[i],data,config,config['entropy_coefficients'][i]))
                del data
            optimization_seconds=time.monotonic()-optimization_start
            update += 1
            generation = update // 10
            host.request('generation', value=generation)
            if update % 10 == 0:
                torch.save([p.state_dict() for p in policies], output / f'generation-{generation}.pt')
                for old in output.glob('generation-*.pt'):
                    if re.fullmatch(r'generation-\d+',old.stem) and old.resolve().parent==output and int(old.stem.split('-')[1]) < generation - 19: old.unlink()
            if config['probe_interval'] and update%config['probe_interval']==0:
                from evaluation import EvaluationRunner
                probe_config=dict(config,output=str(output/'probes'),cancel_control=str(output/'control.json'),progress_output=str(output),evaluation_environments=128)
                torch_state=torch.get_rng_state();cuda_state=torch.cuda.get_rng_state_all() if device=='cuda' else None
                try:
                    snapshot=dict(label=f'Probe update {update}',update=update,generation=generation,weights=[p.state_dict() for p in policies],encoding=ENCODING,model_version=MODEL_VERSION)
                    with EvaluationRunner(probe_config,output/'probes',probe_suite,rules,config['probe_runs']) as runner:
                        runner.progress_context=dict(status='Evaluating',update=update,completed=0,total=1)
                        probe=runner.evaluate(snapshot);metrics['fixed_probe']=probe
                    best_probe=max((p['confidence_interval_95'][0] for p in probe['policies']),default=0.)
                    if probe['completed'] and best_probe>metrics.get('best_probe_score',-1):
                        with EvaluationRunner(probe_config,output/'probes',probe_suite,rules,config['promotion_runs']) as runner:
                            runner.progress_context=dict(status='Evaluating',update=update,completed=0,total=1)
                            full=runner.evaluate(dict(snapshot,label=f'Promotion evaluation {update}'))
                            winner=max(full['policies'],key=lambda p:(p['confidence_interval_95'][0],p['average_wins']))
                            if full['completed'] and winner['confidence_interval_95'][0]>metrics.get('champion_score',0):
                                entries=[entry for entry in runner.last_boards if entry['Policy']==winner['policy']]
                                if entries:host.request('champion_boards',entries=entries)
                                metrics['champion_score']=winner['confidence_interval_95'][0];metrics['champion_update']=update
                            if full['completed']:metrics['best_probe_score']=best_probe
                            atomic_json(output/'promotion-evaluation.json',full)
                finally:
                    torch.set_rng_state(torch_state)
                    if cuda_state is not None:torch.cuda.set_rng_state_all(cuda_state)
                    configure_compute(config)
            if time.monotonic() - checkpoint_at >= 300:
                last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics); checkpoint_at = time.monotonic()
            progress = dict(status='Training', update=update, generation=generation, device=device, device_seconds=timings, decisions=decisions,
                            decisions_per_second=decisions / max(.01, time.monotonic() - start), loss=float(np.mean([x['loss'] for x in optimization])), checkpoint=last_saved,
                            ppo=optimization,rollout_seconds=rollout_seconds,rollout_decisions_per_second=config['environments']*config['rollout']/max(.01,rollout_seconds),optimization_seconds=optimization_seconds,model_version=MODEL_VERSION,objective_version=OBJECTIVE_VERSION,
                            average_deployed=deployed/max(1,battles),unused_deployable_bench=deployable_bench/max(1,battles),limited_turn_rate=limited_turns/max(1,battles),
                            session_runs=metrics['runs']-initial_metrics['runs'],session_success_rate=(metrics['successes']-initial_metrics['successes'])/max(1,metrics['runs']-initial_metrics['runs']),
                            session_average_wins=(metrics['total_wins']-initial_metrics['total_wins'])/max(1,metrics['runs']-initial_metrics['runs']),
                            recent_runs=len(recent),recent_success_rate=sum(x[0] for x in recent)/max(1,len(recent)),recent_average_wins=sum(x[1] for x in recent)/max(1,len(recent)),
                            action_counts=action_counts,opponent_mix=opponent_mix,deployment_improving=deployment_improving,deployment_reversing=deployment_reversing,finish_with_deployable_bench=finish_with_gap,
                            stage_seconds=stage_totals,cuda_peak_allocated=torch.cuda.max_memory_allocated() if device=='cuda' else 0,cuda_peak_reserved=torch.cuda.max_memory_reserved() if device=='cuda' else 0,
                            actual_workers=host.ready['Workers'],worker_milliseconds=host.ready['WorkerMilliseconds'],actual_environments=host.ready['Count'],environment_milliseconds_per_run=host.ready.get('EnvironmentMillisecondsPerRun',{}),
                            success_rate=metrics['successes'] / max(1, metrics['runs']), average_wins=metrics['total_wins'] / max(1, metrics['runs']),
                            diagnostic_fixture=rules['Build'] == 'fixture', **metrics)
            atomic_json(output / 'progress.json', progress)
            from persistence import append_metrics
            append_metrics(output,progress)
        last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics)
        atomic_json(output / 'progress.json', dict(status='Stopped', update=update, checkpoint=last_saved, device=device, device_seconds=timings,
                    actual_workers=host.ready['Workers'],worker_milliseconds=host.ready['WorkerMilliseconds'],actual_environments=host.ready['Count'],environment_milliseconds_per_run=host.ready.get('EnvironmentMillisecondsPerRun',{}),
                    decisions=decisions, decisions_per_second=decisions/max(.01,time.monotonic()-start), success_rate=metrics['successes']/max(1,metrics['runs']),
                    average_deployed=deployed/max(1,battles),unused_deployable_bench=deployable_bench/max(1,battles),limited_turn_rate=limited_turns/max(1,battles),
                    session_runs=metrics['runs']-initial_metrics['runs'],session_success_rate=(metrics['successes']-initial_metrics['successes'])/max(1,metrics['runs']-initial_metrics['runs']),
                    session_average_wins=(metrics['total_wins']-initial_metrics['total_wins'])/max(1,metrics['runs']-initial_metrics['runs']),
                    recent_runs=len(recent),recent_success_rate=sum(x[0] for x in recent)/max(1,len(recent)),recent_average_wins=sum(x[1] for x in recent)/max(1,len(recent)),
                    action_counts=action_counts,opponent_mix=opponent_mix,deployment_improving=deployment_improving,deployment_reversing=deployment_reversing,finish_with_deployable_bench=finish_with_gap,stage_seconds=stage_totals,
                    cuda_peak_allocated=torch.cuda.max_memory_allocated() if device=='cuda' else 0,cuda_peak_reserved=torch.cuda.max_memory_reserved() if device=='cuda' else 0,
                    average_wins=metrics['total_wins']/max(1,metrics['runs']), diagnostic_fixture=rules['Build'] == 'fixture', **metrics))
    finally:
        host.close()

def checkpoint_data(config):
    output = Path(config['output']).resolve()
    pointer = json.loads((output / 'checkpoint.json').read_text())
    directory = output / pointer['directory']
    if directory.parent != output or not directory.name.startswith('checkpoint-'):
        raise ValueError('Invalid checkpoint location.')
    saved = torch.load(directory / 'learner.pt', map_location='cpu', weights_only=True)
    if saved['protocol'] not in (1,PROTOCOL): raise ValueError('Unsupported checkpoint protocol.')
    if saved.get('encoding') not in (4,ENCODING) or saved['policies'][0]['action.0.weight'].shape[1]!=ACTION_WIDTH:raise ValueError('Incompatible observation/action encoding.')
    rules = json.loads(Path(config['rules']).read_text(encoding='utf-8-sig'))
    if rules['Hash'] != saved['rules_hash']:
        archived=output/'history'/f"rules-{saved['rules_hash']}.json"
        if not config.get('_comparison_only') or not re.fullmatch('[A-Fa-f0-9]+',saved['rules_hash']) or not archived.exists():raise ValueError('Rules hash differs from checkpoint.')
        rules=json.loads(archived.read_text(encoding='utf-8-sig'))
        if rules['Hash']!=saved['rules_hash']:raise ValueError('Archived rules hash differs from checkpoint.')
        config['rules']=str(archived)
    return output, directory, saved, rules

def export(config):
    output, directory, saved, rules = checkpoint_data(config)
    target = output / 'export'
    target.mkdir(exist_ok=True)
    for i, weights in enumerate(saved['policies']): torch.save(weights, target / f'policy-{i}.pt')
    atomic_json(target / 'metadata.json', dict(protocol=PROTOCOL, encoding=saved['encoding'],model_version=saved.get('model_version',1),objective_version=saved.get('objective_version',1),action_kind_balancing=saved.get('model_version',1)>=2, rules_hash=rules['Hash'], simulation_revision=rules.get('SimulationRevision',2),species=rules['Species'],
                items=rules['Items'], trinkets=rules['Trinkets'], trainers=rules['Trainers'], entity_order=[x['Id'] for x in rules['Species']],
                observation_width=16+270+len(rules['Trinkets'])+len(rules['Trainers']), action_width=ACTION_WIDTH, update=saved['update'],
                numeric_schema=encoding_metadata(rules,saved['encoding']),ragged_observations=True,observation_length_formula=f"observation_width + 1 + token_count * {12 if saved['encoding']==4 else TOKEN_WIDTH}",
                parameters=rules['Parameters'],profile=rules.get('Profile'),evidence=rules.get('Evidence'),encounters=rules.get('Encounters'),full_fidelity_ready=rules.get('FullFidelityReady',False),
                diagnostic_fixture=rules['Build']=='fixture'))
    atomic_json(output / 'progress.json', dict(status='Exported', update=saved['update'], diagnostic_fixture=rules['Build']=='fixture'))

def encoding_metadata(rules,version=ENCODING):
    """Describe every existing column, including padding, without claiming missing game inputs are encoded."""
    headers=['day','turns_remaining','wins','gold','max_hp','rank','free_rerolls','item_uses','maximum_item_uses',
             'locked','phase','choice_count','overflow_present','next_battle_start_repeat','next_victory_gold','next_copy_enemy']
    entity=['species_id','level','shiny','copies','base_cooldown','damage','heal','shield','burn','poison','shock',
            'speed','multicast','sell_value','rarity','added_types_bitmask','shop_item_id','shop_price']
    metadata=dict(headers=headers,header_divisors=[20,10,10,100,1000,14,10,5,5,1,6,10,1,1,100,1],
                entities=dict(offset=16,count=15,width=18,order=['team:0..5','bench:0..3','shop:0..4'],fields=entity,
                              divisors=[1,4,1,3,10,100,100,100,100,100,100,10,10,10,5,1,1,100]),
                trinket_counts=dict(offset=286,order=[x['Id'] for x in rules['Trinkets']]),
                trainer_flags=dict(offset=286+len(rules['Trinkets']),order=[x['Id'] for x in rules['Trainers']]),
                type_bits=sorted({t for s in rules['Species'] for t in s['Types']}),
                definition_ids='One-based catalog order; zero denotes absent.',
                actions=dict(move_one_hot=['Buy','BuyItem','Sell','Move','(automatic merge)','Reroll','Lock','Choose','Finish'],
                             fields={9:'(source+1)/11',10:'(target+1)/11',11:'(choice+1)/10',12:'offer_price/100',
                                     13:'offered_species_id',14:'offered_level/4',15:'offered_shiny',16:'offered_item_id',
                                     17:'chosen_trinket_id',18:'chosen_trainer_id',19:'first_choice_effect_operation',
                                     20:'first_choice_effect_stat',21:'first_choice_effect_amount',22:'first_choice_effect_target',
                                     23:'granted_species_id',24:'granted_level/4',25:'granted_shiny',26:'grant_stat_bonus',27:'grant_stat',
                                     28:'resolves_at_shop_start',29:'choice_requires_target'},
                             choice_fingerprint_limbs=[30,31,32,33],extra_fields={34:'choice_effect_count',35:'source_species_id',36:'target_species_id',37:'deployed_delta',38:'deployable_bench_delta',39:'finish_with_deployable_bench'},selection='Index into the complete ordered legal candidate list.'),
                state_tokens=dict(offset=16+270+len(rules['Trinkets'])+len(rules['Trainers']),width=12 if version==4 else TOKEN_WIDTH,
                    fields=['json_kind/8','path_fingerprint_0..3','string_fingerprint_0..3','signed_log_numeric_or_species_id','depth/16','ordinal/1000'],
                    fingerprint='FNV-1a 64-bit UTF-16 code units split into four exact 16-bit limbs divided by 65535',
                    contents='Ordered bag and trainer entries, every visible counter, added types, stored discounts, stored devoured creatures, complete choice/effect parameters, overflow, foreign pools and visible pending effects. Ragged batches are padded without truncation.'),
                limitations=['Hidden randomness, pending future choice batches and future opponent boards are excluded.'])
    if version==5:
        effects=[effect for species in rules['Species'] for level in species['Normal']+species['Shiny'] for effect in level['Effects']]
        metadata['state_tokens']=dict(offset=16+270+len(rules['Trinkets'])+len(rules['Trainers']),width=64,
            fields=['kind','owner','definition_or_counter_key','value']+[f'effect:{key}' for key in effects[0]] if effects else [],
            kinds={1:'unit',2:'type',3:'counter',4:'item',5:'trinket',6:'effect',7:'trainer',8:'choice',9:'target_rule',10:'decision_budget_and_history',11:'visible_flags',12:'devoured_boundary',13:'foreign_species',14:'pending_purchase'},
            counter_keys='FNV-1a 64-bit UTF-16 key in columns 2,5,6,7 as four 16-bit limbs / 65535; definition symbols use sorted manifest vocabulary.',
            effect_fields=list(effects[0]) if effects else [],numeric_transform='signed log1p / 10; effect enums / 64; symbols / 4096; nullable effect fields use -1',
            contents='All player-visible persistent state, compiled effects and pending effects; no RNG or future opponents.')
        metadata['actions']['choice_fingerprint_limbs']=[]
        metadata['actions']['fields'].update({30:'choice: weighted effect amounts; other: column delta',31:'choice: weighted operation/target descriptors; other: row delta',32:'choice: definition symbol; other: physical adjacency',33:'choice: target nonshiny; other: source role (team=0,bench=1,shop=2)'})
        metadata['state_tokens']['unit_fields']={2:'species_id',3:'signed_log_stored_discount',4:'level/4',5:'shiny',6:'copies/3',7:'devoured_depth',8:'cooldown/10',9:'rarity/5',10:'stats: damage..sell_value, nine signed-log values',19:'signed_log_instance'}
        metadata['state_tokens']['effect_offset']=4
        metadata['state_tokens']['effect_prefix']=['kind=6','owner','devoured_depth','reserved']
        metadata['state_tokens']['global_history']={0:'kind=10',3:'remaining_decisions/limit',4:'last_action_kind/8',5:'repeated_kind/64'}
        metadata['action_selection']='Choose kind argmax then conditional candidate argmax; sampling uses the joint kind/candidate distribution.'
    return metadata


def evaluate(config,runs=128,initial=False):
    from evaluation import EvaluationRunner
    output,directory,saved,rules=checkpoint_data(config)
    snapshot=dict(label='Initial' if initial else f"Current update {saved['update']}",update=0 if initial else saved['update'],
                  generation=0 if initial else saved['update']//10,weights=None if initial else saved['policies'],encoding=saved['encoding'],model_version=saved.get('model_version',3))
    with EvaluationRunner(config,output,Path(config.get('evaluation_suite_checkpoint',directory/'host.json')).parent,rules,runs) as runner:
        runner.progress_context=dict(status='Evaluating',completed=0,total=1)
        report=runner.evaluate(snapshot)
        if report['completed'] and not runner.cancelled:
            runner.progress_context=dict(status='Evaluating',completed=1,total=2)
            report['baselines']={'strongest-shop':runner.evaluate(dict(label='Strongest-shop baseline',update=-1,generation=-1,weights=None,encoding=5,model_version=MODEL_VERSION,baseline=True))}
        report.update(status='Evaluated' if report['completed'] else 'Cancelled' if runner.cancelled else 'Incomplete',initial_policy=initial,rules_hash=rules['Hash'])
        atomic_json(output/('evaluation-initial.json' if initial else 'evaluation.json'),report);atomic_json(output/'progress.json',report)

def available_snapshots(output,saved,seed):
    snapshots=[dict(label='Initial',update=0,generation=0,weights=None,encoding=saved['encoding'],model_version=saved.get('model_version',3))]
    for path in sorted(output.glob('generation-*.pt'),key=lambda p:int(p.stem.split('-')[1])):
        generation=int(path.stem.split('-')[1]);snapshots.append(dict(label=f'Generation {generation}',update=generation*10,generation=generation,
            weights=torch.load(path,map_location='cpu',weights_only=True),encoding=saved['encoding'],model_version=saved.get('model_version',3)))
    if not any(row['update']==saved['update'] for row in snapshots):snapshots.append(dict(label=f"Current update {saved['update']}",update=saved['update'],generation=saved['update']//10,weights=saved['policies'],encoding=saved['encoding'],model_version=saved.get('model_version',3)))
    return snapshots

def rank_generations(config,runs=128):
    from evaluation import EvaluationRunner
    output,directory,saved,rules=checkpoint_data(config);snapshots=available_snapshots(output,saved,config['seed']);rows=[]
    for archive in config.get('comparison_archives',[]):
        archived_output,_,archived,archived_rules=checkpoint_data(dict(config,output=archive,rules=str(Path(archive)/'rules.json')))
        if archived_rules['Hash']!=rules['Hash']:continue
        for snapshot in available_snapshots(archived_output,archived,archived['config']['seed']):
            snapshot['label']='Archived '+snapshot['label'];snapshot['initial_seed']=archived['config']['seed'];snapshot['archive']=str(archived_output);rows.append(snapshot)
    snapshots+=rows;rows=[]
    snapshots.append(dict(label="Strongest-shop baseline",update=-1,generation=-1,weights=None,encoding=5,model_version=MODEL_VERSION,baseline=True))
    report=dict(status='Cancelled',snapshots=[],best=None,rules_hash=rules['Hash'],completed=0,total=len(snapshots))
    with EvaluationRunner(config,output,Path(config.get('evaluation_suite_checkpoint',directory/'host.json')).parent,rules,runs) as runner:
        atomic_json(output/'generation-ranking.json',dict(report,evaluation_batch=runner.batch_id,suite_hash=runner.suite_hash,status='Ranking generations'))
        for index,snapshot in enumerate(snapshots):
            if runner.cancelled:break
            runner.progress_context=dict(status='Ranking generations',completed=index,total=len(snapshots),snapshots=sorted(rows,key=ranking_key,reverse=True))
            rows.append(runner.evaluate(snapshot))
            ordered=sorted(rows,key=lambda r:(r['completed'],*ranking_key(r)),reverse=True)
            for rank,row in enumerate(ordered,1):row['rank']=rank
            complete=[row for row in ordered if row['completed'] and not row.get('baseline')]
            report=dict(status='Ranking generations',evaluation_batch=runner.batch_id,suite_hash=runner.suite_hash,rules_hash=rules['Hash'],runs_per_snapshot=runs,selection='greedy',snapshots=ordered,best=complete[0] if complete else None,completed=index+1,total=len(snapshots))
            atomic_json(output/'generation-ranking.json',report);atomic_json(output/'progress.json',report)
            if not rows[-1]['completed']:break
        report['status']='Ranked' if len(rows)==len(snapshots) and all(r['completed'] for r in rows) else 'Cancelled' if runner.cancelled else 'Incomplete'
        atomic_json(output/'generation-ranking.json',report);atomic_json(output/'progress.json',report)

def export_snapshot(config, requested_update=None):
    output,directory,saved,rules=checkpoint_data(config);weights=saved['policies'];update=saved['update']
    if requested_update is not None and requested_update!=update:
        if requested_update==0:
            torch.manual_seed(saved['config']['seed']);cls=LegacyPolicy if saved['encoding']==4 else Policy
            width=16+270+len(rules['Trinkets'])+len(rules['Trainers'])
            weights=[(cls(width,len(rules['Species'])) if saved['encoding']==4 else cls(width,len(rules['Species']),saved.get('model_version',5))).state_dict() for _ in range(4)]
        else:
            path=output/f'generation-{requested_update//10}.pt'
            if requested_update%10 or not path.exists():raise ValueError('Requested ranked snapshot is unavailable.')
            weights=torch.load(path,map_location='cpu',weights_only=True)
        update=requested_update
    target=Path(config.get('export_output',output))/('export-archived-'+str(update) if config.get('export_output') else 'export');target.mkdir(parents=True,exist_ok=True)
    for i,state in enumerate(weights):torch.save(state,target/f'policy-{i}.pt')
    atomic_json(target/'metadata.json',dict(protocol=PROTOCOL,encoding=saved['encoding'],model_version=saved.get('model_version',3),objective_version=saved.get('objective_version',3),action_kind_balancing=True,
                rules_hash=rules['Hash'],simulation_revision=rules.get('SimulationRevision',2),species=rules['Species'],items=rules['Items'],trinkets=rules['Trinkets'],trainers=rules['Trainers'],
                entity_order=[x['Id'] for x in rules['Species']],observation_width=16+270+len(rules['Trinkets'])+len(rules['Trainers']),action_width=ACTION_WIDTH,update=update,
                numeric_schema=encoding_metadata(rules,saved['encoding']),ragged_observations=True,observation_length_formula=f"observation_width + 1 + token_count * {12 if saved['encoding']==4 else TOKEN_WIDTH}",parameters=rules['Parameters'],
                profile=rules.get('Profile'),evidence=rules.get('Evidence'),encounters=rules.get('Encounters'),full_fidelity_ready=rules.get('FullFidelityReady',False),diagnostic_fixture=rules['Build']=='fixture'))
    atomic_json(Path(config.get('export_output',output))/'progress.json',dict(status='Exported',update=update,diagnostic_fixture=rules['Build']=='fixture'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    parser.add_argument('--evaluate', action='store_true')
    parser.add_argument('--export', action='store_true')
    parser.add_argument('--runs',type=int,default=128)
    parser.add_argument('--initial',action='store_true')
    parser.add_argument('--rank-generations',action='store_true')
    parser.add_argument('--export-update',type=int)
    parser.add_argument('--export-archive')
    parser.add_argument('--suite',choices=['development','final'],default='development')
    parser.add_argument('--scenario-bank')
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    if args.evaluate or args.export or args.rank_generations:config['_comparison_only']=True
    if args.scenario_bank:config['evaluation_states']=args.scenario_bank
    if args.suite=='final':config['evaluation_seed']=config['seed']+2000000033
    try:
        if args.rank_generations:rank_generations(config,args.runs)
        elif args.evaluate:
            Path(config['output'],'evaluation').mkdir(parents=True,exist_ok=True)
            evaluate(config,args.runs,args.initial)
        elif args.export:
            if args.export_archive:
                config=dict(config,export_output=config['output'],output=args.export_archive,rules=str(Path(args.export_archive)/'rules.json'))
            export_snapshot(config,args.export_update)
        else: run(config)
    except Exception as error:
        atomic_json(Path(config['output']) / 'progress.json', dict(status='Failed', error=str(error)))
        raise

if __name__ == '__main__':
    from persistence import output_writer
    # All CLI operations write progress/cache files, so ownership covers evaluations too.
    import sys
    config_index=sys.argv.index('--config')+1 if '--config' in sys.argv else None
    if config_index is None:main()
    else:
        with output_writer(json.loads(Path(sys.argv[config_index]).read_text(encoding='utf-8-sig'))['output']):main()
