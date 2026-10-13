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

import numpy as np
import torch
from torch import nn
from torch.distributions import Categorical

PROTOCOL = 1
ENCODING = 4
ACTION_WIDTH = 40
TOKEN_WIDTH = 12
MODEL_VERSION = 3
OBJECTIVE_VERSION = 3

def atomic_json(path, value):
    path = Path(path)
    staged = path.with_name(path.name + '.tmp')
    staged.write_text(json.dumps(value), encoding='utf-8')
    os.replace(staged, path)

class Host:
    def __init__(self, config, output):
        self.output = Path(output)
        self.buffer = self.output / 'numeric-batch.bin'
        self.process = subprocess.Popen(['dotnet', config['host'], '--serve', config['rules'], str(self.buffer),
                                         str(config['environments']), str(config['workers']), str(config['seed']),
                                         str(config.get('history', self.output / 'history')), 'evaluation-initial' if config.get('initial_evaluation') else 'evaluation' if config.get('evaluation') else 'training',
                                         'tune' if config.get('auto_tune_workers',True) else 'fixed',
                                         'tune-environments' if config.get('auto_tune_environments',True) and not config.get('resume') else 'fixed-environments',config.get('session','legacy'),
                                         str(config.get('shop_decision_limit',64)),str(config.get('snapshot_update',-1)),str(config.get('evaluation_batch','none')),
                                         str(config.get('snapshot_generation',-1))], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=0x08000000 if os.name == 'nt' else 0)
        self.ready = self._read()
        if self.ready.get("Encoding")!=ENCODING:raise RuntimeError("Incompatible numeric protocol encoding.")
        if self.ready.get('TrainingObjective')!=OBJECTIVE_VERSION:raise RuntimeError('Rebuild the training host for the current learning objective.')
        self.map = np.memmap(self.buffer, mode='r', dtype=np.uint8)
    def _read(self):
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError(self.process.stderr.read() or 'Simulation host terminated.')
        result = json.loads(line)
        if 'Error' in result:
            raise RuntimeError(result['Error'])
        return result
    def request(self, command, **values):
        self.process.stdin.write(json.dumps(dict(command=command, **values)) + '\n')
        self.process.stdin.flush()
        return self._read()
    def batch(self):
        shape = self.request('batch')
        count, width = shape['Count'], shape['ObservationWidth']
        observations = np.frombuffer(self.map, np.float32, count * width).reshape(count, width).copy()
        counts = np.frombuffer(self.map, np.int32, count, offset=count * width * 4).copy()
        offset = count * (width + 1) * 4
        # Copy the packed publication once; row slices remain zero-copy views of this owned array.
        flat = np.frombuffer(self.map, np.float32, int(counts.sum()) * ACTION_WIDTH, offset=offset).reshape(-1, ACTION_WIDTH).copy()
        actions = []
        start = 0
        for n in counts:
            actions.append(flat[start:start + n])
            start += n
        return observations, actions
    def close(self):
        if self.process.poll() is None:
            self.request('stop')
            self.process.wait(timeout=30)
        if hasattr(self, 'map'):
            self.map._mmap.close()

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

