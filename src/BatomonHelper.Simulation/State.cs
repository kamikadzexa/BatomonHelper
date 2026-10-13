using System.Text.Json;

namespace BatomonHelper.Simulation;

public enum Stat { Damage, Heal, Shield, Burn, Poison, Shock, Speed, Multicast, SellValue }
public enum Phase { Shop, Reward, Event, TrainerChoice, Overflow, Complete }
public enum Move { Buy, BuyItem, Sell, Move, Merge, Reroll, Lock, Choose, Finish }
public enum Trigger { None, Cast, BattleStart, Bought, Victory, Defeat, Knockout, DayStart, ItemUsed, TrinketGained, AllyBought, AllyCast, AllyShield, AllyPoison, AllyBurn, AllyShock, Ongoing, AllyLevelUp, RunStart, ShopSpawn, ShopDiscard, ItemBought, AllyKnockout, AnyKnockout, TeamShield, TeamBurn, AllyAnyLevelUp, TeamDamage, Timer, DayEnd }
public enum Target { Self, All, Adjacent, Above, Below, Left, Right, Row, Column, Opposite, RandomAlly, RandomEnemy, Enemies, BottomRight, Selected, Shop, ShopAndTeam, ShopAndAll, TopMiddle, LeftColumn, RightColumn, EnemyRow, BothRows, BothTeams, Actor }
public enum Operation { Add, Multiply, Charge, Trigger, Knockout, Protect, Revive, Cleanse, LevelUp, Gold, AddType, Disable, Transform, CopyStat, TeamHp, GrantTrinket,
    ShopRank, FreeReroll, ItemUses, DiscountShop, RollShop, TrinketGift, GrantUnit, RecruitOffer, Shiny, NextBattleStartRepeat, NextVictoryGold, NextCopyEnemy, LoseLife, ActivateAbility, AddedCooldown, ShopModifier, DiscardStats, Recombine, PendingPurchase, LinkAllies, CopyEnemyStats, Evolve, ProgressEvolution, Hatch, GrowAbility, ForceBerryItems, MergeRequirement, RepeatOngoing, Devour, SpawnDevoured, InventoryRule, TrainerRule }
public enum ShopTrait { LegendaryWeight, MinimumRarity, ForcedShiny, ShinyMultiplier, ItemTierBonus, ItemDiscount,
    CreatureDiscount, FirstCommonFree, FirstBugFree, NoItems, Income, DailyItemUses, RepeatBought, RepeatZeroItem, MythicalItemChance }
public enum EffectScale { None, Trinkets, CommonTrinkets, UniqueTeamTypes, Allies, EnemyStatus, SelfStat, LostLives, EventStat, AlliedStat, Badges, ItemsUsed }
public sealed record Effect(Trigger Trigger, Operation Operation, Target Target, Stat Stat = Stat.Damage,
    double Amount = 0, bool Permanent = false, string? Type = null, string? ExcludeSpecies = null,
    int? Rarity = null, int? MinimumLevel = null, int? MaximumLevel = null, bool Percent = false,
    string? DefinitionId = null, int? MaximumRarity = null, int TargetCount = 1, bool RequireNoAbility = false,
    bool IncludeSelf = false, bool NonUnique = false, Trigger SubjectTrigger = Trigger.None, string? EventType = null,
    ShopTrait? ShopTrait = null, int MinimumDay = 1, bool ForceShiny = false, bool Foreign = false, bool AdmitToShop = false,
    EffectScale Scale = EffectScale.None, Stat ScaleStat = Stat.Damage, string? ScaleType = null, int? ScaleMinimumLevel = null,
    bool OncePerBattle = false, Stat? KnockoutStat = null, double KnockoutBonus = 0, bool KnockoutPermanent = false,
    bool StealKnockoutStats = false, string? EventExcludeSpecies = null, int? EventMinimumLevel = null, bool EventAdjacent = false,
    bool RequireOngoing = false, Target ScaleTarget = Target.All, string? ScaleExcludeSpecies = null, int? ScaleRarity = null,
    string? ProgressCounter = null, Stat? ProgressStat = null, bool DeferredEvolution = false, bool RequireNoOwnedEvolution = false,
    double GrowthPerTrigger = 0, bool ReviveTargets = false, bool GrantTransformStat = false, bool UnlimitedLevels = false, double IntervalSeconds = 0);
