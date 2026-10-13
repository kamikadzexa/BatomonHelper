"""Standalone population PPO; .NET owns gameplay and numeric batch publication."""
from __future__ import annotations
import argparse
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
ENCODING = 3
ACTION_WIDTH = 40
TOKEN_WIDTH = 12

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
                                         'tune-environments' if config.get('auto_tune_environments',True) and not config.get('resume') else 'fixed-environments',config.get('session','legacy')], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=0x08000000 if os.name == 'nt' else 0)
        self.ready = self._read()
        if self.ready.get("Encoding")!=ENCODING:raise RuntimeError("Incompatible numeric protocol encoding.")
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
        flat = np.frombuffer(self.map, np.float32, int(counts.sum()) * ACTION_WIDTH, offset=offset).reshape(-1, ACTION_WIDTH)
        actions = []
        start = 0
        for n in counts:
            actions.append(flat[start:start + n].copy())
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

def padded(actions, device):
    maximum = max(map(len, actions))
    values = np.zeros((len(actions), maximum, ACTION_WIDTH), dtype=np.float32)
    mask = np.zeros((len(actions), maximum), dtype=np.bool_)
    for i, array in enumerate(actions):
        values[i, :len(array)] = array
        mask[i, :len(array)] = True
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
    def forward(self, observations, actions, mask):
        entities = observations[:, 16:286].reshape(-1, 15, 18)
        entity = self.entity(torch.cat([self.species(entities[:, :, 0].long()), entities[:, :, 1:]], dim=-1)).flatten(1)
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
        logits = logits.masked_fill(~mask, -torch.inf)
        return Categorical(logits=logits), self.value(state).squeeze(-1)

