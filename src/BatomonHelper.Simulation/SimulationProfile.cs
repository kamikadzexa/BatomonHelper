using System.Text.Json.Serialization;
namespace BatomonHelper.Simulation;

public enum EvidenceKind { UserProvided, Wiki, Guessed, Synthetic }
public sealed record ProgressionPoint(int Day,double Hp,double Income);
public sealed record ValueEvidence(int Day,double Value,[property:JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))] EvidenceKind Kind,string Explanation);
public sealed record RuleEvidence(string Id,[property:JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))] EvidenceKind Kind,string Value,string Explanation,string Source);
public sealed record EventSchedule(int Day,int Tier);
public sealed record EvidenceOverride(string Id,[property:JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))] EvidenceKind Kind,string Source,string Explanation);

/// <summary>Editable observations and explicitly provisional choices. Unknown handlers are still coverage failures.</summary>
public sealed record SimulationProfile(ProgressionPoint[] Observations,EventSchedule[] Events,int ScheduleDays=16,
    bool AllowGuesses=true,double CombatTickSeconds=.01,string PriceRounding="floor",bool SharedRarityOdds=true,
    bool FullyUnlocked=true,EvidenceOverride[]? EvidenceOverrides=null)
{
    public static SimulationProfile UserPantra() => new([
        new(1,300,30),new(2,500,35),new(3,800,40),new(4,1400,50),new(5,2400,55),
        new(7,5700,65),new(8,8300,70),new(9,11600,75),new(11,24900,85),
        new(13,52300,95),new(14,68000,100),new(15,87400,105)],
        [new(3,1),new(6,2),new(9,3)]);

    public (RunParameters Parameters,RuleEvidence[] Evidence) Compile()
    {
        var points=Observations.OrderBy(x=>x.Day).ToArray();
        if(points.Length<2 || points[0].Day!=1 || points.Select(x=>x.Day).Distinct().Count()!=points.Length ||
            points.Any(x=>x.Day<1 || !double.IsFinite(x.Hp)||x.Hp<=0 || !double.IsFinite(x.Income)||x.Income<0) ||
            ScheduleDays<points[^1].Day || CombatTickSeconds<=0 || CombatTickSeconds>.1 || !double.IsFinite(CombatTickSeconds) ||
            Math.Abs(.1/CombatTickSeconds-Math.Round(.1/CombatTickSeconds))>1e-9 || PriceRounding is not ("floor" or "ceil" or "fractional") ||
            Events.Any(x=>x.Day<2 || x.Tier is <1 or >3) || Events.Select(x=>x.Day).Distinct().Count()!=Events.Length)
            throw new InvalidDataException("Invalid simulation profile observations or guesses.");
        ValueEvidence[] Table(Func<ProgressionPoint,double> value,string name) => Enumerable.Range(1,ScheduleDays).Select(day=>
        {
            var known=points.FirstOrDefault(x=>x.Day==day);
            if(known is not null)return new ValueEvidence(day,value(known),EvidenceKind.UserProvided,"User-provided "+name+" observation.");
            var before=points.Last(x=>x.Day<day);var after=points.FirstOrDefault(x=>x.Day>day);
            if(after is not null)return new ValueEvidence(day,value(before)+(value(after)-value(before))*(day-before.Day)/(after.Day-before.Day),
                EvidenceKind.Guessed,$"Linear interpolation between days {before.Day} and {after.Day}.");
            var previous=points[^2];var slope=(value(before)-value(previous))/(before.Day-previous.Day);
            return new ValueEvidence(day,value(before)+slope*(day-before.Day),EvidenceKind.Guessed,$"Linear extrapolation of the last observed change (days {previous.Day}–{before.Day}).");
        }).ToArray();
        var hp=Table(x=>x.Hp,"base team HP");
        var income=Enumerable.Range(1,ScheduleDays).Select(day=>new ValueEvidence(day,points[0].Income+5*(day-1),EvidenceKind.UserProvided,
            "Latest user correction: base gold starts at 30 and increases by 5 each turn, superseding earlier income observations.")).ToArray();
        var parameters=new RunParameters(points[0].Income,income.Select(x=>x.Value).ToArray(),hp.Select(x=>x.Value).ToArray(),TurnCap:ScheduleDays,
            Verified:hp.All(x=>x.Kind!=EvidenceKind.Guessed) && income.All(x=>x.Kind!=EvidenceKind.Guessed),
            AllowGuesses:AllowGuesses,HpEvidence:hp,IncomeEvidence:income);
        var evidence=new List<RuleEvidence>{
            new("turn-cap",EvidenceKind.UserProvided,ScheduleDays.ToString(System.Globalization.CultureInfo.InvariantCulture),"Runs end when they reach the win target, lose all lives, or reach turn 16.","User message"),
            new("starting-gold",EvidenceKind.UserProvided,points[0].Income.ToString(System.Globalization.CultureInfo.InvariantCulture),"Starting gold is granted once; later daily values are added to unspent gold.","User message"),
            new("event-schedule",EvidenceKind.UserProvided,string.Join(", ",Events.Select(x=>$"day {x.Day}: tier {x.Tier}")),"Normal scheduled events are distinct from Second Chance.","User message"),
            new("shared-rarity-odds",EvidenceKind.UserProvided,SharedRarityOdds.ToString(),"Ordinary item and trinket rarity draws use the same final shop odds as creatures; explicit reward rarities stay explicit.","User message"),
            new("repeatable-rewards",EvidenceKind.UserProvided,"non-unique definitions may repeat","Unique owned/offered trinkets are excluded; non-unique rewards sample with replacement.","User message"),
            new("progression-gaps",EvidenceKind.Guessed,"interpolate missing HP days; extrapolate HP after last observation","User authorizes HP extrapolation. The exact HP curve remains provisional; income advances by the confirmed 5 gold per turn.","Authorized guess"),
            new("battle-tick",EvidenceKind.Guessed,CombatTickSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+" seconds","Use a fixed cooldown-check lattice; timed status and sudden-death intervals retain their documented values.","Authorized guess"),
            new("battle-start-order",EvidenceKind.Guessed,"player before opponent; inventory/trainer effects before creatures, slots 0–5","A deterministic ordering for unspecified start-effect interactions. Documented player-first queued casts are preserved.","Authorized guess"),
            new("event-weights",EvidenceKind.Guessed,"uniform within eligible tier","Fully unlocked Pantra excludes Oshima-only and endless events; special events use their own conditions.","Authorized guess"),
            new("foreign-pool",EvidenceKind.Guessed,"foreign regional base species, non-egg, non-event, rarity <= Legendary","Use catalog region/base-species metadata rather than stale narrative pool counts.","Authorized guess"),
            new("item-eligibility",EvidenceKind.UserProvided,"offer only items with an eligible current table or bench target","Targeted items are excluded when no qualifying creature is owned; targetless items remain eligible.","User message"),
            new("price-rounding",EvidenceKind.Guessed,PriceRounding,"Round percentage-discount prices using the configured policy.","Authorized guess"),
            new("normal-event-order",EvidenceKind.Guessed,"choose event and nested rewards before generating the day's shop","Do not expose unrolled shop randomness while choosing an event.","Authorized guess"),
            new("transform-pool",EvidenceKind.Guessed,"active regional pool, preserving identity, levels, shiny flag and persistent state","Foreign event grants admit the obtained species to later shops. A missing shiny form uses normal level data.","Authorized guess"),
            new("dynamic-stat-cycles",EvidenceKind.Guessed,"evaluate dependencies; cyclic paths fall back to values before the repeated dynamic stat","Each monster/stat pair can enter a dependency path once, bounding cyclic combinations without hiding an unsupported handler.","Authorized guess"),
            new("purchase-buff-order",EvidenceKind.Guessed,"pending bonuses and added types apply before the bought creature's own ability","Only matching paid purchases consume contingent effects; merging preserves them, selling the source cancels them.","Authorized guess"),
            new("revival-timing",EvidenceKind.Guessed,"preserve cooldown progress at knockout; cancel previously queued multicast casts","Revival resumes progress with new cooldown-speed bonuses; once-per-battle revivals cannot reset their consumed flag.","Authorized guess"),
            new("knockout-stat-transfer",EvidenceKind.Guessed,"copy Damage, Heal, Shield, Burn, Poison, Shock and Multicast, excluding Cooldown Speed and Sell Value","Stealing uses the victim's pre-knockout combat stats. Protect prevents both knockout and the associated gain.","Authorized guess"),
            new("evolution-progress",EvidenceKind.Guessed,"damage before shield absorption; burn counts positive cast applications; progress resets per form","Counters persist with the creature. Evolution resets cooldown progress, cancels queued multicast and preserves knockout state.","Authorized guess"),
            new("pipskull-evolution",EvidenceKind.UserProvided,"2 knockouts; evolve at the next shop unless Ratacomb is owned","The user confirms two knockouts, overriding the imported tooltip threshold.","User message"),
            new("hatch-distribution",EvidenceKind.Guessed,"uniform eligible regional/foreign creatures of the specified rarity and type","Count starts on the day after acquisition; hatching keeps creature identity and persistent bonuses.","Authorized guess"),
            new("gachapod-growth",EvidenceKind.Guessed,"one additional Common trinket per cast during the battle","The current description says improve this ability without specifying the increment; growth expires after battle.","https://batomon.com/batomon/gachapod"),
            new("badge-count",EvidenceKind.Guessed,"one badge per run win","Use the current run's wins for badge-scaled effects; hidden account rank does not enter observations.","Authorized guess"),
            new("ongoing-hp",EvidenceKind.Guessed,"inventory HP bonuses, then creature HP bonuses at battle initialization","Apply once before either side's start effects. Mid-battle disable/knockout does not subtract already-granted HP.","Authorized guess"),
            new("merge-counters",EvidenceKind.Guessed,"highest progress per counter; earliest acquisition day","Pending purchase bonuses transfer to the surviving identity. Runtime offer markers do not transfer.","Authorized guess"),
            new("forced-berry-items",EvidenceKind.Guessed,"consume queued credits on fresh item draws; eligible berry rarity falls back downward","Use imported IDs ending in -berry. Tickets and locked existing items do not spend credits.","Authorized guess"),
            new("combat-transform-timing",EvidenceKind.Guessed,"reset cooldown progress and cancel old multicast; retain identity, stats, types and counters","A revived transformed creature resumes with its new form; new start/purchase abilities wait for a later matching event.","Authorized guess"),
            new("unbounded-levels",EvidenceKind.Guessed,"after the last imported level, extrapolate base stats by the last difference; reuse its cooldown/effect parameters","Unlimited species stay in the shop pool and merge beyond level 3. Resale above level 3 uses the level-3 multiplier. Transformations preserve those levels.","Authorized guess"),
            new("ongoing-amplifiers",EvidenceKind.Guessed,"extra applications add; amplifiers do not recursively amplify other amplifiers","Apply to battle stat auras and passive event reactions. Unlabelled imported passive descriptions are classified as Ongoing.","Authorized guess"),
            new("flat-cooldown-order",EvidenceKind.Guessed,"flat cooldown increases apply after cooldown-speed division","Row effects cover the corresponding row on both boards, then the normal 0.1-second minimum applies.","Authorized guess"),
            new("timer-order",EvidenceKind.Guessed,"passive timers run before cooldown casts on a shared tick; pause while knocked out or disabled","Reset their progress on form/level changes. No accumulated catch-up pulses are emitted on revival.","Authorized guess"),
            new("percentage-stacking",EvidenceKind.UserProvided,"add percentage bonuses: two +20% effects give +40%","Ongoing percentage bonuses sum before multiplication.","User message"),
            new("simultaneous-packets",EvidenceKind.UserProvided,"same-tick packets use existing Shock; newly applied Shock takes effect on later ticks; simultaneous defeat is a player win","Damage, status and healing resolve as shared phases before checking defeat.","User message"),
            new("mythical-item-stacking",EvidenceKind.Guessed,"Faebloom's documented 10% chance adds per owned copy, capped at 100%","Shop and bench ownership count; explicit tickets and item suppression keep their normal behavior.","https://batomon.com/mechanics/monster-pool"),
            new("advanced-consumables",EvidenceKind.Guessed,"consume qualifying trinkets in inventory order; incense counts fresh normal shop rolls","Tickets do not spend incense rolls. Multiple gift modifiers stack; next-gift effects attach to the generated choice batch.","Authorized guess"),
            new("trainer-distributions",EvidenceKind.Guessed,"Swim Coach samples current-rank Water rarity; Painter assigns distinct random species to each type","Trainer choices offer three unowned abilities. Newly acquired trainers activate initial abilities once and eligible daily abilities immediately.","Authorized guess"),
            new("trainer-persistence",EvidenceKind.Guessed,"Chemist counts all visible level-ups; Twins clones only merges reaching level 3 with zero resale","Mad Scientist preserves permanent bonuses, identity and typing while resetting form level/copies. Egg Breeder's timer starts when acquired.","Authorized guess"),
            new("inventory-battle-order",EvidenceKind.Guessed,"inventory order resolves start effects; tied highest-cooldown targets choose the first slot","Alpha Crown retains the start-time damage difference. Zenith Stone amplifies flat gains excluding Speed and Multicast; incoming Heal/Shield percentage bonuses add.","Authorized guess"),
            new("devour-summons",EvidenceKind.Guessed,"devour at the next shop; summon temporary exact copies into empty bottom-row slots","Copies trigger only with their parent's casts, expire after battle and preserve stored creature state. Knocked-out occupied slots are not refilled; merges keep the target's payload when both have one.","Authorized guess")};
        foreach(var correction in EvidenceOverrides??[])
        {
            var index=evidence.FindIndex(x=>x.Id==correction.Id);
            if(index<0)throw new InvalidDataException("Unknown evidence override "+correction.Id);
            evidence[index]=evidence[index] with {Kind=correction.Kind,Source=correction.Source,Explanation=correction.Explanation};
        }
        return(parameters,evidence.ToArray());
    }
}
