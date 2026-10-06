"""Offline pixel inspection and reproducible glyph-template extraction from annotated captures."""
from PIL import Image
import json, pathlib, sys
root = pathlib.Path(__file__).resolve().parents[1]

def mask(im, mode):
    def ink(p):
        r,g,b=p[:3]
        if mode=='light': return min(r,g,b)>190 and max(r,g,b)-min(r,g,b)<45
        if mode=='level': return (r>130 and g>75 and b<125 and r>g*1.1) or (r>190 and g>180 and b<125) or (r>200 and g<140 and 75<b<180)
        if mode=='silver': return 110<r<220 and g>110 and b>130 and abs(r-g)<30 and abs(r-b)<65
        if mode=='price': return (min(r,g,b)>190 and max(r,g,b)-min(r,g,b)<45) or (r>200 and g<110 and 75<b<180)
        if mode=='gray': return 65<r<155 and abs(r-g)<14 and abs(g-b)<25
        if mode=='green': return g>r*1.3 and g>b*1.3 and g>100
        if mode=='blue': return b>r*1.4 and g>r*1.3 and b>100
        if mode=='purple': return r>g*1.3 and b>g*1.3 and r>100
        if mode=='orange': return r>g*1.25 and g>b*1.3 and r>100
    result=Image.new('1',im.size)
    result.putdata([ink(p) for p in im.convert('RGB').getdata()])
    return result

def glyphs(im):
    bounds=im.getbbox()
    if not bounds: return []
    im=im.crop(bounds)
    bands=[]; start=None
    for x in range(im.width+1):
        used=x<im.width and any(im.getpixel((x,y)) for y in range(im.height))
        if used and start is None: start=x
        if not used and start is not None:
            glyph=im.crop((start,0,x,im.height)); bands.append(glyph.crop(glyph.getbbox())); start=None
    return bands

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='build':
        import runpy
        runpy.run_path(str(root / 'tools/build-recognition.py'), run_name='__main__')
    elif len(sys.argv)>1 and sys.argv[1]=='read':
        templates=json.loads((root/'assets/recognition/digits.json').read_text())
        regions=[('day',(1092,18,1158,94),'light'),('lives',(746,32,816,92),'light'),('wins',(1300,31,1440,90),'light'),('gold',(1004,872,1100,916),'light'),('rank',(470,868,493,903),'gray'),('odds',(520,869,566,902),'gray'),('stat',(715,1053,740,1081),'light')]
        for day in range(1,4):
            im=Image.open(root/f'tests/BatomonHelper.Tests/Fixtures/board-day{day}.png').convert('RGB')
            for name,rect,mode in regions:
                crop=im.crop(tuple(round(v*(im.width/2048 if i%2==0 else im.height/1152)) for i,v in enumerate(rect)))
                print(day,name)
                for g in glyphs(mask(crop,mode)):
                    n=''.join('1' if p else '0' for p in g.resize((16,24),Image.Resampling.NEAREST).getdata())
                    scores={}
                    for t in templates: scores[t['Character']]=max(scores.get(t['Character'],0),sum(a==b for a,b in zip(n,t['Mask']))/384)
                    print((g.width,g.height),sorted(scores.items(),key=lambda x:-x[1])[:3])
    elif len(sys.argv)>1:
        im=Image.open(root/'tests/BatomonHelper.Tests/Fixtures'/sys.argv[1]).convert('RGB')
        rect=tuple(map(float,sys.argv[2].split(',')))
        crop=im.crop(tuple(round(v*(im.width if i%2==0 else im.height)) for i,v in enumerate(rect)))
        m=mask(crop,sys.argv[3]); gs=glyphs(m)
        print('glyphs',[(g.width,g.height) for g in gs])
        for g in gs:
            g.thumbnail((32,24)); print('\n'.join(''.join('#' if g.getpixel((x,y)) else '.' for x in range(g.width)) for y in range(g.height))); print()