class Policy(nn.Module):
    def __init__(self, width, species):
        super().__init__()
        self.core_width=width
        self.species = nn.Embedding(species + 1, 32)
        self.entity = nn.Sequential(nn.Linear(49, 64), nn.ReLU())
        self.state = nn.Sequential(nn.Linear(width - 270 + 15 * 64 + 128, 256), nn.ReLU(), nn.Linear(256, 256), nn.ReLU())
        self.action = nn.Sequential(nn.Linear(ACTION_WIDTH, 64), nn.ReLU())
        self.score = nn.Sequential(nn.Linear(320, 128), nn.ReLU(), nn.Linear(128, 1))
        self.value = nn.Linear(256, 1)
        self.tokens=nn.Sequential(nn.Linear(TOKEN_WIDTH,64),nn.ReLU())
        # Score the actual source and destination, rather than requiring the state
        # MLP to rediscover dynamic slot indexing from scalar action identifiers.
        self.context=nn.Sequential(nn.Linear(448,128),nn.ReLU(),nn.Linear(128,1))
        nn.init.zeros_(self.context[-1].weight); nn.init.zeros_(self.context[-1].bias)
    def forward(self, observations, actions, mask):
        entities = observations[:, 16:286].reshape(-1, 15, 18)
        entity_slots = self.entity(torch.cat([self.species(entities[:, :, 0].long()), entities[:, :, 1:]], dim=-1))
        entity = entity_slots.flatten(1)
        count=observations[:,self.core_width].long() if observations.shape[1]>self.core_width else torch.zeros(len(observations),device=observations.device,dtype=torch.long)
        if observations.shape[1]>self.core_width+1:
            tokens=observations[:,self.core_width+1:].reshape(len(observations),-1,TOKEN_WIDTH)
            encoded=self.tokens(tokens)
            valid=torch.arange(tokens.shape[1],device=observations.device)[None,:]<count[:,None]
            encoded=encoded*valid[:,:,None]
            pooled=torch.cat([encoded.sum(1)/count.clamp(min=1)[:,None],encoded.amax(1)],dim=-1)
        else:pooled=torch.zeros(len(observations),128,device=observations.device)
        state = self.state(torch.cat([observations[:, :16], entity, observations[:, 286:self.core_width],pooled], dim=-1))
        encoded_actions = self.action(actions)
        logits = self.score(torch.cat([state[:, None, :].expand(-1, actions.shape[1], -1), encoded_actions], dim=-1)).squeeze(-1)
        source=(actions[:,:,9]*11).round().long()-1
        target=(actions[:,:,10]*11).round().long()-1
        shop=actions[:,:,0]+actions[:,:,1]>0
        source=torch.where(shop,source+10,source)
        source_valid=(source>=0)&(source<15)&(shop|(source<10))
        target_valid=(target>=0)&(target<10)
        source_context=entity_slots.gather(1,source.clamp(0,14)[:,:,None].expand(-1,-1,64))*source_valid[:,:,None]
        target_context=entity_slots.gather(1,target.clamp(0,14)[:,:,None].expand(-1,-1,64))*target_valid[:,:,None]
        logits=logits+self.context(torch.cat([state[:,None,:].expand(-1,actions.shape[1],-1),encoded_actions,source_context,target_context],dim=-1)).squeeze(-1)
        # 90 equivalent move candidates must not crowd out a single Finish.
        kinds=actions[:,:,:9]*mask[:,:,None]
        kind_counts=kinds.sum(1)
        multiplicity=(kinds*kind_counts[:,None,:]).sum(-1).clamp(min=1)
        logits=logits-multiplicity.log()
        logits = logits.masked_fill(~mask, -torch.inf)
        return Categorical(logits=logits,validate_args=False), self.value(state).squeeze(-1)

class PopulationInference:
    """One device transfer and batched forward for four independent policies."""
    def __init__(self,policies):
        self.device=next(policies[0].parameters()).device
        params,self.buffers=torch.func.stack_module_state(policies)
        self.params={name:value.detach() for name,value in params.items()}
        self.model=policies[0]
        def forward(params,buffers,obs,features,mask):
            distribution,value=torch.func.functional_call(self.model,(params,buffers),(obs,features,mask))
            return distribution.logits,value
        self.forward=torch.vmap(forward,in_dims=(0,0,0,0,0))

    def __call__(self,observations,actions):
        prepared,_,_=self.prepare(observations,actions)
        return self.infer(prepared)

    def prepare(self,observations,actions):
        packing_start=time.perf_counter()
        count=len(observations);groups=(count+3)//4;total=groups*4
        if total>count:
            observations=np.pad(observations,((0,total-count),(0,0)))
            actions=actions+[np.zeros((1,ACTION_WIDTH),np.float32) for _ in range(total-count)]
        feature_values,mask_values=padded_arrays(actions);maximum=feature_values.shape[1]
        packing_seconds=time.perf_counter()-packing_start
        transfer_start=time.perf_counter()
        obs=torch.as_tensor(observations,device=self.device).reshape(groups,4,-1).transpose(0,1)
        features=torch.as_tensor(feature_values,device=self.device);mask=torch.as_tensor(mask_values,device=self.device)
        transfer_seconds=time.perf_counter()-transfer_start
        features=features.reshape(groups,4,maximum,ACTION_WIDTH).transpose(0,1)
        mask=mask.reshape(groups,4,maximum).transpose(0,1)
        return (obs,features,mask,count,total,maximum),packing_seconds,transfer_seconds

    def infer(self,prepared):
        obs,features,mask,count,total,maximum=prepared
        logits,values=self.forward(self.params,self.buffers,obs,features,mask)
        logits=logits.transpose(0,1).reshape(total,maximum)[:count]
        values=values.transpose(0,1).reshape(total)[:count]
        return Categorical(logits=logits,validate_args=False),values

