"""Explicit screenshot annotations. Saved review records are evidence, not the oracle."""
from pathlib import Path
import json
root=Path(__file__).resolve().parents[1]/'tests/BatomonHelper.Tests/Fixtures'
def empty(): return dict(Id=None,Occupancy='Empty',Variant='Unknown',Level=None,Stats={},Price=None)
def creature(id,level=None,price=None,variant='Normal',**stats): return dict(Id=id,Occupancy='Creature',Variant=variant,Level=level,Stats=stats,Price=price)
def item(id,price): return dict(Id=id,Occupancy='Item',Variant='Unknown',Level=None,Stats={},Price=price)
for day in (3,9,10):
    annotation=dict(TrainerId='treasure-hunter',Day=day,Lives=10 if day==3 else 7,Wins={3:2,9:7,10:8}[day],WinTarget=10,
        TeamHp={3:800,9:11600,10:17300}[day],Gold={3:42,9:52,10:136}[day],ShopRank={3:3,9:10,10:11}[day],
        Odds={3:[75,25,0,0,0],9:[15,20,40,20,5],10:[10,20,35,25,10]}[day],ShopLocked=day==9,
        RerollCost=None if day==9 else 3,FreeRerolls=1 if day==9 else None,ItemUsesRemaining={3:1,9:None,10:2}[day],ItemUsesMaximum={3:1,9:None,10:2}[day])
    if day==3:
        annotation.update(Team=[creature('panbud',2,Damage=67),empty(),empty(),creature('stingarde',2,variant='Shiny',Damage=317),empty(),empty()],Bench=[empty() for _ in range(4)],
            Shop=[creature('panbud',price=10,Damage=25),creature('guardiant',price=12,Damage=15),creature('shogapede',price=22,Poison=6),creature('panbud',price=10,Damage=25),item('basic-bait',0)])
    else:
        annotation.update(Team=[creature('bambudo',4,Damage=1079 if day==9 else 1581),creature('boomagon',3,Damage=50),creature('boomagon',1,Damage=40),
            creature('formiqueen',2,Damage=144 if day==9 else 156),creature('faebloom',1,Damage=777),creature('stingarde',3,variant='Shiny',Damage=520)],
            Bench=[creature('formiqueen',1,Damage=60),creature('formiqueen',1,Damage=60),empty(),empty()])
        annotation['Shop']=[creature('ironcore',price=40,Damage=30),creature('gaiadrasil',price=60),creature('stalagrove',price=35,Shield=60),creature('thorntail',price=35,Damage=50,Poison=1),creature('humbolt',price=25,Damage=30,Shock=1,Multicast=2)] if day==9 else [
            creature('frizzly',price=25,Damage=1,Shock=8),creature('noxnimbus',price=25,Poison=1),creature('cicadence',price=22,Damage=100),creature('gildshell',price=12,Damage=1),item('purple-gift',40)]
    (root/f'treasure-day{day}.expected.json').write_text(json.dumps(annotation,indent=2)+'\n')