def device_benchmark(model, observation, actions):
    measurements = {}
    for device in ['cpu'] + (['cuda'] if torch.cuda.is_available() else []):
        candidate = copy.deepcopy(model).to(device)
        optimizer=torch.optim.Adam(candidate.parameters(),lr=3e-4)
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
    torch.save(dict(protocol=PROTOCOL,encoding=ENCODING,action_width=ACTION_WIDTH, rules_hash=host.ready['RulesHash'], update=update, config=config,
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
    torch.set_num_threads(1)
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
    policies = [p.to(device) for p in policies]
    optimizers = [torch.optim.Adam(p.parameters(), lr=3e-4) for p in policies]
    update = 0; metrics = dict(runs=0, successes=0, total_wins=0, trainers={})
    if saved_checkpoint:
        _, directory, saved, _ = saved_checkpoint
        if saved['rules_hash'] != host.ready['RulesHash'] or saved['config']['environments'] != config['environments'] or saved['config']['seed'] != config['seed']:
            raise RuntimeError('Rules hash, seed or environment count differs from checkpoint.')
        for i in range(4):
            policies[i].load_state_dict(saved['policies'][i]); optimizers[i].load_state_dict(saved['optimizers'][i])
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
    atomic_json(output / 'configuration.json', config)
    start = time.monotonic(); checkpoint_at = start; decisions = 0; last_saved = None
    try:
        while not config.get('updates') or update < config['updates']:
            control = json.loads((output / 'control.json').read_text()) if (output / 'control.json').exists() else {}
            if control.get('command') == 'stop' or config.get('minutes', 0) > 0 and time.monotonic() - start >= config['minutes'] * 60:
                break
            if control.get('command') == 'pause':
                last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics)
                atomic_json(output / 'progress.json', dict(status='Paused', update=update, checkpoint=last_saved, **metrics))
                while json.loads((output / 'control.json').read_text()).get('command') == 'pause': time.sleep(.2)
                continue
            transitions = []
            for _ in range(config['rollout']):
                observation, actions = host.batch()
                selected = np.zeros(config['environments'], dtype=np.int64)
                log_prob = np.zeros_like(selected, dtype=np.float32); values = log_prob.copy()
                for i in range(4):
                    subset = np.arange(i, config['environments'], 4)
                    if not len(subset): continue
                    features, mask = padded([actions[j] for j in subset], device)
                    with torch.no_grad():
                        distribution, value = policies[i](torch.as_tensor(observation[subset], device=device), features, mask)
                        choice = distribution.sample()
                    selected[subset] = choice.cpu().numpy(); log_prob[subset] = distribution.log_prob(choice).cpu().numpy(); values[subset] = value.cpu().numpy()
                result = host.request('step', actions=selected.tolist())
                transitions.append((observation, actions, selected, log_prob, values, np.array(result['Rewards'], np.float32), np.array(result['Terminals'], np.float32)))
                decisions += config['environments']
                for record in result['Runs']:
                    summary = record['Summary']; metrics['runs'] += 1; metrics['successes'] += int(summary['Success']); metrics['total_wins'] += summary['Wins']
                    trainer = metrics['trainers'].setdefault(summary['Trainer'], dict(runs=0, successes=0, wins=0))
                    trainer['runs'] += 1; trainer['successes'] += int(summary['Success']); trainer['wins'] += summary['Wins']
                    cohort=metrics.setdefault('generations',{}).setdefault(str(summary['Generation']),dict(runs=0,successes=0,wins=0))
                    cohort['runs']+=1;cohort['successes']+=int(summary['Success']);cohort['wins']+=summary['Wins']
            observation, actions = host.batch(); tail = np.zeros(config['environments'], np.float32)
            for i in range(4):
                subset = np.arange(i, config['environments'], 4)
                if not len(subset): continue
                features, mask = padded([actions[j] for j in subset], device)
                with torch.no_grad(): _, value = policies[i](torch.as_tensor(observation[subset], device=device), features, mask)
                tail[subset] = value.cpu().numpy()
            advantage, returns = advantages(np.stack([x[5] for x in transitions]), np.stack([x[6] for x in transitions]), np.stack([x[4] for x in transitions]), tail)
            losses = []
            for i in range(4):
                examples = [(t, j) for t in range(len(transitions)) for j in range(i, config['environments'], 4)]
                all_adv = np.array([advantage[t, j] for t, j in examples]); mean, std = all_adv.mean(), all_adv.std() + 1e-8
                for _ in range(4):
                    random.shuffle(examples)
                    for offset in range(0, len(examples), config['minibatch']):
                        batch = examples[offset:offset + config['minibatch']]
                        obs = torch.as_tensor(pad_observations([transitions[t][0][j] for t, j in batch]), device=device)
                        features, mask = padded([transitions[t][1][j] for t, j in batch], device)
                        selected = torch.as_tensor([transitions[t][2][j] for t, j in batch], device=device)
                        old_log = torch.as_tensor(np.array([transitions[t][3][j] for t, j in batch]), device=device)
                        adv = torch.as_tensor(np.array([(advantage[t, j] - mean) / std for t, j in batch]), device=device)
                        target = torch.as_tensor(np.array([returns[t, j] for t, j in batch]), device=device)
                        distribution, value = policies[i](obs, features, mask)
                        ratio = (distribution.log_prob(selected) - old_log).exp()
                        actor = -torch.minimum(ratio * adv, ratio.clamp(.8, 1.2) * adv).mean()
                        loss = actor + .5 * (value - target).square().mean() - .01 * distribution.entropy().mean()
                        if not torch.isfinite(loss): raise RuntimeError('Non-finite PPO loss; checkpoint retained for diagnosis.')
                        optimizers[i].zero_grad(set_to_none=True); loss.backward(); nn.utils.clip_grad_norm_(policies[i].parameters(), .5); optimizers[i].step()
                        losses.append(float(loss.detach().cpu()))
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
                            decisions_per_second=decisions / max(.01, time.monotonic() - start), loss=float(np.mean(losses)), checkpoint=last_saved,
                            actual_workers=host.ready['Workers'],worker_milliseconds=host.ready['WorkerMilliseconds'],actual_environments=host.ready['Count'],environment_milliseconds_per_run=host.ready.get('EnvironmentMillisecondsPerRun',{}),
                            success_rate=metrics['successes'] / max(1, metrics['runs']), average_wins=metrics['total_wins'] / max(1, metrics['runs']),
                            diagnostic_fixture=rules['Build'] == 'fixture', **metrics)
            atomic_json(output / 'progress.json', progress)
            with (output / 'metrics.jsonl').open('a', encoding='utf-8') as file: file.write(json.dumps(progress) + '\n')
        last_saved = save_checkpoint(output, host, policies, optimizers, update, config, metrics)
        atomic_json(output / 'progress.json', dict(status='Stopped', update=update, checkpoint=last_saved, device=device, device_seconds=timings,
                    actual_workers=host.ready['Workers'],worker_milliseconds=host.ready['WorkerMilliseconds'],actual_environments=host.ready['Count'],environment_milliseconds_per_run=host.ready.get('EnvironmentMillisecondsPerRun',{}),
                    decisions=decisions, decisions_per_second=decisions/max(.01,time.monotonic()-start), success_rate=metrics['successes']/max(1,metrics['runs']),
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
    atomic_json(target / 'metadata.json', dict(protocol=PROTOCOL, encoding=ENCODING, rules_hash=rules['Hash'], simulation_revision=rules.get('SimulationRevision',2),species=rules['Species'],
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
                             choice_fingerprint_limbs=[30,31,32,33],extra_fields={34:'choice_effect_count',35:'source_species_id',36:'target_species_id'},zero_padding=[37,38,39],selection='Index into the complete ordered legal candidate list.'),
                state_tokens=dict(offset=16+270+len(rules['Trinkets'])+len(rules['Trainers']),width=TOKEN_WIDTH,
                    fields=['json_kind/8','path_fingerprint_0..3','string_fingerprint_0..3','signed_log_numeric_or_species_id','depth/16','ordinal/1000'],
                    fingerprint='FNV-1a 64-bit UTF-16 code units split into four exact 16-bit limbs divided by 65535',
                    contents='Ordered bag and trainer entries, every visible counter, added types, stored discounts, stored devoured creatures, complete choice/effect parameters, overflow, foreign pools and visible pending effects. Ragged batches are padded without truncation.'),
                limitations=['Hidden randomness, pending future choice batches and future opponent boards are excluded.'])

def evaluate(config, runs=128, initial=False):
    output, directory, saved, rules = checkpoint_data(config)
    evaluation_config = dict(config, session=uuid.uuid4().hex[:12],evaluation=True, initial_evaluation=initial,seed=config['seed']+1000000007, environments=4,history=str(output/'history'))
    torch.manual_seed(evaluation_config['seed']); torch.set_num_threads(1)
    host = Host(evaluation_config, output / 'evaluation')
    # Evaluation histories share the viewer's index, while buffers and control files remain separate.
    try:
        host.request('league_restore', path=str(directory / 'host.json'))
        width = host.ready['ObservationWidth']
        device = 'cuda' if config['device'] != 'cpu' and torch.cuda.is_available() else 'cpu'
        if initial:torch.manual_seed(config['seed'])
        policies = [Policy(width,len(rules['Species'])).to(device) for _ in range(4)]
        for i in range(4):
            if not initial:policies[i].load_state_dict(saved['policies'][i])
            policies[i].eval()
        torch.manual_seed(evaluation_config['seed'])
        results=[];opponent_results={}; decisions=0; start=time.monotonic(); quota=max(1,(runs+3)//4);counts=[0]*4
        atomic_json(output/'control.json',dict(command='evaluate'))
        decision_limit=config.get('evaluation_decision_limit',max(100000,runs*4000))
        while min(counts)<quota and decisions<decision_limit:
            if (output/'control.json').exists() and json.loads((output/'control.json').read_text()).get('command')=='stop':break
            observation,actions=host.batch(); selected=[]
            for i in range(4):
                features,mask=padded([actions[i]],device)
                with torch.no_grad(): distribution,_=policies[i](torch.as_tensor(observation[i:i+1],device=device),features,mask)
                selected.append(int(distribution.sample().item()))
            result=host.request('step',actions=selected); decisions+=4
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
                    completed=min(counts)>=quota,decisions=decisions,seconds=time.monotonic()-start,diagnostic_fixture=rules['Build']=='fixture')
        atomic_json(output/('evaluation-initial.json' if initial else 'evaluation.json'),report); atomic_json(output/'progress.json',report)
    finally: host.close()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    parser.add_argument('--evaluate', action='store_true')
    parser.add_argument('--export', action='store_true')
    parser.add_argument('--runs',type=int,default=128)
    parser.add_argument('--initial',action='store_true')
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    try:
        if args.evaluate:
            Path(config['output'],'evaluation').mkdir(parents=True,exist_ok=True)
            evaluate(config,args.runs,args.initial)
        elif args.export: export(config)
        else: run(config)
    except Exception as error:
        atomic_json(Path(config['output']) / 'progress.json', dict(status='Failed', error=str(error)))
        raise

if __name__ == '__main__': main()
