"""Build font templates from explicit, visually checked labels, never capture proposals."""
from PIL import Image
from pathlib import Path
from collections import deque
import json
import runpy
import math
from functools import lru_cache

root = Path(__file__).resolve().parents[1]
helpers = runpy.run_path(str(root / 'tools/inspect-board.py'))
mask, glyphs = helpers['mask'], helpers['glyphs']
badges = runpy.run_path(str(root / 'tools/badge-faces.py'))
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

def segment_sequence(binary,label,references):
    """Supervised alignment with the runtime width, coverage and seam rules."""
    bounds=binary.getbbox()
    if not bounds:return []
    binary=binary.crop(bounds);w,h=binary.size
    @lru_cache(None)
    def solve(position,index):
        if index==len(label):return (0,[]) if position==w else None
        best=None
        for end in range(position+max(1,int(h*.20)),min(w,position+math.ceil(h*.9))+1):
            remaining=len(label)-index-1
            if w-end<remaining*max(1,int(h*.20)) or w-end>remaining*math.ceil(h*.9):continue
            part=binary.crop((position,0,end,h));box=part.getbbox()
            if not box:continue
            part=part.crop(box);aspect=part.width/part.height;scores=[]
            for ref in references.get(label[index],[]):
                delta=abs(math.log(aspect/(ref.width/ref.height)))
                if delta>=.45:continue
                actual=[bool(p) for p in part.getdata()]
                template=[bool(p) for p in ref.resize(part.size,Image.Resampling.NEAREST).getdata()]
                same=sum(a==t for a,t in zip(actual,template));intersection=sum(a and t for a,t in zip(actual,template));union=sum(a or t for a,t in zip(actual,template))
                scores.append(.5*same/len(actual)+.5*intersection/max(1,union)-min(.20,delta*.25)-min(.25,abs(math.log(part.height/ref.height))*.15))
            # An unobserved isolated character can be aligned from its annotated
            # sequence, but never assigned a different character by OCR.
            score=max(scores,default=.95-min(.25,abs(math.log(aspect/(.4 if label[index]=='1' else .6)))*.25))
            if score<.78:continue
            tail=solve(end,index+1)
            if tail is None:continue
            crossing=0 if end==w else sum(bool(binary.getpixel((end-1,y))) and bool(binary.getpixel((end,y))) for y in range(h))
            total=(score-.15*crossing/h)*(end-position)+tail[0]-.035*h
            if best is None or total>best[0]:best=(total,[part]+tail[1])
        return best
    result=solve(0,0)
    return result[1] if result else []

