using System.Globalization;
using System.Text.RegularExpressions;

namespace BatomonHelper.Simulation;

/// <summary>Anchored ability grammar; a partial match never counts as coverage.</summary>
public static class AbilityRules
{
    private const string Types="Fire|Water|Grass|Rock|Bug|Flying|Electric|Toxic|Dragon|Fighting";
    private const string Stats="Damage|Heal|Shield|Burn|Poison|Shock|Cooldown Speed|Multicast|Sell Value";
    private const string Number="[+-]?[0-9]+(?:\\.[0-9]+)?";
    private static double N(string s) => double.Parse(s,CultureInfo.InvariantCulture);
    private static Stat S(string s) => s switch {"Cooldown Speed"=>Stat.Speed,"Sell Value"=>Stat.SellValue,_=>Enum.Parse<Stat>(s)};
    private static Target T(string s) => s switch
    {
        "this"=>Target.Self,"adjacent allies"=>Target.Adjacent,"ally above"=>Target.Above,"ally behind"=>Target.Left,
        "ally in front"=>Target.Right,"allies"=>Target.All,"random ally"=>Target.RandomAlly,_=>throw new InvalidDataException(s)
    };
    public static bool TryCompile(string text,Trigger trigger,out Effect[] effects)
    {
        effects=[];
        Match M(string pattern) => Regex.Match(text,"^"+pattern+"$",RegexOptions.CultureInvariant);
        var shopBuff=M("Give (?:(?<type>"+Types+") |all )?monsters in the shop(?<owned>, on your team, and on your bench| and on your team)? (?<amount>\\+"+Number+") (?<stat>"+Stats+") for the rest of the run\\.");
        if(shopBuff.Success)
        {
            var owned=shopBuff.Groups["owned"].Value;
            effects=[new(trigger,Operation.Add,owned.Length==0?Target.Shop:owned.StartsWith(",")?Target.ShopAndAll:Target.ShopAndTeam,
                S(shopBuff.Groups["stat"].Value),N(shopBuff.Groups["amount"].Value),true,
                Type:shopBuff.Groups["type"].Success?shopBuff.Groups["type"].Value.ToLowerInvariant():null)];return true;
        }
        var dailyShop=M("At the start of each day, monsters in your shop gain (?<amount>\\+"+Number+") Damage\\.");
        if(dailyShop.Success)
        {effects=[new(Trigger.DayStart,Operation.Add,Target.Shop,Amount:N(dailyShop.Groups["amount"].Value),Permanent:true)];return true;}
        var nextBuy=M("Give the next (?<flying>Flying )?monster you buy (?:(?<type>"+Types+") typing and )?(?<amount>\\+"+Number+") (?<stat>"+Stats+")\\. Lost if sold\\.");
        if(nextBuy.Success)
        {
            effects=[new(trigger,Operation.PendingPurchase,Target.Self,S(nextBuy.Groups["stat"].Value),N(nextBuy.Groups["amount"].Value),true,
                Type:nextBuy.Groups["type"].Success?nextBuy.Groups["type"].Value.ToLowerInvariant():null,
                EventType:nextBuy.Groups["flying"].Success?"flying":null)];return true;
        }
        var scaled=M("(?:Has an additional |Has additional |Gain )?(?<stats>\\+?"+Number+"(?<percent>%)? (?:Damage|Heal|Shield|Burn|Poison|Shock|Cooldown Speed)(?: and \\+?"+Number+" (?:Damage|Heal|Shield))?)(?<permanent> permanently)? for each (?<basis>Trinket that you own|Trinket that you have|different Trinket that you own|unique type on your team|Fire ally|Rock ally|Flying ally|ally of level (?<level>[1-4]) or above)\\.");
        if(scaled.Success)
        {
            var basis=scaled.Groups["basis"].Value;var type=basis is "Fire ally" or "Rock ally" or "Flying ally"?basis.Split(' ')[0].ToLowerInvariant():null;
            var scale=basis.StartsWith("different Trinket")?EffectScale.DistinctTrinkets:basis.StartsWith("Trinket")?EffectScale.Trinkets:basis.StartsWith("unique")?EffectScale.UniqueTeamTypes:EffectScale.Allies;
            effects=Regex.Matches(scaled.Groups["stats"].Value,"(?<amount>\\+?"+Number+")(?<percent>%)? (?<stat>"+Stats+")").Select(match=>
            {
                var stat=S(match.Groups["stat"].Value);var percent=match.Groups["percent"].Success;
                return new Effect(trigger,percent && stat!=Stat.Speed?Operation.Multiply:Operation.Add,Target.Self,stat,
                    N(match.Groups["amount"].Value)/(percent?100:1),scaled.Groups["permanent"].Success || trigger==Trigger.Bought,
                    Scale:scale,ScaleType:type,ScaleMinimumLevel:scaled.Groups["level"].Success?int.Parse(scaled.Groups["level"].Value):null);
            }).ToArray();return true;
        }
        var ongoingStatus=M("Has additional (?<stat>Damage|Shield|Burn) equal to (?<amount>"+Number+")% of the (?<status>Poison|Burn) stacks on the enemy\\.");
        if(ongoingStatus.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,S(ongoingStatus.Groups["stat"].Value),N(ongoingStatus.Groups["amount"].Value)/100,
            Scale:EffectScale.EnemyStatus,ScaleStat:S(ongoingStatus.Groups["status"].Value))];return true;}
        var ownStat=M("Has additional Damage equal to (?<amount>"+Number+") times this monster's (?<stat>Burn|Sell Value)\\.");
        if(ownStat.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Amount:N(ownStat.Groups["amount"].Value),Scale:EffectScale.SelfStat,ScaleStat:S(ownStat.Groups["stat"].Value))];return true;}
        var lives=M("(?<amount>\\+"+Number+") Damage permanently for each life lost this run\\.");
        if(lives.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Amount:N(lives.Groups["amount"].Value),Permanent:true,Scale:EffectScale.LostLives)];return true;}
        var ongoingAura=M("(?<target>Adjacent allies|Ally behind) (?:have|has) (?<amount>\\+"+Number+")% Cooldown Speed for each Trinket that you own\\.");
        if(ongoingAura.Success)
        {effects=[new(trigger,Operation.Add,ongoingAura.Groups["target"].Value=="Adjacent allies"?Target.Adjacent:Target.Left,Stat.Speed,
            N(ongoingAura.Groups["amount"].Value)/100,Scale:EffectScale.Trinkets)];return true;}
        var multiCount=M("(?<shield>\\+"+Number+") Shield permanently for each Rock ally and (?<multicast>\\+"+Number+") Multicast permanently for each Flying ally\\.");
        if(multiCount.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Stat.Shield,N(multiCount.Groups["shield"].Value),true,Scale:EffectScale.Allies,ScaleType:"rock"),
            new(trigger,Operation.Add,Target.Self,Stat.Multicast,N(multiCount.Groups["multicast"].Value),true,Scale:EffectScale.Allies,ScaleType:"flying")];return true;}
        var fireSpeed=M("For each Fire ally, this gains (?<amount>\\+"+Number+")% Cooldown Speed permanently\\.");
        if(fireSpeed.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Stat.Speed,N(fireSpeed.Groups["amount"].Value)/100,true,Scale:EffectScale.Allies,ScaleType:"fire")];return true;}
        var knockoutGain=M("Knockout adjacent allies and gain (?<amount>\\+"+Number+") (?<stat>Poison|Shield)(?<permanent> permanently| this battle) for each ally Knockout\\.");
        if(knockoutGain.Success)
        {effects=[new(trigger,Operation.Knockout,Target.Adjacent,KnockoutStat:S(knockoutGain.Groups["stat"].Value),
            KnockoutBonus:N(knockoutGain.Groups["amount"].Value),KnockoutPermanent:knockoutGain.Groups["permanent"].Value==" permanently")];return true;}
        var knockoutEnemy=M("Knockout (?<target>a random enemy|the monster opposite of this)(?<steal> and gain their stats for this battle \\(except Cooldown\\))?\\.");
        if(knockoutEnemy.Success)
        {effects=[new(trigger,Operation.Knockout,knockoutEnemy.Groups["target"].Value=="a random enemy"?Target.RandomEnemy:Target.Opposite,
            StealKnockoutStats:knockoutEnemy.Groups["steal"].Success)];return true;}
        var knockoutEvent=M("On Knockout of (?<who>this or an ally|any monster), this gains (?<amount>\\+"+Number+") (?<stat>Burn|Damage) permanently\\.");
        if(knockoutEvent.Success)
        {
            var e=new Effect(Trigger.Knockout,Operation.Add,Target.Self,S(knockoutEvent.Groups["stat"].Value),N(knockoutEvent.Groups["amount"].Value),true);
            effects=[e,e with {Trigger=knockoutEvent.Groups["who"].Value=="any monster"?Trigger.AnyKnockout:Trigger.AllyKnockout}];return true;
        }
        var allyKnockout=M("On ally knockout, this gains (?<amount>"+Number+")% of their Damage for this battle\\. \\(Except other (?<except>[A-Za-z]+)\\)");
        if(allyKnockout.Success)
        {effects=[new(Trigger.AllyKnockout,Operation.Add,Target.Self,Amount:N(allyKnockout.Groups["amount"].Value)/100,
            Scale:EffectScale.EventStat,EventExcludeSpecies:allyKnockout.Groups["except"].Value.ToLowerInvariant())];return true;}
        var revive=M("Knocked-out allies are revived and gain (?<speed>\\+"+Number+")% Cooldown Speed for this battle\\.");
        if(revive.Success)
        {effects=[new(trigger,Operation.Revive,Target.All,Stat.Speed,N(revive.Groups["speed"].Value)/100)];return true;}
        var selfRevive=M("(?<amount>\\+"+Number+") Multicast permanently\\. Revives itself once per battle\\.");
        if(selfRevive.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Stat.Multicast,N(selfRevive.Groups["amount"].Value),true),
            new(Trigger.Knockout,Operation.Revive,Target.Self,OncePerBattle:true)];return true;}
        var levelUp=M("(?<first>On the first cast, )?[Ll]evel up (?<target>the ally above|all adjacent allies) for this battle \\(max level (?<max>[1-4])\\)\\.");
        if(levelUp.Success)
        {effects=[new(trigger,Operation.LevelUp,levelUp.Groups["target"].Value=="the ally above"?Target.Above:Target.Adjacent,
            MaximumLevel:int.Parse(levelUp.Groups["max"].Value),OncePerBattle:levelUp.Groups["first"].Success)];return true;}
        var frontMulticast=M("Give the ally in front (?<amount>\\+"+Number+") Multicast and increase this monster's Cooldown by (?<cooldown>"+Number+") seconds for this battle\\.");
        if(frontMulticast.Success)
        {effects=[new(trigger,Operation.Add,Target.Right,Stat.Multicast,N(frontMulticast.Groups["amount"].Value)),
            new(trigger,Operation.AddedCooldown,Target.Self,Amount:N(frontMulticast.Groups["cooldown"].Value))];return true;}
        var reactiveTrigger=M("Trigger this when (?<who>an ally of level (?<level>[1-4]) or above casts|an ally applies Shield|adjacent Toxic allies trigger)\\. \\(Except other (?<except>[A-Za-z]+)\\)");
        if(reactiveTrigger.Success)
        {effects=[new(reactiveTrigger.Groups["who"].Value=="an ally applies Shield"?Trigger.AllyShield:Trigger.AllyCast,Operation.Trigger,Target.Self,
            EventType:reactiveTrigger.Groups["who"].Value=="adjacent Toxic allies trigger"?"toxic":null,
            EventExcludeSpecies:reactiveTrigger.Groups["except"].Value.ToLowerInvariant(),EventMinimumLevel:reactiveTrigger.Groups["level"].Success?int.Parse(reactiveTrigger.Groups["level"].Value):null,
            EventAdjacent:reactiveTrigger.Groups["who"].Value=="adjacent Toxic allies trigger")];return true;}
        var randomTrigger=M("Trigger a random Rock ally\\. \\(Except other Opalion\\)");
        if(randomTrigger.Success)
        {effects=[new(trigger,Operation.Trigger,Target.RandomAlly,Type:"rock",ExcludeSpecies:"opalion")];return true;}
        var rowTrigger=M("Trigger this and allies in this row\\.");
        if(rowTrigger.Success){effects=[new(trigger,Operation.Trigger,Target.Row)];return true;}
        var teamShield=M("When you receive shield, this gains Damage for this battle equal to (?<amount>"+Number+")% of the amount shielded\\.");
        if(teamShield.Success)
        {effects=[new(Trigger.TeamShield,Operation.Add,Target.Self,Amount:N(teamShield.Groups["amount"].Value)/100,Scale:EffectScale.EventStat,ScaleStat:Stat.Shield)];return true;}
        var teamBurn=M("Whenever your team inflicts Burn, gain (?<amount>\\+"+Number+")% Damage this battle\\.");
        if(teamBurn.Success)
        {effects=[new(Trigger.TeamBurn,Operation.Multiply,Target.Self,Amount:N(teamBurn.Groups["amount"].Value)/100)];return true;}
        var poisonReaction=M("When allies inflict Poison, this gains (?<amount>\\+"+Number+") Damage permanently\\.");
        if(poisonReaction.Success)
        {effects=[new(Trigger.AllyPoison,Operation.Add,Target.Self,Amount:N(poisonReaction.Groups["amount"].Value),Permanent:true)];return true;}
        var levelReaction=M("When an ally levels up, this gains (?<amount>\\+"+Number+") Damage and (?<heal>\\+"+Number+") Heal permanently\\.");
        if(levelReaction.Success)
        {effects=[new(Trigger.AllyAnyLevelUp,Operation.Add,Target.Self,Amount:N(levelReaction.Groups["amount"].Value),Permanent:true),
            new(Trigger.AllyAnyLevelUp,Operation.Add,Target.Self,Stat.Heal,N(levelReaction.Groups["heal"].Value),true)];return true;}
        var lostVictory=M("Activate the On Victory abilities of all allies (?<count>[0-9]+) time\\(s\\)\\.");
        if(lostVictory.Success)
        {effects=[new(trigger,Operation.ActivateAbility,Target.All,Amount:N(lostVictory.Groups["count"].Value),SubjectTrigger:Trigger.Victory)];return true;}
        var disableRow=M("Disable abilities of (?<who>all|enemy) Ongoing monsters in this row\\.");
        if(disableRow.Success)
        {effects=[new(trigger,Operation.Disable,disableRow.Groups["who"].Value=="all"?Target.BothRows:Target.EnemyRow,RequireOngoing:true)];return true;}
        var stealEnemy=M("Gain (?<amount>"+Number+")% of the stats of the enemy monster with the highest stats permanently \\(excluding Multicast and Cooldown\\)\\.");
        if(stealEnemy.Success)
        {effects=[new(trigger,Operation.CopyEnemyStats,Target.Self,Amount:N(stealEnemy.Groups["amount"].Value)/100,Permanent:true)];return true;}
        var alliedStat=M("Has additional Damage equal to (?<amount>"+Number+")% of the total Damage of (?<who>adjacent allies|your allies)\\. \\(Except other (?<except>[A-Za-z]+)\\)");
        if(alliedStat.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Amount:N(alliedStat.Groups["amount"].Value)/100,Scale:EffectScale.AlliedStat,
            ScaleTarget:alliedStat.Groups["who"].Value=="adjacent allies"?Target.Adjacent:Target.All,ScaleExcludeSpecies:alliedStat.Groups["except"].Value.ToLowerInvariant())];return true;}
        var copyAlliedStat=M("Gain (?<stat>Shield|Poison) for this battle equal to (?:(?<amount>"+Number+")x )?(?:the )?(?<total>total )?(?<sourceStat>Shield|Poison) of (?<who>adjacent allies|your allies)\\. \\(Except other (?<except>[A-Za-z]+)\\)");
        if(copyAlliedStat.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,S(copyAlliedStat.Groups["stat"].Value),copyAlliedStat.Groups["amount"].Success?N(copyAlliedStat.Groups["amount"].Value):1,
            Scale:EffectScale.AlliedStat,ScaleStat:S(copyAlliedStat.Groups["sourceStat"].Value),
            ScaleTarget:copyAlliedStat.Groups["who"].Value=="adjacent allies"?Target.Adjacent:Target.All,ScaleExcludeSpecies:copyAlliedStat.Groups["except"].Value.ToLowerInvariant())];return true;}
        var burnTransfer=M("Give the Fire ally behind (?:(?<amount>"+Number+")x )?this monster's Burn for this battle\\.");
        if(burnTransfer.Success)
        {effects=[new(trigger,Operation.Add,Target.Left,Stat.Burn,burnTransfer.Groups["amount"].Success?N(burnTransfer.Groups["amount"].Value):1,
            Type:"fire",Scale:EffectScale.SelfStat,ScaleStat:Stat.Burn)];return true;}
        var allyShieldDamage=M("Allies gain Damage for this battle equal to (?:(?<amount>"+Number+")x )?their Shield\\.");
        if(allyShieldDamage.Success)
        {effects=[new(trigger,Operation.CopyStat,Target.All,Amount:allyShieldDamage.Groups["amount"].Success?N(allyShieldDamage.Groups["amount"].Value):1,ScaleStat:Stat.Shield)];return true;}
        var toxicCast=M("When a Toxic ally casts, give it (?<amount>\\+"+Number+")% Cooldown Speed for this battle\\.");
        if(toxicCast.Success)
        {effects=[new(Trigger.AllyCast,Operation.Add,Target.Actor,Stat.Speed,N(toxicCast.Groups["amount"].Value)/100,EventType:"toxic")];return true;}
        var firstGift=M("On the first cast, gain (?<count>[0-9]+) random non-unique Legendary Trinket\\(s\\) for each Legendary ally\\.");
        if(firstGift.Success)
        {effects=[new(trigger,Operation.GrantTrinket,Target.Self,Amount:N(firstGift.Groups["count"].Value),Rarity:4,NonUnique:true,
            Scale:EffectScale.Allies,ScaleRarity:4,OncePerBattle:true)];return true;}
        var sellDamage=M("(?<sell>\\+"+Number+") Sell Value permanently\\. Ongoing Has additional Damage equal to (?<amount>"+Number+") times this monster's Sell Value\\.");
        if(sellDamage.Success)
        {effects=[new(trigger,Operation.Add,Target.Self,Stat.SellValue,N(sellDamage.Groups["sell"].Value),true),
            new(Trigger.Ongoing,Operation.Add,Target.Self,Amount:N(sellDamage.Groups["amount"].Value),Scale:EffectScale.SelfStat,ScaleStat:Stat.SellValue)];return true;}
        var commonKnockout=M("Knockout all Common allies and enemies(?<gain> and gain (?<amount>\\+"+Number+") Damage permanently for each Knockout)?\\.");
        if(commonKnockout.Success)
        {effects=[new(trigger,Operation.Knockout,Target.BothTeams,Rarity:0,
            KnockoutStat:commonKnockout.Groups["gain"].Success?Stat.Damage:null,KnockoutBonus:commonKnockout.Groups["amount"].Success?N(commonKnockout.Groups["amount"].Value):0,
            KnockoutPermanent:commonKnockout.Groups["gain"].Success)];return true;}
        var growingGift=M("When this triggers, gain (?:(?<count>[0-9]+)|a) random non-unique Common Trinkets?, then improve this ability for this battle\\.");
        if(growingGift.Success)
        {effects=[new(Trigger.Cast,Operation.GrantTrinket,Target.Self,Amount:growingGift.Groups["count"].Success?N(growingGift.Groups["count"].Value):1,
            Rarity:0,NonUnique:true,GrowthPerTrigger:1)];return true;}
        var badges=M("Has an additional (?<amount>\\+"+Number+") Damage for each Badge that you have\\.");
        if(badges.Success){effects=[new(trigger,Operation.Add,Target.Self,Amount:N(badges.Groups["amount"].Value),Scale:EffectScale.Badges)];return true;}
        var hpGrowth=M("Your team has (?<hp>\\+"+Number+") HP\\. On Victory Increase this ability's HP bonus by (?<amount>"+Number+")\\.");
        if(hpGrowth.Success)
        {effects=[new(Trigger.Ongoing,Operation.TeamHp,Target.Self,Amount:N(hpGrowth.Groups["hp"].Value),ProgressCounter:"team-hp"),
            new(Trigger.Victory,Operation.GrowAbility,Target.Self,Amount:N(hpGrowth.Groups["amount"].Value),Permanent:true,ProgressCounter:"team-hp")];return true;}
        var berries=M("The next (?<count>[0-9]+) items in your shop will be Berries\\.(?: When you use an item, this gains (?<damage>\\+"+Number+") Damage\\.)?");
        if(berries.Success)
        {
            var list=new List<Effect>{new(Trigger.Bought,Operation.ForceBerryItems,Target.Self,Amount:N(berries.Groups["count"].Value))};
            if(berries.Groups["damage"].Success)list.Add(new(Trigger.ItemUsed,Operation.Add,Target.Self,Amount:N(berries.Groups["damage"].Value),Permanent:true));
            effects=list.ToArray();return true;
        }
        var sameRarityTransform=M("Give adjacent allies (?<amount>\\+"+Number+") Multicast permanently and transform them into random monsters of their rarity\\.");
        if(sameRarityTransform.Success)
        {effects=[new(trigger,Operation.Transform,Target.Adjacent,Stat.Multicast,N(sameRarityTransform.Groups["amount"].Value),true,
            DefinitionId:"same-rarity",ReviveTargets:true,GrantTransformStat:true)];return true;}
        var battleTransform=M("(?:Gain (?<damage>\\+"+Number+") Damage permanently\\. )?For this battle, transform into a random monster (?<tier>[0-9]+) rarity above your shop's highest available rarity\\.");
        if(battleTransform.Success)
        {
            var list=new List<Effect>();if(battleTransform.Groups["damage"].Success)list.Add(new(trigger,Operation.Add,Target.Self,Amount:N(battleTransform.Groups["damage"].Value),Permanent:true));
            list.Add(new(trigger,Operation.Transform,Target.Self,Amount:N(battleTransform.Groups["tier"].Value),DefinitionId:"above-shop"));effects=list.ToArray();return true;
        }
        if(text=="Level up. This can level up indefinitely.")
        {effects=[new(trigger,Operation.LevelUp,Target.Self,Permanent:true,UnlimitedLevels:true)];return true;}
        var unlimitedMerge=M("This only needs (?<count>[0-9]+) copies to level up\\. This can level up indefinitely\\.");
        if(unlimitedMerge.Success)
        {effects=[new(Trigger.Ongoing,Operation.MergeRequirement,Target.Self,Amount:N(unlimitedMerge.Groups["count"].Value),UnlimitedLevels:true)];return true;}
        var amplify=M("(?<who>Adjacent allies apply|The ally behind applies) (?<their>their|its) Ongoing abilities (?<count>[0-9]+) additional time\\(s\\)\\.");
        if(amplify.Success)
        {effects=[new(Trigger.Ongoing,Operation.RepeatOngoing,amplify.Groups["who"].Value=="Adjacent allies apply"?Target.Adjacent:Target.Left,Amount:N(amplify.Groups["count"].Value))];return true;}
        var slowRow=M("Allies and enemies in this row have (?<amount>\\+"+Number+") Cooldown\\.");
        if(slowRow.Success)
        {effects=[new(Trigger.Ongoing,Operation.AddedCooldown,Target.BothRows,Amount:N(slowRow.Groups["amount"].Value))];return true;}
        var timer=M("Every (?<seconds>[0-9]+(?:\\.[0-9]+)?) seconds, (?<amount>\\+"+Number+") Multicast for this battle\\.");
        if(timer.Success && N(timer.Groups["seconds"].Value)>0)
        {effects=[new(Trigger.Timer,Operation.Add,Target.Self,Stat.Multicast,N(timer.Groups["amount"].Value),IntervalSeconds:N(timer.Groups["seconds"].Value))];return true;}
        var mythicalItem=M("Mythical Item may appear in your shop\\. Ongoing Adjacent allies have (?<amount>\\+"+Number+")% Damage per Mythical Item used\\.");
        if(mythicalItem.Success)
        {effects=[new(Trigger.Ongoing,Operation.ShopModifier,Target.Self,Amount:.1,ShopTrait:ShopTrait.MythicalItemChance),
            new(Trigger.Ongoing,Operation.Multiply,Target.Adjacent,Amount:N(mythicalItem.Groups["amount"].Value)/100,Scale:EffectScale.ItemsUsed,ScaleRarity:5)];return true;}
        if(text=="At the start of the next day, devour the ally in front and evolve into Rigalord.")
        {effects=[new(Trigger.DayStart,Operation.Devour,Target.Right,Permanent:true,DefinitionId:"rigalord")];return true;}
        if(text=="Spawn exact copies of the Batomon this devoured in empty slots the bottom row. They ONLY trigger when this casts.")
        {effects=[new(Trigger.Cast,Operation.SpawnDevoured,Target.Self)];return true;}
        var discarded=M("When rerolling the shop, gain (?<amount>"+Number+")% of the total stats of the monsters remaining in the shop \\(excluding Multicast and Cooldown\\)\\.");
        if(discarded.Success)
        {
            effects=[new(Trigger.ShopDiscard,Operation.DiscardStats,Target.Self,Amount:N(discarded.Groups["amount"].Value)/100,Permanent:true)];return true;
        }
        var grant=M("Gain (?:(?<count>[0-9]+)|a) (?<random>random non-unique (?<rarity>Common|Uncommon|Rare|Super Rare|Legendary|Mythical)|Junk) Trinkets?\\.");
        if(grant.Success)
        {
            var tier=grant.Groups["rarity"].Value switch {"Common"=>0,"Uncommon"=>1,"Rare"=>2,"Super Rare"=>3,"Legendary"=>4,"Mythical"=>5,_=>0};
            effects=[new(trigger,Operation.GrantTrinket,Target.Self,Amount:grant.Groups["count"].Success?N(grant.Groups["count"].Value):1,
                Rarity:tier,DefinitionId:grant.Groups["random"].Value=="Junk"?"junk":null,NonUnique:true)];return true;
        }
        var triggerGain=M("Trigger this and gain (?<amount>\\+"+Number+") (?<stat>"+Stats+") permanently\\.");
        if(triggerGain.Success)
        {
            effects=[new(trigger,Operation.Trigger,Target.Self),new(trigger,Operation.Add,Target.Self,S(triggerGain.Groups["stat"].Value),N(triggerGain.Groups["amount"].Value),true)];return true;
        }
        var protectGain=M("Give adjacent allies (?<amount>\\+"+Number+") Damage permanently and Protect (?<protect>[0-9]+)\\.");
        if(protectGain.Success)
        {
            effects=[new(trigger,Operation.Add,Target.Adjacent,Amount:N(protectGain.Groups["amount"].Value),Permanent:true),new(trigger,Operation.Protect,Target.Adjacent,Amount:N(protectGain.Groups["protect"].Value))];return true;
        }
        var triggerAlly=M("Trigger the (?<type>"+Types+") ally above and give it (?<amount>\\+"+Number+") Damage permanently\\.");
        if(triggerAlly.Success)
        {
            var type=triggerAlly.Groups["type"].Value.ToLowerInvariant();
            effects=[new(trigger,Operation.Trigger,Target.Above,Type:type),new(trigger,Operation.Add,Target.Above,Amount:N(triggerAlly.Groups["amount"].Value),Permanent:true,Type:type)];return true;
        }
        var activation=M("Activate the (?<trigger>On Bought|On Victory|On Battle Start) abilit(?:y|ies) of (?<target>the ally above|adjacent allies)(?: (?<count>[0-9]+) time\\(s\\))?(?: and increase this monster's Cooldown by (?<cooldown>"+Number+") for this battle)?\\.");
        if(activation.Success)
        {
            var list=new List<Effect>{new(trigger,Operation.ActivateAbility,activation.Groups["target"].Value=="the ally above"?Target.Above:Target.Adjacent,
                Amount:activation.Groups["count"].Success?N(activation.Groups["count"].Value):1,SubjectTrigger:RuleCompiler.ParseTrigger(activation.Groups["trigger"].Value))};
            if(activation.Groups["cooldown"].Success)list.Add(new(trigger,Operation.AddedCooldown,Target.Self,Amount:N(activation.Groups["cooldown"].Value)));
            effects=list.ToArray();return true;
        }
        var charge=M("Charge (?<target>the ally behind|adjacent (?<type>"+Types+") allies) by (?<amount>"+Number+") second\\(s\\)(?: and give this (?<speed>\\+"+Number+")% Cooldown Speed for this battle)?\\. \\((?:Dracana|Ironcore) can't receive charge\\)");
        if(charge.Success)
        {
            var list=new List<Effect>{new(trigger,Operation.Charge,charge.Groups["target"].Value=="the ally behind"?Target.Left:Target.Adjacent,
                Amount:N(charge.Groups["amount"].Value),Type:charge.Groups["type"].Success?charge.Groups["type"].Value.ToLowerInvariant():null)};
            if(charge.Groups["speed"].Success)list.Add(new(trigger,Operation.Add,Target.Self,Stat.Speed,N(charge.Groups["speed"].Value)/100));
            effects=list.ToArray();return true;
        }
        var dualCharge=M("Charge adjacent Fire and Water allies by (?<amount>"+Number+") second\\(s\\)\\. \\(Steamscuttle can't receive charge\\)");
        if(dualCharge.Success)
        {effects=[new(trigger,Operation.Charge,Target.Adjacent,Amount:N(dualCharge.Groups["amount"].Value),Type:"fire|water")];return true;}
        var chargeEvent=M("Whenever an ally inflicts (?<status>Shock|Poison), Charge this by (?<amount>"+Number+") second\\(s\\)\\.");
        if(chargeEvent.Success)
        {
            effects=[new(chargeEvent.Groups["status"].Value=="Shock"?Trigger.AllyShock:Trigger.AllyPoison,Operation.Charge,Target.Self,Amount:N(chargeEvent.Groups["amount"].Value))];return true;
        }
        var itemGain=M("When you use an item, this gains (?<amount>\\+"+Number+") Damage and Shield\\.");
        if(itemGain.Success)
        {
            effects=[new(Trigger.ItemUsed,Operation.Add,Target.Self,Stat.Damage,N(itemGain.Groups["amount"].Value),true),new(Trigger.ItemUsed,Operation.Add,Target.Self,Stat.Shield,N(itemGain.Groups["amount"].Value),true)];return true;
        }
        var purchaseGain=M("After you buy a (?<type>"+Types+") monster, this gains (?<amount>\\+"+Number+")(?<percent>%)? (?<stat>"+Stats+")\\.");
        if(purchaseGain.Success)
        {
            var stat=S(purchaseGain.Groups["stat"].Value);var percent=purchaseGain.Groups["percent"].Success;
            effects=[new(Trigger.AllyBought,percent && stat!=Stat.Speed?Operation.Multiply:Operation.Add,Target.Self,stat,
                N(purchaseGain.Groups["amount"].Value)/(percent?100:1),true,EventType:purchaseGain.Groups["type"].Value.ToLowerInvariant())];return true;
        }
        var buffText=text.Replace("This and all your allies","This and allies",StringComparison.Ordinal);
        buffText=Regex.Replace(buffText,"^Adjacent ("+Types+") allies", "$1 adjacent allies");
        buffText=Regex.Replace(buffText,"^(Adjacent allies|Ally behind|Allies)",m=>char.ToLowerInvariant(m.Value[0])+m.Value[1..]);
        var buff=Regex.Match(buffText,"^(?:(?<self>This and )(?:your )?|Give (?:the )?|Your )?(?:(?<type>"+Types+") |(?<rarity>Common|Uncommon|Rare|Super Rare|Legendary) )?(?<target>adjacent allies|ally above|ally behind|ally in front|allies|monsters|team)(?<minimum> of level (?<level>[1-4]) or above)?(?<noAbility> with no abilities)? (?:gain|have|has)? ?(?<stats>\\+"+Number+"%? (?:"+Stats+")(?: and \\+"+Number+"%? (?:"+Stats+"))*)(?<permanent> permanently| for the rest of the run)?(?<temporary> for this battle)?\\.(?: \\(Zephyrex can't have Multicast\\))?$",RegexOptions.CultureInvariant);
        if(buff.Success)
        {
            var target=buff.Groups["target"].Value;var type=buff.Groups["type"].Success?buff.Groups["type"].Value.ToLowerInvariant():null;
            int? rarity=buff.Groups["rarity"].Success?buff.Groups["rarity"].Value switch {"Common"=>0,"Uncommon"=>1,"Rare"=>2,"Super Rare"=>3,_=>4}:null;
            effects=Regex.Matches(buff.Groups["stats"].Value,"(?<amount>\\+"+Number+")(?<percent>%)? (?<stat>"+Stats+")").Select(match=>
            {
                var stat=S(match.Groups["stat"].Value);var percent=match.Groups["percent"].Success;
                return new Effect(trigger,percent && stat!=Stat.Speed?Operation.Multiply:Operation.Add,
                    target is "team" or "monsters"?Target.All:T(target),stat,N(match.Groups["amount"].Value)/(percent?100:1),
                    buff.Groups["permanent"].Success || trigger==Trigger.Bought,type,Rarity:rarity,
                    MinimumLevel:buff.Groups["minimum"].Success?int.Parse(buff.Groups["level"].Value):null,
                    RequireNoAbility:buff.Groups["noAbility"].Success,IncludeSelf:buff.Groups["self"].Success);
            }).ToArray();return true;
        }
        return false;
    }
}
