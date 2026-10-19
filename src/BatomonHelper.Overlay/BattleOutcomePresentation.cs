using BatomonHelper.Core;

namespace BatomonHelper.Overlay;

public static class BattleOutcomePresentation
{
    public static string Summary(GameBattleOutcome? outcome) => outcome is null
        ? "Calculating fight result..." : outcome.PlayerWon ? "WIN" : "LOSS";
}
