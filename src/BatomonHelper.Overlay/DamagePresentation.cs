using BatomonHelper.Simulation;

namespace BatomonHelper.Overlay;

public sealed record DamageRateRow(string Metric,string Second0,string Second5,string Second10,string Second15,string Second20,string Second30,string Description);

public static class DamagePresentation
{
    public static string Summary(BoardAnalysisResult result)
    {
        var timing = result.MedianKillSeconds is {} seconds ? $"Estimated win at {seconds:0.0}s ({seconds / 30:0%})" : "Target not defeated within 30s";
        return $"Next opponent HP (est.): {result.TargetHp:N0}  ·  {timing}";
    }

    public static DamageRateRow[] Rows(BoardAnalysisResult result)
    {
        var rows = new List<DamageRateRow>();
        Add("DPS", point=>point.Dps, "All damage combined, including burn, poison and electricity.");
        foreach(var (kind,label) in new[]{("Burn","Burn /s"),("Poison","Poison /s"),
            ("Shock","Electricity /s"),("Heal","Healing /s"),("Shield","Shield /s")})
        {
            if(!result.Curve.Any(point=>point.OutputPerSecond.GetValueOrDefault(kind)>0))continue;
            Add(label,point=>point.OutputPerSecond.GetValueOrDefault(kind),kind=="Heal"
                ? "Healing your team can produce, including healing that would be wasted at full HP."
                : kind=="Burn" ? "New burn applied; the burn already on the opponent also wears off over time."
                : "New amounts produced, rather than damage dealt by existing stacks.");
        }
        return rows.ToArray();

        void Add(string label,Func<DamagePoint,double> value,string description)
        {
            var values=result.Checkpoints.Select(point=>FormatRate(value(point))).ToArray();
            rows.Add(new(label,values[0],values[1],values[2],values[3],values[4],values[5],description));
        }
    }

    public static string FormatRate(double value) => value==0 || Math.Abs(value)>=1 ? value.ToString("N1") :
        Math.Abs(value)>=.001 ? value.ToString("0.###") : value.ToString("0.###E+0");
}