def build():
    samples=[]
    def add(name,palette,rect,label,family): samples.append((name,palette,rect,label,family))
    runs=[('board-day1',1,10,0,300,30,1),('board-day2',2,9,0,500,0,2),('board-day3',3,9,1,800,50,3),
          ('treasure-day3',3,10,2,800,42,3),('treasure-day9',9,7,7,11600,52,10),('treasure-day10',10,7,8,17300,136,11),
          ('smuggler-day9',9,1,4,11600,147,10),('smuggler-day10',10,1,5,17300,31,11)]
    for stem,day,lives,wins,hp,gold,rank in runs:
        name=stem+'.png'
        for rect,label in [((860,18,1199,94),'DAY'+str(day)),((746,32,816,92),str(lives)),((1300,31,1510,90),str(wins)+'/10'),((954,872,1092,916),'$'+str(gold))]: add(name,'light',rect,label,'hud')
        add(name,'light',(110,789,419,824),str(hp),'hp')
        add(name,'gray',(411,870,493,901),'Rank'+str(rank),'rank')
        if (stem.startswith('treasure') and day!=9) or stem=='smuggler-day9': add(name,'light',(90,1055,235,1110),'$3','hud')
        else: add(name,'light',(210,1055,284,1104),'('+str(7 if stem=='smuggler-day10' else {1:3,2:5,3:8,9:1}[day])+')','hud')
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
        # Detect and isolate badges with the runtime rules, never saved crop proposals.
        image=Image.open(fixtures/name).convert('RGB')
        positions={'Team':[(649,475),(878,475),(1107,475),(649,661),(878,661),(1107,661)],
                   'Bench':[(524,212),(754,212),(984,212),(1214,212)],
                   'Shop':[(411,933),(661,933),(910,933),(1160,933),(1409,933)]}
        for area,rows in positions.items():
            for i,(x,y) in enumerate(rows):
                slot=annotation[area][i]
                width,height=(223,145) if area=='Shop' else (199,156)
                rect=(x,y+height-.023*1152,x+width,y+height+.008*1152)
                native=tuple(round(v*(image.width/2048 if j%2==0 else image.height/1152)) for j,v in enumerate(rect))
                crop=image.crop(native)
                for kind,face in badges['faces'](crop,image.width/640):
                    if kind not in slot['Stats']:continue
                    ink=badges['isolate'](mask(crop.crop(face),'light'))
                    bounds=ink.getbbox()
                    if not bounds or bounds[3]-bounds[1]<image.width/640*3 or sum(bool(p) for p in ink.getdata())<2*(image.width/640)**2:continue
                    absolute=tuple((face[j]+native[j%2])/(image.width/2048 if j%2==0 else image.height/1152) for j in range(4))
                    add(name,'light',absolute,('X' if kind=='Multicast' else '')+str(slot['Stats'][kind]),'badge')
        if stem=='smuggler-day10':add(name,'silver',(1471,941,1495,973),'2','level')
    add('board-day1.png','light',(1414,962,1462,994),'1/1','hud')
    add('treasure-day10.png','light',(1414,962,1462,994),'2/2','hud')
    add('treasure-hunter-rank9.png','light',(1414,962,1462,994),'2/3','hud')
    for stem,rank,odds in [('rank11-shop',11,[10,20,35,25,10]),('treasure-hunter-rank9',9,[20,25,38,15,2])]:
        add(stem+'.png','gray',(411,870,493,901),'Rank'+str(rank),'rank')
        for palette,x,w,value in zip(['gray','green','blue','purple','orange'],[521,597,672,750,827],[54,47,47,47,47],odds): add(stem+'.png',palette,(x,870,x+w,901),str(value)+'%','ribbon')
    templates=[]
    # Bootstrap only isolated, explicitly annotated glyphs at native scale.
    # Joined labels then use supervised sequence alignment with these references.
    references={}
    for name,palette,rect,label,family in samples:
        if family!='badge':continue
        im=Image.open(fixtures/name).convert('RGB')
        crop=im.crop(tuple(round(v*(im.width/2048 if i%2==0 else im.height/1152)) for i,v in enumerate(rect)))
        gs=glyphs(badges['isolate'](mask(crop,palette)))
        if len(gs)==len(label):
            for ch,g in zip(label,gs):references.setdefault(ch,[]).append(g)
    references={ch:list({(g.size,g.tobytes()):g for g in gs}.values()) for ch,gs in references.items()}
    variants=[]
    for sample in samples:
        variants.append((*sample,None,0,0))
        variants.extend((*sample,width,0,0) for width in (640,1280,1600,1920,2434))
        # Resample from the source at different origins, rather than translating
        # an already rounded glyph. Fractional scales change stroke widths with phase.
        variants.extend((*sample,width,px,py) for width in (640,1600,2434)
                        for px,py in ((.5,0),(0,.5),(.5,.5)))
    frames={}
    for name,palette,rect,label,family,width,px,py in variants:
        key=(name,width,px,py)
        if key not in frames:
            source=Image.open(fixtures/name).convert('RGB')
            if width and (px or py):
                height=round(width*9/16); sx,sy=source.width/width,source.height/height
                source=source.transform((width,height),Image.Transform.AFFINE,(sx,0,px*sx,0,sy,py*sy),Image.Resampling.NEAREST)
            elif width: source=source.resize((width,round(width*9/16)),Image.Resampling.NEAREST)
            frames[key]=source
        im=frames[key]
        crop=im.crop(tuple(round(v*(im.width/2048 if i%2==0 else im.height/1152)) for i,v in enumerate(rect)))
        binary=mask(crop,palette)
        if family=='badge':binary=badges['isolate'](binary)
        gs=glyphs(binary); gs=[g for g in gs if g.height>=max(8,max((v.height for v in gs),default=0)*(.75 if family=='level' else .4))]
        if family=='badge' and len(gs)!=len(label):
            gs=segment_sequence(binary,label,references)
        if len(gs)!=len(label):
            if width: continue
            raise ValueError(f'{name} {palette} {rect} {label}: glyphs {[(g.width,g.height) for g in gs]}')
        for ch,g in zip(label,gs):
            aspect=g.width/g.height; native=''.join('1' if p else '0' for p in g.getdata()); w,h=g.size; g=g.resize((16,24),Image.Resampling.NEAREST)
            templates.append({'Character':ch,'Mask':''.join('1' if p else '0' for p in g.getdata()),'Family':family,'Aspect':round(aspect,4),'Holes':holes(g),'Scaled':bool(width),'Width':w,'Height':h,'NativeMask':native})
    labels={}
    for t in templates:
        key=(t['Family'],t['Mask'])
        if not t['Scaled']:
            if key in labels and labels[key]!=t['Character']: raise ValueError(f'Conflicting glyph labels: {t["Family"]} {labels[key]} / {t["Character"]}')
            labels[key]=t['Character']
    for t in templates: t.pop('Scaled')
    templates=list({(t['Character'],t['NativeMask'],t['Width'],t['Height'],t['Family']):t for t in templates}.values())
    (root/'assets/recognition/digits.json').write_text(json.dumps(templates,separators=(',',':')))
    print('Templates:',len(templates))
    for family in ('hud','hp','badge','level','ribbon','rank','price'): print(family,sorted({t['Character'] for t in templates if t['Family']==family}))

if __name__=='__main__': build()
