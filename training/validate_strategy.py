"""Bounded smoke checks or staged, equal-budget strategic learning experiments.

No candidate is promoted by training counters. Final evaluation is used only after
the development gate passes; failed gates are recorded explicitly.
"""
from pathlib import Path
import argparse
import json
import subprocess
import sys
import time
import numpy as np
from learner import atomic_json
from strategy import default_config,StrategyHost

def paired(candidate,baseline,seed=69):
    for key in ('Rules','Suite','Seed','Code','Opponents'):
        if candidate['identity'][key]!=baseline['identity'][key]:raise ValueError('Unpaired suite: '+key)
    left={r['scenario']:r for r in candidate['scenarios']};right={r['scenario']:r for r in baseline['scenarios']}
    if set(left)!=set(right) or not candidate['completed'] or not baseline['completed']:raise ValueError('Incomplete paired evaluation.')
    delta=np.array([float(left[i]['success'])-float(right[i]['success']) for i in sorted(left)])
    if not len(delta):raise ValueError('No scenarios.')
    rng=np.random.default_rng(seed);means=delta[rng.integers(0,len(delta),(4000,len(delta)))].mean(1)
    return dict(runs=len(delta),improvement=float(delta.mean()),confidence_interval_95=np.quantile(means,[.025,.975]).tolist())

def run(config,*arguments):
    output=Path(config['output']);output.mkdir(parents=True,exist_ok=True);path=output/'experiment-launch.json'
    atomic_json(path,config);atomic_json(output/'control.json',dict(command='run'))
    subprocess.run([sys.executable,str(Path(__file__).with_name('strategy.py')),'--config',str(path),*arguments],check=True)

def evaluate(config,runs,variant,suite='development'):
    run(config,'--evaluate','--runs',str(runs),'--variant',variant,'--suite',suite)
    report=json.loads((Path(config['output'])/f'evaluation-{suite}-{variant}.json').read_text())
    if report['identity']['Suite']!=suite:raise RuntimeError('Evaluation ran against the wrong suite.')
    return report

def smoke(config,root):
    output=root/'smoke';config=dict(config,output=str(output),minutes=.75,warmup_runs=1,analysis_samples=1,optimization_steps=1,checkpoint_runs=1,
        search_seconds=.06,search_simulations=8,evaluation_search_seconds=.06,evaluation_search_simulations=8)
    run(config);before=json.loads((output/'checkpoint.json').read_text())
    run(dict(config,resume=True,minutes=.12));after=json.loads((output/'checkpoint.json').read_text())
    if after['update']<before['update']:raise RuntimeError('Resume regressed updates.')
    run(config,'--export')
    if not (output/'export/manifest.json').exists():raise RuntimeError('Export missing.')
    reports={variant:evaluate(config,2,variant) for variant in ('baseline','policy','search')}
    if config.get('legacy_checkpoint') and Path(config['legacy_checkpoint']).exists():
        for index in range(4):reports[f'legacy-{index}']=evaluate(config,2,f'legacy-{index}')
        legacy_config=dict(json.loads((Path(config['legacy_checkpoint'])/'configuration.json').read_text()),export_output=str(output/'legacy-export'))
        legacy_path=output/'legacy-export-launch.json';atomic_json(legacy_path,legacy_config)
        subprocess.run([sys.executable,str(Path(__file__).with_name('learner.py')),'--config',str(legacy_path),'--export'],check=True)
        if not list((output/'legacy-export').glob('export-archived-*/metadata.json')):raise RuntimeError('Legacy export missing.')
    with StrategyHost(config,output/'advisor-position','development') as host:
        position=host.request('visible');actions=host.request('observe')['Actions']
    position_path=output/'advisor-position.json';advice_path=output/'advice.json';atomic_json(position_path,position)
    run(config,'--advise',str(position_path),'--result',str(advice_path),'--thinking-seconds','0.1')
    if json.loads(advice_path.read_text())['Action'] not in actions:raise RuntimeError('Advisor recommended an illegal action.')
    final=evaluate(config,2,'search','final')
    if reports['search']['identity']['Opponents']==final['identity']['Opponents']:raise RuntimeError('Final opponent archive is not independent.')
    report=dict(smoke_passed=True,learning_acceptance_passed=False,reason='Two-run smoke checks do not establish learning improvement.',
        before=before,after=after,comparisons=paired(reports['search'],reports['baseline']),reports=reports,final=final)
    atomic_json(root/'validation.json',report)

