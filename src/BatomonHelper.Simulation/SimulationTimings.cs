using System.Diagnostics;

namespace BatomonHelper.Simulation;

public sealed class SimulationTimings
{
    public double LegalActionsMilliseconds {get;private set;}
    public double StateCopyMilliseconds {get;private set;}
    public double CombatMilliseconds {get;private set;}
    public Scope Legal()=>new(this,0);
    public Scope Copy()=>new(this,1);
    public Scope Combat()=>new(this,2);
    public void Clear(){LegalActionsMilliseconds=StateCopyMilliseconds=CombatMilliseconds=0;}
    public readonly struct Scope(SimulationTimings owner,int stage):IDisposable
    {
        private readonly long _start=Stopwatch.GetTimestamp();
        public void Dispose(){var ms=Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if(stage==0)owner.LegalActionsMilliseconds+=ms;else if(stage==1)owner.StateCopyMilliseconds+=ms;else owner.CombatMilliseconds+=ms;}
    }
}
