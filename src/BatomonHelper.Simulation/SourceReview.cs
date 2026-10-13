using System.Security.Cryptography;
using System.Text;
using BatomonHelper.Core;
namespace BatomonHelper.Simulation;
public sealed record ReviewedPage(string Url,string Role,DateTimeOffset RetrievedAt,string Sha256);
public sealed record SourceDecision(string Id,string Decision,string[] Sources);
public sealed record SourceReview(string Build,string Balance,ReviewedPage[] Pages,SourceDecision[] Decisions,string[] StillMissing);
public static class SourceReviewer
{
    public static async Task<SourceReview> FetchAsync(HttpClient http,CancellationToken token=default)
    {
        var targets=new (string Url,string Role)[]{
            ("https://www.batomon.wiki/mechanics","Navigation index; its links lead to the already imported community mechanic pages."),
            ("https://batomon.com/mechanics/monster-pool","Detailed shop rules; older assertions must be reconciled against current references."),
            ("https://batomon.com/whats-new","Developer patch notes republished by the wiki; current conflict precedence."),
            ("https://batomon.com/items/coupon","Current item effect."),
            ("https://batomon.com/trinkets/membership-card","Current trinket effect."),
            ("https://batomon.com/mechanics/evolution","Evolution timing; Pipskull threshold conflicts with the pinned creature tooltip."),
            ("https://batomon.com/batomon/pipskull","Pinned creature threshold takes precedence over the older narrative count."),
            ("https://batomon.com/mechanics/transformation","Persistent state preservation and Aerophim revival."),
            ("https://batomon.com/batomon/gachapod","Growing trinket ability; increment is not specified.")};
        var pages=new List<ReviewedPage>();var documents=new Dictionary<string,string>();
        foreach(var (url,role) in targets){var text=await http.GetStringAsync(url,token);documents[url]=text;pages.Add(new(url,role,DateTimeOffset.UtcNow,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));}
        var patches=documents["https://batomon.com/whats-new"];
        if(!patches.Contains("25738032",StringComparison.Ordinal) || !patches.Contains("Balance 25",StringComparison.Ordinal))throw new InvalidDataException("Reviewed source build changed; inspect before replacing provenance.");
        var patchText=WikiPayload.MainText(patches);
        if(!WikiPayload.MainText(documents["https://batomon.com/items/coupon"]).Contains(ItemRules.Definition("coupon")!.Value.Text,StringComparison.Ordinal) ||
           !WikiPayload.MainText(documents["https://batomon.com/trinkets/membership-card"]).Contains(ShopRules.Definition("trinkets","membership-card")!.Value.Text,StringComparison.Ordinal) ||
           !patchText.Contains("a level 4 Batomon resells at its level 3 price",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A source reconciliation anchor changed.");
        return new("25738032","25",pages.ToArray(),[
            new("mechanics-index","The supplemental index contains links to batomon.com rather than additional numeric mechanics tables.",["https://www.batomon.wiki/mechanics"]),
            new("coupon","Use a discount of 3, as the current item and patch notes agree. The shop guide's 5 is retained only as a recorded conflict.",["https://batomon.com/mechanics/monster-pool","https://batomon.com/whats-new","https://batomon.com/items/coupon"]),
            new("level-four-resale","Use the level-3 resale multiplier for level 4. The shop guide's old multiplier 12 is superseded by launch notes.",["https://batomon.com/mechanics/monster-pool","https://batomon.com/whats-new"]),
            new("membership-card","Implement one additional daily item allowance per copy, matching the current trinket. Do not use the old guide's reroll-after-purchase behavior.",["https://batomon.com/mechanics/monster-pool","https://batomon.com/trinkets/membership-card"]),
            new("common-bug-purchase","Consume Bargain Bin before Bug Catcher, following the 1.3.0 fix.",["https://batomon.com/whats-new"]),
            new("catalog-pool","Keep refreshed catalog membership; use a labelled foreign eligibility guess rather than the old 70-of-88 narrative count.",["https://batomon.com/mechanics/monster-pool"]),
            new("pipskull-threshold","User confirms two Knockouts, overriding the imported three-Knockout tooltip. Next-shop timing and the owned-Ratacomb restriction remain sourced separately.",["https://batomon.com/mechanics/evolution","https://batomon.com/batomon/pipskull","User correction, 2026-10-13"]),
            new("aerophim-transform","Persistent transformation preserves identity/state and revives knocked-out targets; Multicast is retained before changing species. Cooldown and queued-cast behavior are explicitly provisional.",["https://batomon.com/mechanics/transformation"]),
            new("gachapod-growth","Use a labelled increment of one extra Common trinket per cast; the source states improvement but not the increment.",["https://batomon.com/batomon/gachapod"]),
            new("user-timing-and-stacking","User confirms additive percentages, same-tick Shock applies only on later damage ticks, simultaneous defeat is a player win, and daily gold increases by 5 from starting 30.",["User correction, 2026-10-13"])],["HP gaps and later values are explicitly extrapolated; latest gold correction is 30 + 5 * (day - 1).","Event timing/tiers are user-provided; sampling weights are provisionally uniform.","Cooldown lattice and complete battle-start priority are labelled guesses.","Foreign eligibility, conditional item eligibility, rounding and remaining interaction assumptions are inspectable in the rules evidence table."]);
    }
}
