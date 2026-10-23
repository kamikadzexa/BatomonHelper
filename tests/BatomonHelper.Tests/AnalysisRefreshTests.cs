using BatomonHelper.Core;
using BatomonHelper.Overlay;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class AnalysisRefreshTests
{
    [Fact]
    public void Heartbeats_and_setup_checks_preserve_analysis_but_option_changes_invalidate_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application();
                var model = new OverlayViewModel();
                try
                {
                    Field(model.Setup, "_settings").SetValue(model.Setup, new HelperSettings());
                    model.Setup.SetConnection("");
                    var row = new DamageRateRow("DPS", "1", "1", "1", "1", "1", "1", "");
                    model.DamageRows.Add(row);
                    model.OpponentDamageRows.Add(row);
                    var cancellation = new CancellationTokenSource();
                    Field(model, "_damageCancellation").SetValue(model, cancellation);
                    Field(model, "_damageIdentity").SetValue(model, "battle:unchanged");
                    Field(model, "_positionIdentity").SetValue(model, "unchanged");
                    for (var index = 0; index < 4; index++)
                    {
                        model.Setup.SetConnection("");
                        model.Setup.Refresh();
                        model.Setup.SetConnection("Temporary disconnect");
                    }
                    Assert.Same(row, Assert.Single(model.DamageRows));
                    Assert.Same(row, Assert.Single(model.OpponentDamageRows));
                    Assert.Equal("battle:unchanged", Field(model, "_damageIdentity").GetValue(model));
                    Assert.Equal("unchanged", Field(model, "_positionIdentity").GetValue(model));
                    Assert.Same(cancellation, Field(model, "_damageCancellation").GetValue(model));
                    Assert.False(cancellation.IsCancellationRequested);

                    // Playback samples and a committed outcome must retain the exact displayed rows.
                    var battleSide = new GameBattleSide(1, 100, "fixture", [], new([], []));
                    var start = new GameBattleState(battleSide, battleSide);
                    Field(model, "_damageIdentity").SetValue(model, "battle:" + JsonSerializer.Serialize(start));
                    var refreshBattle = typeof(OverlayViewModel).GetMethod("RefreshBattleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    foreach (var seconds in new[] { .5, 1, 2 })
                    {
                        var frameSide = new GameBattleFrameSide(90, 100, 0, 0, 0, 0, []);
                        var update = start with { Frame = new(seconds, frameSide, frameSide), Outcome = new(true) };
                        ((Task)refreshBattle.Invoke(model, [update])!).GetAwaiter().GetResult();
                        Assert.Same(row, Assert.Single(model.DamageRows));
                        Assert.Same(row, Assert.Single(model.OpponentDamageRows));
                        Assert.False(cancellation.IsCancellationRequested);
                    }

                    // Simulate a settings change without writing the user's settings.
                    var settings = (HelperSettings)Field(model.Setup, "_settings").GetValue(model.Setup)!;
                    Field(model.Setup, "_settings").SetValue(model.Setup, settings with { ShowDpsCounters = false });
                    model.Setup.SetConnection("");
                    Assert.Empty(model.DamageRows);
                    Assert.Empty(model.OpponentDamageRows);
                    Assert.Null(Field(model, "_damageIdentity").GetValue(model));
                    Assert.Null(Field(model, "_positionIdentity").GetValue(model));
                    var side = new GameBattleSide(1, 100, "fixture", [], new([], []));
                    var refresh = typeof(OverlayViewModel).GetMethod("RefreshBattleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    foreach (var hp in new[] { 100, 200 })
                    {
                        var battle = new GameBattleState(side, side with { TeamHp = hp });
                        ((Task)refresh.Invoke(model, [battle])!).GetAwaiter().GetResult();
                        Assert.Null(Field(model, "_damageCancellation").GetValue(model));
                    }
                    cancellation.Dispose();
                }
                finally { model.StopCaptures(); }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Analysis refresh regression test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
