namespace BatomonHelper.Simulation;

public static class InventoryRules
{
    private static Effect Buff(Stat stat, double amount, Target target = Target.All, Trigger trigger = Trigger.Ongoing,
        bool percent = false, bool permanent = false, string? type = null, EffectScale scale = EffectScale.None) =>
        new(trigger, percent && stat != Stat.Speed ? Operation.Multiply : Operation.Add, target, stat, amount, permanent, type, Scale: scale);
    private static Effect[] SixStats(double amount, Target target, string? type = null) =>
        Enumerable.Range(0, 6).Select(x => Buff((Stat)x, amount, target, percent: true, type: type)).ToArray();
    public static (string Text, Effect[] Effects)? Definition(string id) => id switch
    {
        "haste-crown" => ("Your top middle monster has +20% Cooldown Speed.", [Buff(Stat.Speed, .2, Target.TopMiddle)]),
        "power-crown" => ("Your top middle monster has +20 Damage.", [Buff(Stat.Damage, 20, Target.TopMiddle)]),
        "winged-crown" => ("Your top middle monster has +1 Multicast.", [Buff(Stat.Multicast, 1, Target.TopMiddle)]),
        "master-crown" => ("Your top middle monster has +80% stats (except Cooldown and Multicast).", SixStats(.8, Target.TopMiddle)),
        "quick-flag" => ("Your monsters in the leftmost column have +4% Cooldown Speed.", [Buff(Stat.Speed, .04, Target.LeftColumn)]),
        "speed-crest" => ("Your monsters in the rightmost column have +15% Cooldown Speed.", [Buff(Stat.Speed, .15, Target.RightColumn)]),
        "rally-flag" => ("On Battle Start Your monsters in the leftmost column gain +12 Damage permanently.", [Buff(Stat.Damage, 12, Target.LeftColumn, Trigger.BattleStart, permanent: true)]),
        "momentum-flag" => ("On Battle Start Your monsters in the leftmost column gain +8% Cooldown Speed permanently.", [Buff(Stat.Speed, .08, Target.LeftColumn, Trigger.BattleStart, permanent: true)]),
        "earth-crest" => ("On Battle Start Your monsters in the rightmost column gain +10% Shield permanently.", [Buff(Stat.Shield, .1, Target.RightColumn, Trigger.BattleStart, percent: true, permanent: true)]),
        "sea-crest" => ("On Battle Start Your monsters in the rightmost column gain +10% Heal permanently.", [Buff(Stat.Heal, .1, Target.RightColumn, Trigger.BattleStart, percent: true, permanent: true)]),
        "tempo-charm" => ("On Battle Start A random monster gains +4% Cooldown Speed permanently.", [Buff(Stat.Speed, .04, Target.RandomAlly, Trigger.BattleStart, permanent: true)]),
        "gold-bracelet" => ("At the start of each day, give a random monster +8 Sell Value.", [Buff(Stat.SellValue, 8, Target.RandomAlly, Trigger.DayStart, permanent: true)]),
        "winged-fossil" => ("Your Flying monsters have +25% stats (except Cooldown and Multicast).", SixStats(.25, Target.All, "flying")),
        "fancy-sword" => ("Your monsters have +3% Damage for each Trinket you have.", [Buff(Stat.Damage, .03, percent: true, scale: EffectScale.Trinkets)]),
        "scrap-sword" => ("Your monsters have +2 Damage for each Common Trinket you have.", [Buff(Stat.Damage, 2, scale: EffectScale.CommonTrinkets)]),
        "rainbow-berry" => ("Your monsters have +4 Damage for each unique type on your team.", [Buff(Stat.Damage, 4, scale: EffectScale.UniqueTeamTypes)]),
        "metronome" => ("When your monsters cast, they gain +10 Damage for this battle.", [Buff(Stat.Damage, 10, Target.Self, Trigger.Cast)]),
        "repeater-charm" => ("Your monsters' On Battle Start abilities activate an additional time.", [new(Trigger.Ongoing, Operation.NextBattleStartRepeat, Target.All, Amount: 1)]),
        "link-cable" => ("All of your team's monsters are now considered adjacent to each other.", [new(Trigger.Ongoing, Operation.LinkAllies, Target.All)]),
        _ => null
    };
    public static bool TryCompile(string id, string? text, out Effect[] effects)
    {
        var definition = Definition(id); effects = definition?.Effects ?? [];
        return definition is { } d && d.Text == text?.Trim();
    }
}