def load_policy(policy, weights):
    missing, unexpected=policy.load_state_dict(weights,strict=False)
    allowed={f'context.{layer}.{field}' for layer in (0,2) for field in ('weight','bias')}
    if unexpected or set(missing)-allowed:
        raise ValueError(f'Incompatible policy weights: missing={missing}, unexpected={unexpected}')

def optimizer_for(policy, device, learning_rate=3e-4):
    return torch.optim.Adam(policy.parameters(),lr=learning_rate,eps=1e-5,fused=device=='cuda')

def configure_compute(config):
    torch.set_num_threads(1)
    torch.set_float32_matmul_precision('high' if config.get('tf32',True) else 'highest')

def pack_rollout(transitions, advantage, returns, policy, environments, device):
    examples=[(t,j) for t in range(len(transitions)) for j in range(policy,environments,4)]
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
    losses=[]; kls=[]; entropies=[]; clips=[]; epochs=0
    size=len(data[0]); device=data[0].device
    for _ in range(config.get('epochs',4)):
        permutation=torch.randperm(size,device=device);epoch_kls=[]
        for offset in range(0,size,config['minibatch']):
            indices=permutation[offset:offset+config['minibatch']]
            obs,features,mask,selected,old_log,adv,target=(x[indices] for x in data)
            distribution,value=policy(obs,features,mask)
            log_ratio=distribution.log_prob(selected)-old_log
            ratio=log_ratio.exp();clip=config.get('clip',.2)
            actor=-torch.minimum(ratio*adv,ratio.clamp(1-clip,1+clip)*adv).mean()
            uncertainty=exploration_entropy(distribution,features,mask).mean()
            loss=actor+.5*(value-target).square().mean()-entropy*uncertainty
            optimizer.zero_grad(set_to_none=True);loss.backward()
            nn.utils.clip_grad_norm_(policy.parameters(),.5,error_if_nonfinite=True);optimizer.step()
            losses.append(loss.detach());entropies.append(uncertainty.detach())
            epoch_kls.append(((ratio-1)-log_ratio).mean().detach())
            clips.append(((ratio-1).abs()>clip).float().mean().detach())
        epochs+=1
        epoch_kl=torch.stack(epoch_kls).mean();kls.extend(epoch_kls)
        # Synchronize once per epoch, not once for every metric of every minibatch.
        if float(epoch_kl.cpu())>config.get('target_kl',.02):break
    summary=torch.stack([torch.stack(x).mean() for x in (losses,kls,entropies,clips)]).cpu().numpy()
    if not np.isfinite(summary).all():raise RuntimeError('Non-finite PPO metrics; checkpoint retained for diagnosis.')
    return dict(loss=float(summary[0]),kl=float(summary[1]),entropy=float(summary[2]),clip_fraction=float(summary[3]),epochs=epochs)

def device_benchmark(model, observation, actions):
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

