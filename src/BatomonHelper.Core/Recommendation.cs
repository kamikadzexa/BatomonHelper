namespace BatomonHelper.Core;

public sealed record Recommendation(string Action, decimal Score, IReadOnlyList<string> Reasons);

public interface IRecommendationEngine
{
    IReadOnlyList<Recommendation> Evaluate(CatalogSnapshot catalog, ShopOddsSnapshot snapshot, OddsResult odds);
}
