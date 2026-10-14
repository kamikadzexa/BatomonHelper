using BatomonHelper.Simulation;

namespace BatomonHelper.Tests;

public sealed class TrainingObservationTests
{
    [Fact]
    public void Training_view_has_the_same_policy_features_as_the_isolated_snapshot()
    {
        var rules=RuleCompiler.Fixture();var environment=new GameEnvironment(rules);environment.Reset(69,"fixture");
        var encoder=new PolicyEncoding(rules);

        Assert.Equal(encoder.Observation(environment.GetObservation(),3,64),
            encoder.Observation(environment.GetTrainingObservation(),3,64));
    }
}
