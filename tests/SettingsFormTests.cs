using WetFlow;
using Xunit;

namespace WetFlow.Tests;

// The SettingsForm UI itself is not unit-tested (per project convention), but the
// "Off" <-> "" mapping between the escalation-model dropdown and the stored setting
// is pure logic and is covered here.
public class SettingsFormEscalationMappingTests
{
    [Theory]
    [InlineData("Off", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("small", "small")]
    [InlineData("medium", "medium")]
    public void EscalationModelFromSelection_MapsOffToEmpty(string? selection, string expected)
        => Assert.Equal(expected, SettingsForm.EscalationModelFromSelection(selection));

    [Theory]
    [InlineData("", "Off")]
    [InlineData("small", "small")]
    [InlineData("medium", "medium")]
    public void SelectionFromEscalationModel_MapsEmptyToOff(string model, string expected)
        => Assert.Equal(expected, SettingsForm.SelectionFromEscalationModel(model));
}