def experiments(config,root,minutes,main_minutes,runs,seeds):
    report=dict(learning_acceptance_passed=False,stage='development',trials=[],final=[])
    for seed in seeds:
        for variant in ('full','no-wiki','no-analysis'):
            trial=dict(config,seed=seed,minutes=minutes,output=str(root/f'{variant}-{seed}'),disable_wiki=variant=='no-wiki',disable_analysis=variant=='no-analysis')
            run(trial)
            measured={name:evaluate(trial,runs,name) for name in ('baseline','scripted-search','policy','search')}
            if config.get('legacy_checkpoint'):
                measured.update({f'legacy-{i}':evaluate(trial,runs,f'legacy-{i}') for i in range(4)})
            comparisons={name:paired(measured['search'],baseline) for name,baseline in measured.items() if name not in ('search','policy')}
            passed=runs>=512 and measured['search']['limited_turn_rate']<.05 and all(c['improvement']>=.05 and c['confidence_interval_95'][0]>0 for c in comparisons.values())
            report['trials'].append(dict(seed=seed,variant=variant,passed=passed,measurements=measured,comparisons=comparisons));atomic_json(root/'validation.json',report)
    if not all(t['passed'] for t in report['trials'] if t['variant']=='full'):
        report['reason']='Development strength/termination gate failed; longer main runs were not started.';atomic_json(root/'validation.json',report);return
    report['stage']='main'
    for seed in seeds:
        trial=dict(config,seed=seed,minutes=main_minutes,output=str(root/f'main-{seed}'))
        run(trial)
        measured={name:evaluate(trial,runs,name,'final') for name in ('baseline','scripted-search','search')}
        if config.get('legacy_checkpoint'):measured.update({f'legacy-{i}':evaluate(trial,runs,f'legacy-{i}','final') for i in range(4)})
        comparisons={name:paired(measured['search'],baseline) for name,baseline in measured.items() if name!='search'}
        passed=measured['search']['limited_turn_rate']<.05 and all(c['improvement']>=.05 and c['confidence_interval_95'][0]>0 for c in comparisons.values())
        report['final'].append(dict(seed=seed,passed=passed,comparisons=comparisons,measurements=measured));atomic_json(root/'validation.json',report)
    report['learning_acceptance_passed']=runs>=512 and bool(config.get('legacy_checkpoint')) and len(seeds)>=3 and all(t['passed'] for t in report['final'])
    report['stage']='completed';atomic_json(root/'validation.json',report)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config');parser.add_argument('--output',required=True);parser.add_argument('--smoke',action='store_true')
    parser.add_argument('--minutes',type=float,default=120);parser.add_argument('--main-minutes',type=float,default=720);parser.add_argument('--runs',type=int,default=512);parser.add_argument('--seeds',default='69,70,71')
    args=parser.parse_args();config=default_config(json.loads(Path(args.config).read_text(encoding='utf-8-sig')) if args.config else {})
    root=Path(args.output).resolve();root.mkdir(parents=True,exist_ok=True)
    if (root/'validation.json').exists():raise RuntimeError('Choose a new experiment directory; existing results are preserved.')
    if args.smoke:smoke(config,root)
    else:experiments(config,root,args.minutes,args.main_minutes,args.runs,[int(s) for s in args.seeds.split(',')])

if __name__=='__main__':main()
