namespace BatomonHelper.Capture;

public enum CaptureStep { Baseline, Reroll, Buy, Lock, NextDay, Resolve, RestartValidation }
public sealed record CaptureGuideState(CaptureStep Step, string GameInstruction, string PrimaryAction, string Progress);

public static class CaptureGuide
{
    public static CaptureGuideState For(CaptureStep step) => step switch
    {
        CaptureStep.Baseline => new(step, "Keep the Batomon shop visible. Click Read game screen, review the fields, then save the baseline.", "Save baseline", "1 / 7 — Baseline"),
        CaptureStep.Reroll => new(step, "In Batomon, use one normal reroll. Do not buy, lock, or advance the day. Then read the game screen.", "Apply reroll filter", "2 / 7 — Reroll"),
        CaptureStep.Buy => new(step, "In Batomon, buy exactly one shop offer. Then read the game screen.", "Apply buy filter", "3 / 7 — Buy"),
        CaptureStep.Lock => new(step, "In Batomon, toggle the shop lock exactly once. Then read the game screen.", "Apply lock filter", "4 / 7 — Lock"),
        CaptureStep.NextDay => new(step, "In Batomon, advance to the next day once. Then read the game screen.", "Apply next-day filter", "5 / 7 — Next day"),
        CaptureStep.Resolve => new(step, "Repeat the requested controlled action until a field has one candidate. Select that field and find module paths.", "Find module paths", "6 / 7 — Resolve"),
        _ => new(step, "Restart Batomon, load a new shop run, then repeat a baseline and one reroll to validate the path.", "Restart validation", "7 / 7 — Restart validation")
    };

    public static CaptureStep Next(CaptureStep step) => step switch
    {
        CaptureStep.Baseline => CaptureStep.Reroll, CaptureStep.Reroll => CaptureStep.Buy,
        CaptureStep.Buy => CaptureStep.Lock, CaptureStep.Lock => CaptureStep.NextDay,
        CaptureStep.NextDay => CaptureStep.Resolve, CaptureStep.Resolve => CaptureStep.RestartValidation,
        _ => CaptureStep.RestartValidation
    };
}
