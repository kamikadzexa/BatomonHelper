using BatomonHelper.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatomonHelper.Simulation;

/// <summary>Build-time compiler. Unknown prose is an audit failure, never a silent no-op.</summary>
public static class RuleCompiler
{
    /// <summary>Battle projections never buy items or execute shop-only trainer abilities.</summary>
    public static RulesManifest CompileCombat(CatalogSnapshot catalog, StrategyKnowledge knowledge)
    {
        var full = Compile(catalog, knowledge);
        var combat = full with {
            CombatOnly = true,
            Items = [],
            Issues = full.Issues.Where(issue => issue.Kind != "items" &&
                !(issue.Kind == "trainers" && (issue.Id, issue.Reason) is
                    ("witch", "The first shop rolled each day contains a Potion.") or
                    ("bug-catcher", "The first Bug monster you buy each day costs $10 less."))).ToArray()
        };
        return combat with { Hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(combat))) };
    }

    public static RulesManifest Compile(CatalogSnapshot catalog, StrategyKnowledge knowledge,SimulationProfile? profile=null)
    {
        var issues = new List<RuleIssue>();
        profile??=SimulationProfile.UserPantra();var settings=profile.Compile();
        var descriptions = new Dictionary<string, string>();
        foreach (var composition in knowledge.Compositions) Walk(composition.Data);
        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var id) && element.TryGetProperty("effect", out var text) && text.ValueKind == JsonValueKind.String)
                    descriptions[id.GetString()!] = text.GetString()!;
                foreach (var property in element.EnumerateObject()) Walk(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Walk(value);
        }
        if ((catalog.DeclaredSteamBuild, catalog.Balance) is not (("25738032", "25") or ("26078162", "26")))
            issues.Add(new("catalog-build", "Source", $"Bundled build {catalog.DeclaredSteamBuild}/Balance {catalog.Balance} is not a reviewed catalog target.", "https://batomon.com/wiki"));
        if(!profile.AllowGuesses && settings.Evidence.Any(x=>x.Kind==EvidenceKind.Guessed))
            issues.Add(new("guesses-disabled","Source","This profile contains inferred rules while guesses are disabled.","user-profile.json"));
        var encounters=EncounterRules.Compile(knowledge,issues);
        var species = catalog.Monsters.Where(m => m.Id != "voltalith").Select(m =>
        {
            var rules = m.Rules;
            if (rules is null) { issues.Add(new(m.Id, "Creature", "Missing rules.", "https://batomon.com/batomon/" + m.Id)); return null; }
            foreach (var field in rules.UnverifiedPatchFields) issues.Add(new(m.Id, "Variant", field, rules.SourceUrl));
            string? evolved = null; int? evolutionLevel = null;
            if (rules.Evolution is { } evolution)
            {
                using var document = JsonDocument.Parse(evolution);
                if (document.RootElement.TryGetProperty("targetId", out var target)) evolved = target.GetString()?.Replace('_', '-');
                if (document.RootElement.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number) evolutionLevel = level.GetInt32();
            }
            LevelDefinition[] Levels(bool shiny) => (shiny ? rules.ShinyLevels : rules.Levels).Select(level =>
            {
                var stats = new double[9]; stats[(int)Stat.Multicast] = level.Multicast ?? 1;
                foreach (var stat in level.Stats) if ((int)stat.Kind < 8) stats[(int)stat.Kind] = stat.Kind == VisibleStatKind.CooldownSpeed ? (stat.Value ?? 0) / 100d : stat.Value ?? 0;
                var ability = (shiny ? rules.ShinyAbilities : rules.Abilities).GetValueOrDefault(level.Level);
                var trigger = ParseTrigger(shiny ? rules.ShinyAbilityTrigger : rules.AbilityTrigger);
                var effects = CompileText(ability, trigger, out var understood);
                if(m.Id=="pipskull")effects=effects.Select(e=>e.ProgressCounter=="knockouts"?e with {Amount=2}:e).ToArray();
                if(effects.Any(x=>x.Operation is Operation.Evolve or Operation.ProgressEvolution) && evolved is null)
                {understood=false;effects=[];}
                if (!understood) issues.Add(new($"{m.Id}/{(shiny ? "shiny" : "normal")}/{level.Level}", "Ability", ability ?? "Unknown ability.", rules.SourceUrl));
                var origin=trigger==Trigger.None && effects.Length>0?Trigger.Ongoing:trigger;
                return new LevelDefinition(level.Level, (double?)level.Cooldown, stats, effects, ability, origin);
            }).ToArray();
            return new Species(m.Id, m.Name, (int)m.Rarity, Number(m.ReferenceValues.GetValueOrDefault("Cost")), rules.MergeGroup,
                rules.Types.ToArray(), m.ActiveSets.Contains("starter") && m.BaseSpeciesId is null && !m.Id.Contains("egg") && m.Id is not ("ignit" or "talonite" or "wishwash"),
                m.Regions.Contains("Pantra region"), evolved, evolutionLevel, Levels(false), Levels(true), rules.SourceUrl,
                !m.Regions.Contains("Pantra region") && m.Regions.Count>0 && m.BaseSpeciesId is null && !m.Id.Contains("egg") &&
                    (int)m.Rarity<5 && m.Id is not ("ignit" or "talonite" or "wishwash"));
        }).OfType<Species>().ToArray();
        InventoryDefinition Inventory(string id, string name, int rarity, bool unique, double cost, string? text, string category)
        {
            text ??= descriptions.GetValueOrDefault(id);
            var url = $"https://batomon.com/{category}/{id}";
            bool supported;
            Effect[] effects;
            if (category == "items") supported = ItemRules.TryCompile(id, text, out effects);
            else if(ShopRules.TryCompile(category,id,text,out effects)) supported=true;
            else if(category=="trinkets" && InventoryRules.TryCompile(id,text,out effects))supported=true;
            else if(AdvancedRules.TryCompile(category,id,text,out effects))supported=true;
            else effects = CompileText(text, category == "trinkets" || category == "trainers" ? Trigger.Ongoing : Trigger.Bought, out supported);
            if (!supported) issues.Add(new(id, category, text ?? "No complete effect text was imported.", url));
            return new(id, name, rarity, unique, cost, effects, url, text ?? "Missing effect description");
        }
        // Repair cached catalogs imported before labelled costs were parsed. Verified for Balance 25.
        var items = catalog.Items.Select(x => Inventory(x.Id, x.Name, (int)x.Rarity, false, x.Id=="gray-chip"?0:x.Price ?? 0, x.RuleText, "items")).ToArray();
        var trinkets = catalog.Trinkets.Select(x => Inventory(x.Id, x.Name, (int)x.Rarity, x.IsUnique, 0, x.RuleText, "trinkets")).ToArray();
        var trainers = catalog.Trainers.Where(x => x.Id != "mysterious-trainer").Select(x => Inventory(x.Id, x.Name, 0, false, 0, x.RuleText, "trainers")).ToArray();
        var manifest = new RulesManifest(catalog.DeclaredSteamBuild, catalog.Balance ?? "unknown", catalog.WikiVersion ?? "unknown", "",
            species, items, trinkets, trainers, issues.Distinct().ToArray(),settings.Parameters,
            Evidence:[..settings.Evidence,new("gray-chip-price",EvidenceKind.Wiki,"0","Cost is $0; $15 is the conditional victory payout. Repairs the earlier first-dollar-amount importer.","https://batomon.com/items/gray-chip")],Encounters:encounters,Profile:profile);
        return manifest with { Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)+"/encoding:"+PolicyEncoding.Version))) };
    }

    public static Trigger ParseTrigger(string? text) => text switch
    {
        "On Cast" => Trigger.Cast, "On Battle Start" => Trigger.BattleStart, "On Bought" => Trigger.Bought,
        "On Victory" => Trigger.Victory, "On Battle Lost" => Trigger.Defeat, "On Knocked Out" or "On Knockout" => Trigger.Knockout,
        "On Trinket Gained" => Trigger.TrinketGained, "Ongoing" => Trigger.Ongoing, _ => Trigger.None
    };
    private static double Number(string? text) => double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static Effect[] CompileText(string? text, Trigger trigger, out bool understood)
    {
        understood = true;
        if (string.IsNullOrWhiteSpace(text) || text == "Does nothing." || Regex.IsMatch(text, "^Evolves at level [1-4]\\.$")) return [];
        text = text.Trim();
        foreach (var prefix in new[] { "On Battle Start", "On Bought", "On Victory", "On Battle Lost", "On Trinket Gained" })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) { trigger = ParseTrigger(prefix); text = text[prefix.Length..].Trim(); break; }
        if (AbilityRules.TryCompile(text,trigger,out var compiled)) return compiled;
        if (EvolutionRules.TryCompile(text,trigger,out compiled)) return compiled;
        var permanent = text.Contains("permanently", StringComparison.OrdinalIgnoreCase) || trigger == Trigger.Bought;
        var target = text.Contains("adjacent", StringComparison.OrdinalIgnoreCase) ? Target.Adjacent
            : Regex.IsMatch(text, "\\babove\\b", RegexOptions.IgnoreCase) ? Target.Above
            : Regex.IsMatch(text, "\\bbelow\\b", RegexOptions.IgnoreCase) ? Target.Below
            : Regex.IsMatch(text, "\\bbehind\\b", RegexOptions.IgnoreCase) ? Target.Left
            : Regex.IsMatch(text, "\\bfront\\b", RegexOptions.IgnoreCase) ? Target.Right
            : text.Contains("allies") || text.StartsWith("Your monsters") || text.StartsWith("Give your") || text.StartsWith("Your team") ? Target.All : Target.Self;
        var type = Regex.Match(text, "\\b(Fire|Water|Grass|Rock|Bug|Flying|Electric|Toxic)\\b", RegexOptions.IgnoreCase).Value.ToLowerInvariant();
        string? filter = type.Length == 0 ? null : type;
        var gain = Regex.Match(text, "^(?:(?:This and )?(?:adjacent|Adjacent) (?:[A-Za-z]+ )?allies (?:gain|have) |Give (?:the |your )?(?:[A-Za-z]+ )?(?:ally (?:above|behind|in front)|monsters) |Your (?:[A-Za-z]+ )?monsters have |Your team gains )?(?<stats>[+-][\\d.]+%? (?:Damage|Heal|Shield|Burn|Poison|Shock|Cooldown Speed|Multicast|Sell Value)(?: and [+-][\\d.]+%? (?:Damage|Heal|Shield|Burn|Poison|Shock|Multicast))?)(?: permanently| for this battle| for the rest of the run)?\\.$");
        if (gain.Success)
        {
            return Regex.Matches(gain.Groups["stats"].Value, "([+-][\\d.]+)(%)? (Damage|Heal|Shield|Burn|Poison|Shock|Cooldown Speed|Multicast|Sell Value)").Select(m =>
            {
                var stat = m.Groups[3].Value switch { "Cooldown Speed" => Stat.Speed, "Sell Value" => Stat.SellValue, var v => Enum.Parse<Stat>(v) };
                var amount = Number(m.Groups[1].Value); var percent = m.Groups[2].Success;
                return new Effect(trigger, percent && stat != Stat.Speed ? Operation.Multiply : Operation.Add,
                    target, stat, percent ? amount / 100 : amount, permanent, filter);
            }).ToArray();
        }
        var gold = Regex.Match(text, "^Gain \\$(\\d+)\\.$");
        if (gold.Success) return [new(trigger, Operation.Gold, Target.Self, Amount: Number(gold.Groups[1].Value), Permanent: true)];
        var cleanse = Regex.Match(text, "^Remove (\\d+)(%)? (?:stacks of every debuff|of debuffs) on your team\\.$");
        if (cleanse.Success) return [new(trigger, Operation.Cleanse, Target.All, Amount: Number(cleanse.Groups[1].Value) / (cleanse.Groups[2].Success ? 100 : 1), Percent: cleanse.Groups[2].Success)];
        if (text == "Trigger this.") return [new(trigger, Operation.Trigger, Target.Self)];
        if (text == "Knockout self.") return [new(trigger, Operation.Knockout, Target.Self)];
        if (text == "Knockout the enemy opposite of this.") return [new(trigger, Operation.Knockout, Target.Opposite)];
        var triggerTarget = Regex.Match(text, "^Trigger (?:the )?(?:(Grass|Bug|Water|Rock) )?(ally above|adjacent [A-Za-z]+ allies)\\.(?: \\(Except other ([A-Za-z]+)\\))?$");
        if (triggerTarget.Success) return [new(trigger, Operation.Trigger, target, Type: filter, ExcludeSpecies: triggerTarget.Groups[3].Success ? triggerTarget.Groups[3].Value.ToLowerInvariant() : null)];
        var protect = Regex.Match(text, "^Give adjacent allies Protect (\\d+)\\.$");
        if (protect.Success) return [new(trigger, Operation.Protect, Target.Adjacent, Amount: Number(protect.Groups[1].Value))];
        var hp = Regex.Match(text, "^\\+(\\d+)(%)? HP$");
        if (hp.Success) return [new(trigger, Operation.TeamHp, Target.All, Amount: Number(hp.Groups[1].Value) / (hp.Groups[2].Success ? 100 : 1), Percent: hp.Groups[2].Success)];
        understood = false;
        return [];
    }

    public static RulesManifest Fixture()
    {
        var a = new double[9]; a[0] = 25; a[7] = 1;
        var b = new double[9]; b[2] = 10; b[7] = 1;
        var fixture = new RulesManifest("fixture", "fixture", "fixture", "fixture-v9", [
            new("striker", "Fixture striker", 0, 10, "striker", ["fire"], true, true, null, null,
                Enumerable.Range(1,4).Select(l => new LevelDefinition(l, 2, a.Select(x => x*l).ToArray(), [])).ToArray(), [], "fixture"),
            new("guard", "Fixture guard", 0, 10, "guard", ["rock"], true, true, null, null,
                Enumerable.Range(1,4).Select(l => new LevelDefinition(l, 3, b.Select(x => x*l).ToArray(), [])).ToArray(), [], "fixture")],
            [], [], [new("fixture", "Fixture trainer", 0, false, 0, [], "fixture", "Diagnostic fixture, not game rules")], [],
            new(30, Enumerable.Repeat(30d,32).ToArray(), Enumerable.Range(1,32).Select(d => 100d + d*20).ToArray(), Verified: true));
        return fixture with { Species=fixture.Species.Concat(Enumerable.Range(0,18).Select(i=>fixture.Species[i%2] with
            {Id="fixture-"+i,Name="Fixture unit "+i,MergeGroup="fixture-"+i})).ToArray() };
    }
}