public sealed record LevelDefinition(int Level, double? Cooldown, double[] Stats, Effect[] Effects, string? Description = null, Trigger AbilityTrigger = Trigger.None);
public sealed record Species(string Id, string Name, int Rarity, double Cost, string MergeGroup,
    string[] Types, bool ShopEligible, bool Pantra, string? EvolutionId, int? EvolutionLevel,
    LevelDefinition[] Normal, LevelDefinition[] Shiny, string SourceUrl, bool ForeignEligible = false);
public sealed record InventoryDefinition(string Id, string Name, int Rarity, bool Unique,
    double Cost, Effect[] Effects, string SourceUrl, string Description);
public sealed record RuleIssue(string Id, string Kind, string Reason, string SourceUrl);
public sealed record RulesManifest(string Build, string Balance, string Version, string Hash,
    Species[] Species, InventoryDefinition[] Items, InventoryDefinition[] Trinkets,
    InventoryDefinition[] Trainers, RuleIssue[] Issues, RunParameters Parameters, int SimulationRevision = 8,
    RuleEvidence[]? Evidence = null, EncounterDefinition[]? Encounters = null, SimulationProfile? Profile = null)
{
    public const int CurrentRevision = 8;
    public int GuessedRules => Evidence?.Count(x=>x.Kind==EvidenceKind.Guessed)??0;
    public bool Ready => SimulationRevision == CurrentRevision && Issues.Length == 0 && Parameters.Usable && (GuessedRules==0 || Parameters.AllowGuesses);
    public bool FullFidelityReady => Build!="fixture" && Ready && Parameters.Verified && GuessedRules==0;
    public void RequireReady()
    {
        if(SimulationRevision!=CurrentRevision)throw new InvalidOperationException($"Simulation revision {SimulationRevision} is incompatible with runtime revision {CurrentRevision}.");
        if (!Ready) throw new InvalidOperationException($"Training blocked: {Issues.Length} unresolved rules. " +
            string.Join("; ", Issues.Take(4).Select(x => x.Id + ": " + x.Reason)));
    }
}
public sealed record RunParameters(double InitialGold, double[] DailyIncome, double[] DailyHp,
    int InitialLives = 10, int WinTarget = 10, bool Verified = false, bool AllowGuesses = false,
    ValueEvidence[]? HpEvidence = null,ValueEvidence[]? IncomeEvidence = null)
{
    public bool Usable => (Verified || AllowGuesses) && DailyIncome.Length>0 && DailyHp.Length==DailyIncome.Length;
}
public sealed record SimAction(Move Kind, int Source = -1, int Target = -1, int Choice = -1);

