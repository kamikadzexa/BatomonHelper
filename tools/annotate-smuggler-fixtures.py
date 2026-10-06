"""Visually checked annotations. Saved capture proposals are retained only as evidence."""
import json
from pathlib import Path

fixtures = Path(__file__).resolve().parents[1] / 'tests/BatomonHelper.Tests/Fixtures'

def slot(id, stats=None, level=None, variant='Normal', price=None):
    return dict(Id=id, Occupancy='Creature' if id else 'Empty', Variant=variant if id else 'Unknown',
                Level=level, Stats=stats or {}, Price=price)

for day in (9, 10):
    data = dict(TrainerId='smuggler', Day=day, Lives=1, Wins=4 if day == 9 else 5, WinTarget=10,
                TeamHp=11600 if day == 9 else 17300, Gold=147 if day == 9 else 31, ShopRank=day+1,
                Odds=[15,20,40,20,5] if day == 9 else [10,20,35,25,10], ShopLocked=day == 10,
                RerollCost=3 if day == 9 else None, FreeRerolls=None if day == 9 else 7,
                ItemUsesRemaining=None, ItemUsesMaximum=None)
    if day == 9:
        data['Team'] = [slot('ironcore',{'Damage':30},1), slot('humbolt',{'Damage':63,'Shock':5,'Multicast':3},2),
                        slot('pylong',{'Damage':1,'Shock':15},1), slot('plunderbird',{'Damage':8,'Heal':55,'Multicast':5},3,'Shiny'),
                        slot('ironcore',{'Damage':30},1), slot('humbolt',{'Damage':38,'Shock':1,'Multicast':2},1)]
        data['Bench'] = [slot('saberhorn',{'Damage':120},1,'Shiny')] + [slot(None) for _ in range(3)]
        data['Shop'] = [slot('humbolt',{'Damage':38,'Shock':1,'Multicast':2},price=25), slot('bumblebolt',{'Damage':3,'Shock':1},price=10),
                        slot('sarudo',{'Damage':120},price=18), slot('galvanade',{'Shock':25},price=25), slot('infernade',{'Burn':20},price=25)]
    else:
        data['Team'] = [slot('galvanine',{'Damage':12,'Shock':6},1), slot('humbolt',{'Damage':69,'Shock':7,'Multicast':4},3),
                        slot('pylong',{'Damage':1,'Shock':15},1), slot('plunderbird',{'Damage':8,'Heal':68,'Multicast':5},3,'Shiny'),
                        slot('ironcore',{'Damage':30},2), slot('clawnetic',{'Damage':60},1)]
        data['Bench'] = [slot(None), slot('pylong',{'Damage':1,'Shock':16},1), slot(None), slot(None)]
        data['Shop'] = [slot('zephyrex',{'Damage':145},variant='Unknown',price=60), slot('prismagon',{'Damage':100},variant='Unknown',price=45),
                        slot('brawlmantis',{'Damage':60},price=30), slot('ninflora',{'Damage':50},variant='Unknown',price=37),
                        slot('pylong',{'Damage':1,'Shock':15},level=2,price=40)]
    (fixtures / f'smuggler-day{day}.expected.json').write_text(json.dumps(data,indent=2)+'\n')
