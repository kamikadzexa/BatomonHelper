using BatomonHelper.Core;

namespace BatomonHelper.Tests;

public sealed class RunStateValidatorTests
{
    [Fact]
    public void Rejects_overflow_when_team_and_bench_are_not_full()
    {
        var validation = RunStateValidator.Validate(RunState.Empty with { IsOverflow = true }, CatalogSnapshot.Empty);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, x => x.Contains("Overflow", StringComparison.Ordinal));
    }
}
