"""Build disjoint diagnostic suites from exact, legally reached archived states."""
import hashlib
import json
from pathlib import Path

def build_scenario_banks(checkpoint,rules,output):
    source=json.loads(Path(checkpoint).read_text(encoding='utf-8-sig'))
    manifest=json.loads(Path(rules).read_text(encoding='utf-8-sig'))
    if source['RulesHash']!=manifest['Hash']:raise ValueError('Scenario source rules differ.')
    definitions={x['Id']:x for x in manifest['Species']}
    selected={};banks={'development':[],'final':[]}
    for state in source['States']:
        if state['Phase']!=0:continue
        band='early' if state['Rank']<=4 else 'middle' if state['Rank']<=8 else 'late'
        digest=hashlib.sha256(json.dumps(state,sort_keys=True).encode()).hexdigest()
        if digest in selected:continue
        selected[digest]=True;split='development' if int(digest[:8],16)%2==0 else 'final'
        if sum(x['band']==band for x in banks[split])>=32:continue
        labels=[];effects=[]
        for unit in state['Units']:
            if unit is None:continue
            species=definitions[unit['SpeciesId']];levels=species['Shiny'] if unit['Shiny'] and species['Shiny'] else species['Normal']
            level=min(levels,key=lambda x:abs(x['Level']-unit['Level']));effects+=level['Effects']
        # Label effects by their explicit target and trigger fields; these labels are diagnostics, not rewards.
        if any(x['Target'] in (2,3,4,5,6,7,8,13,18,19,20) for x in effects):labels.append('position')
        if any(x['Trigger']>=10 for x in effects):labels.append('trigger-chain')
        if len({(u['SpeciesId'],u['Level']) for u in state['Units'] if u})<sum(u is not None for u in state['Units']):labels.append('merge')
        if all(state['Units'][:6]) and any(o and o['Creature'] for o in state['Shop']):labels.append('replacement')
        if state['Gold']>=50:labels.append('economy')
        banks[split].append(dict(id=digest,band=band,labels=labels,state=state))
    target=Path(output);target.mkdir(parents=True,exist_ok=True)
    for name,rows in banks.items():
        (target/(name+'.json')).write_text(json.dumps(dict(version=1,rules_hash=manifest['Hash'],source_sha256=hashlib.sha256(Path(checkpoint).read_bytes()).hexdigest(),scenarios=rows)),encoding='utf-8')
    return {name:len(rows) for name,rows in banks.items()}