def advantages(rewards, terminals, values, tail, gamma=1., lam=.95):
    result = np.zeros_like(rewards)
    carry = np.zeros(rewards.shape[1], dtype=np.float32)
    for t in reversed(range(len(rewards))):
        next_value = tail if t == len(rewards) - 1 else values[t + 1]
        live = 1 - terminals[t]
        delta = rewards[t] + gamma * next_value * live - values[t]
        carry = delta + gamma * lam * live * carry
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
    config.setdefault('gae_lambda',.99);config.setdefault('entropy_coefficient',.002)
    config.setdefault('shop_decision_limit',64);config.setdefault('target_kl',.02)
    # A CPU-only startup probe cannot judge GPU batch efficiency. Keep the
    # requested environment budget for CUDA/Auto; CPU may tune it down.
    config.setdefault('auto_calibrate',not config.get('resume',False))
    config.setdefault('auto_tune_environments',config['auto_calibrate'] and not config.get('resume',False))
    saved_checkpoint = checkpoint_data(config) if config.get('resume') else None
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
        evaluation_path=output/'evaluation.json'
        if evaluation_path.exists():
            evaluation=json.loads(evaluation_path.read_text())
            if evaluation.get('rules_hash')==host.ready['RulesHash'] and evaluation.get('completed'):
                for score in evaluation['policies']:
                    if score['runs']>=20 and score['success_rate']>metrics.setdefault('champion_scores',{}).get(str(score['policy']),-1):
                        host.request('promote',policy=score['policy'],generation=max(0,(evaluation['update']-1)//10))
                        metrics['champion_scores'][str(score['policy'])]=score['success_rate']
        ranking_path=output/'generation-ranking.json'
        if ranking_path.exists():
            ranking=json.loads(ranking_path.read_text())
            if ranking.get('rules_hash')==host.ready['RulesHash'] and ranking.get('best'):
                champion=ranking['best'];policy=max(champion['policies'],key=lambda row:(row['confidence_interval_95'][0],row['average_wins'],-row['unused_deployable_bench']))
                host.request('promote',policy=policy['policy'],generation=champion['generation'])
                metrics['ranked_champion']=dict(generation=champion['generation'],policy=policy['policy'],update=champion['update'],score=policy['confidence_interval_95'][0])
    atomic_json(output / 'configuration.json', config)
    start = time.monotonic(); checkpoint_at = start; decisions = 0; last_saved = None
    battles=deployed=deployable_bench=limited_turns=deployment_improving=deployment_reversing=finish_with_gap=0
    action_counts={};opponent_mix={};recent=deque(maxlen=2000)
    stage_totals={key:0. for key in ('batch','python_packing','host_to_device','cuda_inference','device_to_host','inference','step','encoding','buffer','simulation','history')}
    initial_metrics={key:metrics[key] for key in ('runs','successes','total_wins')}
    try:
        # A failure before the first five-minute save must still leave a valid
        # paired learner/host checkpoint. Do not snapshot a partially failed step.
        last_saved=save_checkpoint(output,host,policies,optimizers,update,config,metrics)
        checkpoint_at=time.monotonic()
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
                    forward_start=time.perf_counter();distribution,value=population.infer(prepared);choice=distribution.sample()
                    device_numeric=torch.stack([choice.float(),distribution.log_prob(choice),value],dim=1)
                    stage_totals['cuda_inference']+=time.perf_counter()-forward_start
                    transfer_back_start=time.perf_counter();numeric=device_numeric.cpu().numpy()
                    stage_totals['device_to_host']+=time.perf_counter()-transfer_back_start
                stage_totals['inference']+=time.perf_counter()-inference_start
                selected=numeric[:,0].astype(np.int64);log_prob=numeric[:,1];values=numeric[:,2]
                step_start=time.monotonic()
                result = host.request('step', actions=selected.tolist())
                stage_totals['step']+=time.monotonic()-step_start
                transitions.append((observation, actions, selected, log_prob, values, np.array(result['Rewards'], np.float32), np.array(result['Terminals'], np.float32)))
                decisions += config['environments']
                battles+=result['Battles'];deployed+=result['Deployed'];deployable_bench+=result['DeployableBench'];limited_turns+=result['LimitedTurns']
                deployment_improving+=result.get('DeploymentImproving',0);deployment_reversing+=result.get('DeploymentReversing',0);finish_with_gap+=result.get('FinishWithDeployableBench',0)
                for key,value in result.get('ActionCounts',{}).items():action_counts[key]=action_counts.get(key,0)+value
                for opponent in result.get('Opponents',[]):opponent_mix[opponent['Kind']]=opponent_mix.get(opponent['Kind'],0)+1
                for key,source in (('encoding','EncodingMilliseconds'),('buffer','BufferMilliseconds'),('simulation','SimulationMilliseconds'),('history','HistoryMilliseconds')):stage_totals[key]+=result.get(source,0)/1000
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
            advantage, returns = advantages(np.stack([x[5] for x in transitions]), np.stack([x[6] for x in transitions]), np.stack([x[4] for x in transitions]), tail,lam=config['gae_lambda'])
            optimization=[]
            for i in range(4):
                if i>=config['environments']:continue
                data=pack_rollout(transitions,advantage,returns,i,config['environments'],device)
                optimization.append(optimize(policies[i],optimizers[i],data,config,config['entropy_coefficient']))
                del data
            optimization_seconds=time.monotonic()-optimization_start
            update += 1
            generation = update // 10
            host.request('generation', value=generation)
            if update % 10 == 0:
                torch.save([p.state_dict() for p in policies], output / f'generation-{generation}.pt')
                for old in output.glob('generation-*.pt'):
                    if re.fullmatch(r'generation-\d+',old.stem) and old.resolve().parent==output and int(old.stem.split('-')[1]) < generation - 19: old.unlink()
            if time.monotonic() - checkpoint_at >= 300:
                last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics); checkpoint_at = time.monotonic()
            progress = dict(status='Training', update=update, generation=generation, device=device, device_seconds=timings, decisions=decisions,
                            decisions_per_second=decisions / max(.01, time.monotonic() - start), loss=float(np.mean([x['loss'] for x in optimization])), checkpoint=last_saved,
                            ppo=optimization,rollout_seconds=rollout_seconds,optimization_seconds=optimization_seconds,model_version=MODEL_VERSION,objective_version=OBJECTIVE_VERSION,
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
            with (output / 'metrics.jsonl').open('a', encoding='utf-8') as file: file.write(json.dumps(progress) + '\n')
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
    if saved['protocol'] != PROTOCOL: raise ValueError('Unsupported checkpoint protocol.')
    if saved.get('encoding')!=ENCODING or saved['policies'][0]['action.0.weight'].shape[1]!=ACTION_WIDTH:raise ValueError('Incompatible observation/action encoding.')
    rules = json.loads(Path(config['rules']).read_text(encoding='utf-8-sig'))
    if rules['Hash'] != saved['rules_hash']:
        raise ValueError('Rules hash differs from checkpoint.')
    return output, directory, saved, rules

def export(config):
    output, directory, saved, rules = checkpoint_data(config)
    target = output / 'export'
    target.mkdir(exist_ok=True)
    for i, weights in enumerate(saved['policies']): torch.save(weights, target / f'policy-{i}.pt')
    atomic_json(target / 'metadata.json', dict(protocol=PROTOCOL, encoding=ENCODING,model_version=saved.get('model_version',1),objective_version=saved.get('objective_version',1),action_kind_balancing=saved.get('model_version',1)>=2, rules_hash=rules['Hash'], simulation_revision=rules.get('SimulationRevision',2),species=rules['Species'],
                items=rules['Items'], trinkets=rules['Trinkets'], trainers=rules['Trainers'], entity_order=[x['Id'] for x in rules['Species']],
                observation_width=16+270+len(rules['Trinkets'])+len(rules['Trainers']), action_width=ACTION_WIDTH, update=saved['update'],
                numeric_schema=encoding_metadata(rules),ragged_observations=True,observation_length_formula="observation_width + 1 + token_count * 12",
                parameters=rules['Parameters'],profile=rules.get('Profile'),evidence=rules.get('Evidence'),encounters=rules.get('Encounters'),full_fidelity_ready=rules.get('FullFidelityReady',False),
                diagnostic_fixture=rules['Build']=='fixture'))
    atomic_json(output / 'progress.json', dict(status='Exported', update=saved['update'], diagnostic_fixture=rules['Build']=='fixture'))

def encoding_metadata(rules):
    """Describe every existing column, including padding, without claiming missing game inputs are encoded."""
    headers=['day','lives','wins','gold','max_hp','rank','free_rerolls','item_uses','maximum_item_uses',
             'locked','phase','choice_count','overflow_present','next_battle_start_repeat','next_victory_gold','next_copy_enemy']
    entity=['species_id','level','shiny','copies','base_cooldown','damage','heal','shield','burn','poison','shock',
            'speed','multicast','sell_value','rarity','added_types_bitmask','shop_item_id','shop_price']
    return dict(headers=headers,header_divisors=[20,10,10,100,1000,14,10,5,5,1,6,10,1,1,100,1],
                entities=dict(offset=16,count=15,width=18,order=['team:0..5','bench:0..3','shop:0..4'],fields=entity,
                              divisors=[1,4,1,3,10,100,100,100,100,100,100,10,10,10,5,1,1,100]),
                trinket_counts=dict(offset=286,order=[x['Id'] for x in rules['Trinkets']]),
                trainer_flags=dict(offset=286+len(rules['Trinkets']),order=[x['Id'] for x in rules['Trainers']]),
                type_bits=sorted({t for s in rules['Species'] for t in s['Types']}),
                definition_ids='One-based catalog order; zero denotes absent.',
                actions=dict(move_one_hot=['Buy','BuyItem','Sell','Move','Merge','Reroll','Lock','Choose','Finish'],
                             fields={9:'(source+1)/11',10:'(target+1)/11',11:'(choice+1)/10',12:'offer_price/100',
                                     13:'offered_species_id',14:'offered_level/4',15:'offered_shiny',16:'offered_item_id',
                                     17:'chosen_trinket_id',18:'chosen_trainer_id',19:'first_choice_effect_operation',
                                     20:'first_choice_effect_stat',21:'first_choice_effect_amount',22:'first_choice_effect_target',
                                     23:'granted_species_id',24:'granted_level/4',25:'granted_shiny',26:'grant_stat_bonus',27:'grant_stat',
                                     28:'resolves_at_shop_start',29:'choice_requires_target'},
                             choice_fingerprint_limbs=[30,31,32,33],extra_fields={34:'choice_effect_count',35:'source_species_id',36:'target_species_id',37:'deployed_delta',38:'deployable_bench_delta',39:'finish_with_deployable_bench'},selection='Index into the complete ordered legal candidate list.'),
                state_tokens=dict(offset=16+270+len(rules['Trinkets'])+len(rules['Trainers']),width=TOKEN_WIDTH,
                    fields=['json_kind/8','path_fingerprint_0..3','string_fingerprint_0..3','signed_log_numeric_or_species_id','depth/16','ordinal/1000'],
                    fingerprint='FNV-1a 64-bit UTF-16 code units split into four exact 16-bit limbs divided by 65535',
                    contents='Ordered bag and trainer entries, every visible counter, added types, stored discounts, stored devoured creatures, complete choice/effect parameters, overflow, foreign pools and visible pending effects. Ragged batches are padded without truncation.'),
                limitations=['Hidden randomness, pending future choice batches and future opponent boards are excluded.'])

def evaluate(config, runs=128, initial=False):
    output, directory, saved, rules = checkpoint_data(config)
    evaluation_config = dict(config, session=uuid.uuid4().hex[:12],evaluation=True, initial_evaluation=initial,seed=config['seed']+1000000007, environments=4,history=str(output/'history'))
    torch.manual_seed(evaluation_config['seed']); configure_compute(config)
    host = Host(evaluation_config, output / 'evaluation')
    # Evaluation histories share the viewer's index, while buffers and control files remain separate.
    try:
        host.request('league_restore', path=str(directory / 'host.json'))
        width = host.ready['ObservationWidth']
        device = 'cuda' if config['device'] != 'cpu' and torch.cuda.is_available() else 'cpu'
        if initial:torch.manual_seed(config['seed'])
        policies = [Policy(width,len(rules['Species'])).to(device) for _ in range(4)]
        for i in range(4):
            if not initial:load_policy(policies[i],saved['policies'][i])
            policies[i].eval()
        torch.manual_seed(evaluation_config['seed'])
        with torch.inference_mode():population=PopulationInference(policies)
        results=[];opponent_results={}; decisions=0; start=time.monotonic(); quota=max(1,(runs+3)//4);counts=[0]*4
        battles=deployed=deployable_bench=limited_turns=0
        atomic_json(output/'control.json',dict(command='evaluate'))
        decision_limit=config.get('evaluation_decision_limit',max(100000,runs*4000))
        while min(counts)<quota and decisions<decision_limit:
            if (output/'control.json').exists() and json.loads((output/'control.json').read_text()).get('command')=='stop':break
            observation,actions=host.batch()
            with torch.inference_mode():
                distribution,_=population(observation,actions)
                selected=(distribution.sample() if config.get('evaluation_sampling',False) else distribution.probs.argmax(-1)).cpu().tolist()
            result=host.request('step',actions=selected); decisions+=4
            battles+=result['Battles'];deployed+=result['Deployed'];deployable_bench+=result['DeployableBench'];limited_turns+=result['LimitedTurns']
            for opponent in result.get('Opponents',[]):
                if counts[opponent['Policy']]>=quota:continue
                key=f"{opponent['Kind']}:generation-{opponent['Generation']}"
                row=opponent_results.setdefault(key,dict(battles=0,wins=0));row['battles']+=1;row['wins']+=int(opponent['Won'])
            for completed in result['Runs']:
                summary=completed['Summary'];policy=summary['Policy']
                if counts[policy]<quota: results.append(summary);counts[policy]+=1
            if decisions%1000==0: atomic_json(output/'progress.json',dict(status='Evaluating',runs=len(results),target=runs,diagnostic_fixture=rules['Build']=='fixture'))
        score=sum(x['Success'] for x in results)/max(1,len(results))
        policy_results=[]
        for i in range(4):
            subset=[x for x in results if x['Policy']==i]
            policy_results.append(dict(policy=i,runs=len(subset),success_rate=sum(x['Success'] for x in subset)/max(1,len(subset)),
                                       average_wins=sum(x['Wins'] for x in subset)/max(1,len(subset))))
        trainer_results={}
        for summary in results:
            row=trainer_results.setdefault(summary['Trainer'],dict(runs=0,successes=0,wins=0));row['runs']+=1;row['successes']+=int(summary['Success']);row['wins']+=summary['Wins']
        report=dict(opponents=opponent_results,trainers=trainer_results,status='Evaluated' ,update=0 if initial else saved['update'],initial_policy=initial,rules_hash=rules['Hash'],runs=len(results),success_rate=score,
                    average_wins=sum(x['Wins'] for x in results)/max(1,len(results)),confidence_interval_95=wilson(sum(x['Success'] for x in results),len(results)),policies=policy_results,
                    completed=min(counts)>=quota,decisions=decisions,seconds=time.monotonic()-start,diagnostic_fixture=rules['Build']=='fixture',
                    selection='sample' if config.get('evaluation_sampling',False) else 'greedy',average_deployed=deployed/max(1,battles),
                    unused_deployable_bench=deployable_bench/max(1,battles),limited_turn_rate=limited_turns/max(1,battles))
        atomic_json(output/('evaluation-initial.json' if initial else 'evaluation.json'),report); atomic_json(output/'progress.json',report)
    finally: host.close()

def available_snapshots(output, saved, seed):
    snapshots=[dict(label='Initial',update=0,generation=0,weights=None)]
    for path in sorted(output.glob('generation-*.pt'),key=lambda p:int(p.stem.split('-')[1])):
        generation=int(path.stem.split('-')[1]);snapshots.append(dict(label=f'Generation {generation}',update=generation*10,generation=generation,
                                                                       weights=torch.load(path,map_location='cpu',weights_only=True)))
    if not any(row['update']==saved['update'] for row in snapshots):
        snapshots.append(dict(label=f"Current update {saved['update']}",update=saved['update'],generation=saved['update']//10,weights=saved['policies']))
    return snapshots

def evaluate_snapshot(config, output, checkpoint_directory, rules, snapshot, runs, batch_id):
    evaluation_seed=config['seed']+1000000007
    evaluation_config=dict(config,session=f"rank-{batch_id}-{snapshot['update']}",evaluation=True,initial_evaluation=snapshot['weights'] is None,
                           seed=evaluation_seed,environments=4,history=str(output/'history'),auto_calibrate=False,auto_tune_environments=False,
                           snapshot_update=snapshot['update'],snapshot_generation=snapshot['generation'],evaluation_batch=batch_id)
    target=output/'ranking'/f"update-{snapshot['update']}";target.mkdir(parents=True,exist_ok=True)
    torch.manual_seed(config['seed'] if snapshot['weights'] is None else evaluation_seed);host=Host(evaluation_config,target)
    try:
        host.request('league_restore',path=str(checkpoint_directory/'host.json'));device='cuda' if config['device']!='cpu' and torch.cuda.is_available() else 'cpu'
        width=host.ready['ObservationWidth'];policies=[Policy(width,len(rules['Species'])).to(device) for _ in range(4)]
        if snapshot['weights'] is not None:
            for policy,weights in zip(policies,snapshot['weights']):load_policy(policy,weights)
        for policy in policies:policy.eval()
        torch.manual_seed(evaluation_seed);population=PopulationInference(policies)
        quota=max(1,(runs+3)//4);counts=[0]*4;results=[];decisions=0;start=time.monotonic()
        policy_battles=[0]*4;policy_deployed=[0]*4;policy_bench=[0]*4;policy_limited=[0]*4
        opponent_results={};decision_limit=config.get('evaluation_decision_limit',max(100000,runs*4000))
        while min(counts)<quota and decisions<decision_limit:
            observation,actions=host.batch()
            with torch.inference_mode():distribution,_=population(observation,actions);selected=distribution.probs.argmax(-1).cpu().tolist()
            step=host.request('step',actions=selected);decisions+=4
            for row in step.get('PolicyBattleStats',[]):
                i=row['Policy'];policy_battles[i]+=row['Battles'];policy_deployed[i]+=row['Deployed'];policy_bench[i]+=row['DeployableBench'];policy_limited[i]+=row['LimitedTurns']
            for opponent in step.get('Opponents',[]):
                key=f"{opponent['Kind']}:generation-{opponent['Generation']}";row=opponent_results.setdefault(key,dict(battles=0,wins=0));row['battles']+=1;row['wins']+=int(opponent['Won'])
            for completed in step['Runs']:
                summary=completed['Summary'];policy=summary['Policy']
                if counts[policy]<quota:results.append(summary);counts[policy]+=1
        policies_report=[]
        for i in range(4):
            subset=[row for row in results if row['Policy']==i];successes=sum(int(row['Success']) for row in subset);battle_count=max(1,policy_battles[i])
            policies_report.append(dict(policy=i,runs=len(subset),success_rate=successes/max(1,len(subset)),confidence_interval_95=wilson(successes,len(subset)),
                                        average_wins=sum(row['Wins'] for row in subset)/max(1,len(subset)),average_deployed=policy_deployed[i]/battle_count,
                                        unused_deployable_bench=policy_bench[i]/battle_count,limited_turn_rate=policy_limited[i]/battle_count))
        successes=sum(int(row['Success']) for row in results);total_battles=max(1,sum(policy_battles));interval=wilson(successes,len(results))
        return dict(label=snapshot['label'],update=snapshot['update'],generation=snapshot['generation'],runs=len(results),success_rate=successes/max(1,len(results)),
                    confidence_interval_95=interval,score=interval[0],average_wins=sum(row['Wins'] for row in results)/max(1,len(results)),
                    average_deployed=sum(policy_deployed)/total_battles,unused_deployable_bench=sum(policy_bench)/total_battles,
                    limited_turn_rate=sum(policy_limited)/total_battles,seconds=time.monotonic()-start,completed=min(counts)>=quota,
                    policies=policies_report,opponents=opponent_results)
    finally:host.close()

def rank_generations(config, runs=128):
    output,directory,saved,rules=checkpoint_data(config);batch_id=uuid.uuid4().hex[:12]
    snapshots=available_snapshots(output,saved,config['seed']);rows=[]
    for index,snapshot in enumerate(snapshots):
        atomic_json(output/'progress.json',dict(status='Ranking generations',snapshot=snapshot['label'],completed=index,total=len(snapshots)))
        rows.append(evaluate_snapshot(config,output,directory,rules,snapshot,runs,batch_id))
    ordered=sorted(rows,key=ranking_key,reverse=True)
    for rank,row in enumerate(ordered,1):row['rank']=rank
    report=dict(status='Ranked',evaluation_batch=batch_id,rules_hash=rules['Hash'],runs_per_snapshot=runs,selection='greedy',snapshots=ordered,best=ordered[0])
    atomic_json(output/'generation-ranking.json',report);atomic_json(output/'progress.json',report)

def export_snapshot(config, requested_update=None):
    output,directory,saved,rules=checkpoint_data(config);weights=saved['policies'];update=saved['update']
    if requested_update is not None and requested_update!=update:
        if requested_update==0:
            torch.manual_seed(config['seed']);weights=[policy.state_dict() for policy in [Policy(saved['policies'][0]['state.0.weight'].shape[1]-128+270-15*64,len(rules['Species'])) for _ in range(4)]]
        else:
            path=output/f'generation-{requested_update//10}.pt'
            if requested_update%10 or not path.exists():raise ValueError('Requested ranked snapshot is unavailable.')
            weights=torch.load(path,map_location='cpu',weights_only=True)
        update=requested_update
    target=output/'export';target.mkdir(exist_ok=True)
    for i,state in enumerate(weights):torch.save(state,target/f'policy-{i}.pt')
    atomic_json(target/'metadata.json',dict(protocol=PROTOCOL,encoding=ENCODING,model_version=MODEL_VERSION,objective_version=OBJECTIVE_VERSION,action_kind_balancing=True,
                rules_hash=rules['Hash'],simulation_revision=rules.get('SimulationRevision',2),species=rules['Species'],items=rules['Items'],trinkets=rules['Trinkets'],trainers=rules['Trainers'],
                entity_order=[x['Id'] for x in rules['Species']],observation_width=16+270+len(rules['Trinkets'])+len(rules['Trainers']),action_width=ACTION_WIDTH,update=update,
                numeric_schema=encoding_metadata(rules),ragged_observations=True,observation_length_formula='observation_width + 1 + token_count * 12',parameters=rules['Parameters'],
                profile=rules.get('Profile'),evidence=rules.get('Evidence'),encounters=rules.get('Encounters'),full_fidelity_ready=rules.get('FullFidelityReady',False),diagnostic_fixture=rules['Build']=='fixture'))
    atomic_json(output/'progress.json',dict(status='Exported',update=update,diagnostic_fixture=rules['Build']=='fixture'))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    parser.add_argument('--evaluate', action='store_true')
    parser.add_argument('--export', action='store_true')
    parser.add_argument('--runs',type=int,default=128)
    parser.add_argument('--initial',action='store_true')
    parser.add_argument('--rank-generations',action='store_true')
    parser.add_argument('--export-update',type=int)
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    try:
        if args.rank_generations:rank_generations(config,args.runs)
        elif args.evaluate:
            Path(config['output'],'evaluation').mkdir(parents=True,exist_ok=True)
            evaluate(config,args.runs,args.initial)
        elif args.export: export_snapshot(config,args.export_update)
        else: run(config)
    except Exception as error:
        atomic_json(Path(config['output']) / 'progress.json', dict(status='Failed', error=str(error)))
        raise

if __name__ == '__main__': main()
