"""Equal-wall-budget, three-seed ablations against a frozen suite. Never claims a passed gate from training counters."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time
import numpy as np
import torch
from learner import atomic_json,checkpoint_data
from evaluation import EvaluationRunner

def paired_interval(left,right):
    a={row['scenario']:int(row['success']) for row in left};b={row['scenario']:int(row['success']) for row in right}
    if a.keys()!=b.keys():raise ValueError('Paired evaluation scenarios differ.')
    values=np.array([a[key]-b[key] for key in sorted(a)],np.float64)
    if len(values)<16:return dict(mean=float(values.mean()),confidence_interval_95=[-1,1],sufficient_sample=False)
    rng=np.random.default_rng(69);sample=values[rng.integers(0,len(values),(2000,len(values)))].mean(1)
    return dict(mean=float(values.mean()),confidence_interval_95=np.quantile(sample,[.025,.975]).tolist(),sufficient_sample=True)

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--archive',required=True);parser.add_argument('--baseline-root')
    parser.add_argument('--output',default='work/validation-v5');parser.add_argument('--minutes',type=float,default=3)
    parser.add_argument('--runs',type=int,default=128);parser.add_argument('--seeds',default='69,70,71')
    parser.add_argument('--variants',default='full,no-curriculum,legacy');parser.add_argument('--scenario-bank')
    args=parser.parse_args();root=Path(__file__).resolve().parent.parent;output=Path(args.output).resolve();output.mkdir(parents=True,exist_ok=True)
    archive=Path(args.archive).resolve();pointer=json.loads((archive/'checkpoint.json').read_text());suite=archive/pointer['directory']
    rules=json.loads((archive/'rules.json').read_text(encoding='utf-8-sig'));rows=[]
    host=root/'tools/BatomonHelper.TrainingHost/bin/Release/net10.0/BatomonHelper.TrainingHost.dll'
    for variant in args.variants.split(','):
        for seed in map(int,args.seeds.split(',')):
            target=output/f'{variant}-seed-{seed}';target.mkdir(exist_ok=True)
            config=dict(output=str(target),rules=str(archive/'rules.json'),host=str(host),workers=16,environments=1024,seed=seed,minutes=args.minutes,
                device='cuda' if torch.cuda.is_available() else 'cpu',rollout=128,minibatch=2048,updates=0,resume=False,auto_calibrate=False,
                auto_tune_workers=False,auto_tune_environments=False,probe_interval=0,curriculum=variant!='no-curriculum',user_pool=str(output/'empty-user-pool'))
            script=root/'training/learner.py'
            if variant=='legacy':
                if not args.baseline_root:raise ValueError('Build an unchanged checkout and pass --baseline-root for the legacy ablation.')
                baseline=Path(args.baseline_root).resolve();script=baseline/'training/learner.py';config['host']=str(baseline/'tools/BatomonHelper.TrainingHost/bin/Release/net10.0/BatomonHelper.TrainingHost.dll')
            path=target/'launch.json';atomic_json(path,config)
            start=time.monotonic();print(f'Training {variant}, seed {seed}, {args.minutes:g} minute budget',flush=True)
            if not (target/'checkpoint.json').exists():
                with (target/'process.log').open('w',encoding='utf-8') as log:
                    subprocess.run([sys.executable,str(script),'--config',str(path)],cwd=root,stdout=log,stderr=subprocess.STDOUT,check=True)
            training_seconds=time.monotonic()-start
            current=dict(config,host=str(host),evaluation_seed=1000000076,evaluation_states=args.scenario_bank)
            _,_,saved,_=checkpoint_data(current)
            snapshot=dict(label=f'{variant} seed {seed}',update=saved['update'],generation=saved['update']//10,encoding=saved['encoding'],model_version=saved['model_version'],weights=saved['policies'])
            print(f'Evaluating {snapshot["label"]}, update {saved["update"]}',flush=True)
            with EvaluationRunner(current,target,suite,rules,args.runs) as runner:
                runner.progress_context=dict(status='Evaluating');evaluation=runner.evaluate(snapshot)
                baseline=runner.evaluate(dict(label='Strongest-shop baseline',update=-1,generation=-1,encoding=5,model_version=5,weights=None,baseline=True))
                archived=runner.evaluate(dict(label='Archived generation 10',update=100,generation=10,encoding=4,model_version=3,
                    weights=torch.load(archive/'generation-10.pt',map_location='cpu',weights_only=True)))
            if not all(r['completed'] for r in (evaluation,baseline,archived)):
                atomic_json(output/'report.json',dict(status='Incomplete',rows=rows,last_evaluation=evaluation));return
            metrics=[json.loads(line) for line in (target/'metrics.jsonl').read_text().splitlines()]
            row=dict(variant=variant,seed=seed,minutes=args.minutes,training_seconds=training_seconds,update=saved['update'],
                rollout_seconds=metrics[-1]['rollout_seconds'],optimization_seconds=metrics[-1]['optimization_seconds'],
                decisions_per_second=metrics[-1]['decisions_per_second'],evaluation=evaluation,
                versus_strongest=paired_interval(evaluation['scenarios'],baseline['scenarios']),versus_generation10=paired_interval(evaluation['scenarios'],archived['scenarios']))
            row['acceptance_passed']=bool(evaluation['completed'] and evaluation['limited_turn_rate']<.05 and row['versus_strongest']['confidence_interval_95'][0]>0 and row['versus_generation10']['confidence_interval_95'][0]>0)
            rows.append(row);atomic_json(output/'report.json',dict(status='Running',rows=rows))
            print(f'{snapshot["label"]}: success {evaluation["success_rate"]:.1%}, forced finish {evaluation["limited_turn_rate"]:.1%}, gate {row["acceptance_passed"]}',flush=True)
    full=[row for row in rows if row['variant']=='full']
    atomic_json(output/'report.json',dict(status='Complete',minutes=args.minutes,seeds=list(map(int,args.seeds.split(','))),runs=args.runs,
        acceptance_passed=len(full)>=3 and all(row['acceptance_passed'] for row in full),rows=rows,
        limitations=['Bootstrap intervals describe paired simulator episodes; short training budgets do not establish convergence.',
            'Curriculum training win counters include suffix starts; only fixed evaluations compare quality.',
            'Use --scenario-bank with disjoint development/final files for strategy and late-rank diagnostics.']))

if __name__=='__main__':main()
