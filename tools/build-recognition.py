"""Build font templates from explicit, visually checked labels, never capture proposals."""
from PIL import Image
from pathlib import Path
from collections import deque
import json
import runpy

root = Path(__file__).resolve().parents[1]
helpers = runpy.run_path(str(root / 'tools/inspect-board.py'))
mask, glyphs = helpers['mask'], helpers['glyphs']
fixtures = root / 'tests/BatomonHelper.Tests/Fixtures'

def holes(bits):
    pixels = list(bits.getdata()); w,h = bits.size; seen=set(); count=0
    for start in range(len(pixels)):
        if pixels[start] or start in seen: continue
        queue=deque([start]); seen.add(start); area=0; edge=False
        while queue:
            p=queue.popleft(); x,y=p%w,p//w; area+=1; edge |= x==0 or y==0 or x==w-1 or y==h-1
            for nx,ny in ((x-1,y),(x+1,y),(x,y-1),(x,y+1)):
                n=ny*w+nx
                if 0<=nx<w and 0<=ny<h and not pixels[n] and n not in seen: seen.add(n); queue.append(n)
        if not edge and area>=3: count+=1
    return count

def build():
    samples=[]
    def add(name,palette,rect,label,family): samples.append((name,palette,rect,label,family))
    runs=[('board-day1',1,10,0,300,30,1),('board-day2',2,9,0,500,0,2),('board-day3',3,9,1,800,50,3),
          ('treasure-day3',3,10,2,800,42,3),('treasure-day9',9,7,7,11600,52,10),('treasure-day10',10,7,8,17300,136,11)]
    for stem,day,lives,wins,hp,gold,rank in runs:
        name=stem+'.png'
        for rect,label in [((860,18,1199,94),'DAY'+str(day)),((746,32,816,92),str(lives)),((1300,31,1510,90),str(wins)+'/10'),((954,872,1092,916),'$'+str(gold))]: add(name,'light',rect,label,'hud')
        add(name,'light',(110,789,419,824),str(hp),'hp')
        add(name,'gray',(411,870,493,901),'Rank'+str(rank),'rank')
        if stem.startswith('treasure') and day!=9: add(name,'light',(90,1055,235,1110),'$3','hud')
        else: add(name,'light',(210,1055,284,1104),'('+str({1:3,2:5,3:8,9:1}[day])+')','hud')
        annotation=json.loads((fixtures/(stem+'.expected.json')).read_text())
        odds=annotation.get('Odds',{1:[100,0,0,0,0],2:[90,10,0,0,0],3:[75,25,0,0,0]}.get(day))
        for palette,x,w,value in zip(['gray','green','blue','purple','orange'],[521,597,672,750,827],[54,47,47,47,47],odds): add(name,palette,(x,870,x+w,901),str(value)+'%','ribbon')
        for i,slot in enumerate(annotation['Shop']):
            if slot['Price'] is not None:
                x=[411,661,910,1160,1409][i]; add(name,'price',(x+223-51.2,1082.88,x+223,1115.136),'$'+str(slot['Price']),'price')
        for area in ('Team','Bench'):
            for i,slot in enumerate(annotation[area]):
                if slot['Level'] is None: continue
                x,y=([(649,475),(878,475),(1107,475),(649,661),(878,661),(1107,661)] if area=='Team' else [(524,212),(754,212),(984,212),(1214,212)])[i]
                add(name,'level' if slot['Level']!=2 else 'silver',(x+60,y,x+78,y+25),str(slot['Level']),'level')
        if stem.startswith('treasure'):
            evidence=json.loads((fixtures/(stem+'.v2.json')).read_text())
            for field in evidence['Result']['Fields']:
                import re
                match=re.fullmatch(r'(Team|Bench|Shop)\[(\d+)\]\.Stats\[(\d+)\]\.Value',field['Name'])
                if not match: continue
                area,index,stat=match[1],int(match[2]),int(match[3]); stats=list(annotation[area][index]['Stats'].items())
                if stat>=len(stats): continue
                kind,value=stats[stat]; r=field['Region']; rect=(r['X']*2048,r['Y']*1152,(r['X']+r['Width'])*2048,(r['Y']+r['Height'])*1152)
                add(name,'light',rect,('X' if kind=='Multicast' else '')+str(value),'badge')
    add('board-day1.png','light',(1414,962,1462,994),'1/1','hud')
    add('treasure-day10.png','light',(1414,962,1462,994),'2/2','hud')
    add('treasure-hunter-rank9.png','light',(1414,962,1462,994),'2/3','hud')
    for rect,label in [((506,1057,537,1080),'15'),((736,1060,762,1080),'5'),((783,1060,809,1080),'5'),((980,1060,1008,1080),'40'),((1031,1060,1055,1080),'4'),((1250,1060,1280,1080),'20')]: add('board-day1.png','light',rect,label,'badge')
    add('board-day2.png','light',(1038,1059,1050,1079),'8','badge')
    add('board-day3.png','light',(758,1059,782,1079),'30','badge')
    for stem,rank,odds in [('rank11-shop',11,[10,20,35,25,10]),('treasure-hunter-rank9',9,[20,25,38,15,2])]:
        add(stem+'.png','gray',(411,870,493,901),'Rank'+str(rank),'rank')
        for palette,x,w,value in zip(['gray','green','blue','purple','orange'],[521,597,672,750,827],[54,47,47,47,47],odds): add(stem+'.png',palette,(x,870,x+w,901),str(value)+'%','ribbon')
    templates=[]
    variants=[]
    for sample in samples:
        variants.append((*sample,None))
        if sample[4] in ('hud','hp','rank','ribbon','level','price'): variants.extend((*sample,width) for width in (640,1280,1920))
    for name,palette,rect,label,family,width in variants:
        im=Image.open(fixtures/name).convert('RGB')
        if width: im=im.resize((width,round(width*9/16)),Image.Resampling.NEAREST)
        crop=im.crop(tuple(round(v*(im.width/2048 if i%2==0 else im.height/1152)) for i,v in enumerate(rect)))
        gs=glyphs(mask(crop,palette)); gs=[g for g in gs if g.height>=max(8,max((v.height for v in gs),default=0)*.4)]
        if family=='badge':
            split=[]
            for g in gs:
                count=round(g.width/(g.height*.6)) if g.width>g.height*.9 else 1
                for i in range(count):
                    part=g.crop((round(i*g.width/count),0,round((i+1)*g.width/count),g.height)); split.append(part.crop(part.getbbox()))
            gs=split
        if len(gs)!=len(label):
            if width: continue
            raise ValueError(f'{name} {palette} {rect} {label}: glyphs {[(g.width,g.height) for g in gs]}')
        for ch,g in zip(label,gs):
            aspect=g.width/g.height; g=g.resize((16,24),Image.Resampling.NEAREST)
            templates.append({'Character':ch,'Mask':''.join('1' if p else '0' for p in g.getdata()),'Family':family,'Aspect':round(aspect,4),'Holes':holes(g),'Scaled':bool(width)})
    labels={}
    for t in templates:
        key=(t['Family'],t['Mask'])
        if not t['Scaled']:
            if key in labels and labels[key]!=t['Character']: raise ValueError(f'Conflicting glyph labels: {t["Family"]} {labels[key]} / {t["Character"]}')
            labels[key]=t['Character']
    for t in templates: t.pop('Scaled')
    templates=list({(t['Character'],t['Mask'],t['Family'],t['Aspect']):t for t in templates}.values())
    (root/'assets/recognition/digits.json').write_text(json.dumps(templates,separators=(',',':')))
    print('Templates:',len(templates))
    for family in ('hud','hp','badge','level','ribbon','rank','price'): print(family,sorted({t['Character'] for t in templates if t['Family']==family}))

if __name__=='__main__': build()