/// <summary>Copyable RNG state. Neither observation nor policy encoding exposes it.</summary>
public sealed class SeedStream(ulong state)
{
    public ulong State { get; set; } = state;
    public ulong Next()
    {
        State += 0x9E3779B97F4A7C15UL;
        var z = State; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double Uniform() => (Next() >> 11) * (1d / (1UL << 53));
    public int Index(int count) => count > 0 ? (int)(Next() % (uint)count) : throw new InvalidOperationException("Empty eligible pool.");
}
public sealed class Unit
{
    public long Instance { get; set; }
    public string SpeciesId { get; set; } = "";
    public int Level { get; set; } = 1;
    public bool Shiny { get; set; }
    public string[] AddedTypes { get; set; } = [];
    public double[] Bonuses { get; set; } = new double[9];
    public double StoredDiscount { get; set; }
    public int Copies { get; set; } = 1;
    public Dictionary<string, double> Counters { get; set; } = [];
    public Unit? Devoured { get; set; }
    public Unit Clone() => new() { Instance = Instance, SpeciesId = SpeciesId, Level = Level, Shiny = Shiny,
        AddedTypes = [.. AddedTypes], Bonuses = [.. Bonuses], StoredDiscount = StoredDiscount, Copies = Copies,
        Counters = new(Counters), Devoured=Devoured?.Clone() };
}
public sealed record BagEntry(string Id, int Quantity, long Instance = 0);
public sealed record Offer(Unit? Creature, string? ItemId, double Price, double? BasisPrice = null);
public sealed record Choice(string Label, string? DefinitionId, Effect[] Effects, TargetRule? TargetRule = null, bool AtShopStart = false);
public sealed record TargetRule(int? MinimumLevel=null,int? MaximumLevel=null,string? ExcludedType=null,bool NonShiny=false);
public sealed record EncounterDefinition(string Id,string Name,int Tier,Choice[] Choices,string SourceUrl,int MinimumGames=0,string Region="Pantra");
public sealed record ChoiceBatch(Phase Phase, Choice[] Choices,string? EventName=null,bool TakeAll=false);
public sealed record PurchaseBuff(long SourceInstance, Effect Effect);
public sealed class RunState
{
    public string RunId { get; set; } = "";
    public int Day { get; set; } = 1;
    public int Lives { get; set; } = 10;
    public int Wins { get; set; }
    public double Gold { get; set; }
    public double MaxHp { get; set; }
    public int Rank { get; set; } = 1;
    public string Trainer { get; set; } = "";
    public Unit?[] Units { get; set; } = new Unit?[10];
    public Offer?[] Shop { get; set; } = new Offer?[5];
    public BagEntry[] Bag { get; set; } = [];
    public string[] ExtraTrainers { get; set; } = [];
    public string[] ForeignPool { get; set; } = [];
    public Dictionary<string, double> Counters { get; set; } = [];
    public bool Locked { get; set; }
    public bool CarryShop { get; set; }
    public bool SecondChanceUsed { get; set; }
    public int FreeRerolls { get; set; }
    public int ItemUses { get; set; } = 1;
    public int MaximumItemUses { get; set; } = 1;
    public Phase Phase { get; set; }
    public Unit? Overflow { get; set; }
    public Unit[] PendingUnits { get; set; } = [];
    public Choice[] Choices { get; set; } = [];
    public ChoiceBatch[] PendingChoices { get; set; } = [];
    public Phase ActiveChoicePhase { get; set; } = Phase.Reward;
    public bool TakeAllChoices { get; set; }
    public ulong RandomState { get; set; }
    public long NextInstance { get; set; } = 1;
    public long NextInventoryInstance { get; set; } = 1;
    public string[] DayChoices { get; set; } = [];
    public bool PendingShopStart { get; set; }
    public Effect[] PendingShopEffects { get; set; } = [];
    public string? CurrentEvent { get; set; }
    public PurchaseBuff[] PendingPurchaseBuffs { get; set; } = [];
    public RunState Clone() => JsonSerializer.Deserialize<RunState>(JsonSerializer.Serialize(this))!;
}
public sealed record Observation(int Day, int Lives, int Wins, double Gold, double MaxHp, int Rank,
    string Trainer, Unit?[] Units, Offer?[] Shop, BagEntry[] Bag, string[] ExtraTrainers,
    bool Locked, int FreeRerolls, int ItemUses, int MaximumItemUses, Phase Phase, Choice[] Choices, Unit? Overflow,
    Dictionary<string, double>? Counters = null, VisibleRunState? VisibleState = null);
public sealed record VisibleRunState(string[] ForeignPool,bool CarryShop,bool SecondChanceUsed,Unit[] PendingUnits,
    bool TakeAllChoices,Effect[] PendingShopEffects,PurchaseBuff[] PendingPurchaseBuffs,bool PendingShopStart,Phase ActiveChoicePhase,string? CurrentEvent);
public sealed record FrozenBoard(int Day, double MaxHp, string Trainer, Unit?[] Team, BagEntry[] Bag, string[] ExtraTrainers,
    Dictionary<string, double>? Counters = null, int Rank = 1, string[]? ForeignPool = null, int Wins = 0)
{
    public static FrozenBoard From(RunState state) => new(state.Day, state.MaxHp, state.Trainer,
        state.Units.Take(6).Select(x => x?.Clone()).ToArray(), [.. state.Bag], [.. state.ExtraTrainers], new(state.Counters),state.Rank,[..state.ForeignPool],state.Wins);
}
public sealed record BattleResult(bool PlayerWon, bool BothDefeated, double Seconds, double PlayerHp,
    double OpponentHp, Unit?[] PersistentTeam, BagEntry[] Bag, double Gold, string[] Trace,Dictionary<string,double>? PersistentCounters=null,
    PurchaseBuff[]? PendingPurchaseBuffs=null);
public sealed record StepResult(Observation Observation, double Reward, bool Terminal, BattleResult? Battle = null,
    DayRecord? DayRecord = null);
public sealed record DayRecord(int Day, Observation BeforeBattle, FrozenBoard Opponent, BattleResult Result,
    int WinsAfter, int LivesAfter, string[] Choices);
public sealed record RunSummary(string RunId, int Generation, int Policy, string Trainer, int Wins,
    int Lives, int Days, bool Success, bool Evaluation, DateTimeOffset CompletedAt, string RulesHash, bool Pinned = false,
    int SnapshotUpdate = -1, string? EvaluationBatch = null)
{
    public string Outcome => Success?"Success":"Failure";
}
public sealed record RunRecord(RunSummary Summary, DayRecord[] Days);
