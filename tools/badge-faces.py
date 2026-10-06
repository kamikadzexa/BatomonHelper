"""Same face ownership and border flood removal used by BadgeDetector."""
from PIL import Image
from collections import deque

def kind(c):
    r,g,b=c[:3]
    distance=lambda a,d,e:abs(r-a)+abs(g-d)+abs(b-e)
    if distance(239,66,107)<45 or (r>220 and 45<g<135 and 80<b<180): return 'Damage'
    if distance(248,144,40)<45: return 'Burn'
    if r>220 and g>190 and b<110: return 'Shock'
    if distance(33,140,165)<35 or distance(63,172,191)<35: return 'Multicast'
    if b>130 and g>105 and r<115: return 'Heal'
    if distance(152,160,216)<55: return 'Shield'
    if r>115 and b>135 and g<130: return 'Poison'

KINDS=['Damage','Heal','Shield','Burn','Poison','Shock','CooldownSpeed','Multicast']
def colored_faces(image):
    w,h=image.size;colors=[kind(c) for c in image.getdata()];seen=set();result=[]
    for seed,k in enumerate(colors):
        if not k or seed in seen:continue
        queue=deque([seed]);seen.add(seed);left,right,top,bottom=w,0,h,0
        while queue:
            p=queue.popleft();x,y=p%w,p//w
            left=min(left,x);right=max(right,x);top=min(top,y);bottom=max(bottom,y)
            for dy in (-1,0,1):
                for dx in (-1,0,1):
                    nx,ny=x+dx,y+dy;n=ny*w+nx
                    if 0<=nx<w and 0<=ny<h and n not in seen and colors[n]==k:seen.add(n);queue.append(n)
        result.append((k,(left,top,right+1,bottom+1)))
    return result

def faces(image,scale):
    import math,runpy
    helpers=runpy.run_path(str(__import__('pathlib').Path(__file__).with_name('inspect-board.py')))
    image=image.convert('RGB');w,h=image.size
    colored=colored_faces(image)
    ink=isolate(helpers['mask'](image,'light'));runs=[];begin=None
    for x in range(w+1):
        used=x<w and any(ink.getpixel((x,y)) for y in range(h))
        if used and begin is None:begin=x
        if used or begin is None:continue
        if runs and begin-runs[-1][1]<=math.ceil(scale*2):runs[-1]=(runs[-1][0],x)
        else:runs.append((begin,x))
        begin=None
    result=[]
    for left,right in runs:
        ys=[y for x in range(left,right) for y in range(h) if ink.getpixel((x,y))]
        if not ys or max(ys)-min(ys)<scale*3:continue
        top,bottom=min(ys),max(ys);padding=math.ceil(scale*2);counts=[0]*8
        for y in range(max(0,top-padding),min(h,bottom+padding+1)):
            for x in range(max(0,left-padding),min(w,right+padding)):
                k=kind(image.getpixel((x,y)))
                if k:counts[KINDS.index(k)]+=1
        if max(counts)<scale*scale:continue
        k=KINDS[counts.index(max(counts))];bounds=[left,top,right,bottom+1]
        surround=(max(0,left-padding),max(0,top-padding),min(w,right+padding),min(h,bottom+padding+1))
        for fk,face in colored:
            if fk==k and face[2]-face[0]<=right-left+scale*10 and face[0]<surround[2] and face[2]>surround[0] and face[1]<surround[3] and face[3]>surround[1]:
                bounds=[min(bounds[0],face[0]),min(bounds[1],face[1]),max(bounds[2],face[2]),max(bounds[3],face[3])]
        border=math.ceil(scale*.7)
        result.append((k,(max(0,bounds[0]-border),max(0,bounds[1]-border),min(w,bounds[2]+border),min(h,bounds[3]+border))))
    return result

def isolate(mask):
    mask=mask.copy();w,h=mask.size;queue=deque()
    def add(x,y):
        if mask.getpixel((x,y)):mask.putpixel((x,y),0);queue.append((x,y))
    for x in range(w):add(x,0);add(x,h-1)
    for y in range(h):add(0,y);add(w-1,y)
    while queue:
        x,y=queue.popleft()
        for nx,ny in ((x-1,y),(x+1,y),(x,y-1),(x,y+1)):
            if 0<=nx<w and 0<=ny<h:add(nx,ny)
    return mask
