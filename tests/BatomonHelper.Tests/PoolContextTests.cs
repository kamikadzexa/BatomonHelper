using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;
public sealed class PoolContextTests
{
    [Fact]
    public void Captured_maxed_evolutions_exclude_their_bases_and_unique_ownership_survives_llm_removal()
    {
        var catalog=CatalogSnapshot.Empty with {
            Monsters=[new("alpha","Alpha",Rarity.Common,null,["starter"]),new("beta","Beta",Rarity.Common,null,["starter"]),
                new("evolved","Evolved",Rarity.Common,"alpha",["starter"])],
            Trinkets=[new("unique","Unique",Rarity.Common,IsUnique:true),new("ordinary","Ordinary",Rarity.Common)] };
        var board=new BoardSnapshot {Team=[new(0,SlotOccupancy.Creature,"evolved",ShinyState.Normal,3,[])],Bench=[]};
        var snapshot=new ShopOddsSnapshot(null,1,30,ShopRankOdds.ForRank(1),false,null,DateTimeOffset.UtcNow);
        var context=PoolContext.Create(catalog,snapshot,board,[],["unique"]);
        Assert.Contains("alpha",context.LevelThreeMonsterIds);Assert.Contains("evolved",context.LevelThreeMonsterIds);
        Assert.Equal("beta",Assert.Single(context.Odds.MonsterOdds).MonsterId);
        Assert.False(context.Odds.TrinketOdds.Single(x=>x.TrinketId=="unique").IsEligible);
        Assert.Equal(1,context.Odds.TrinketOdds.Single(x=>x.TrinketId=="ordinary").PerTrinketRoll);
    }
}
