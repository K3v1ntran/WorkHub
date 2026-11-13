using WorkHub.Core.Settings;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Phase 16A placement policy (proposal §4.4, acceptance A1–A12) and start-tab matching (L10). Pure: work areas are DIP
/// rectangles of the virtual screen; "primary" is (0, 0)–(1920, 1040).
/// </summary>
public sealed class WindowPlacementPolicyTests
{
    private static readonly PlacementRect Primary = new(0, 0, 1920, 1040);

    private static WindowPlacementSettings Saved(double? left, double? top, double width = 1200, double height = 800, bool maximized = false) =>
        new() { Left = left, Top = top, Width = width, Height = height, IsMaximized = maximized };

    private static PlacementDecision Decide(WindowPlacementSettings saved, params PlacementRect[] areas) =>
        WindowPlacementPolicy.Decide(saved, areas.Length == 0 ? [Primary] : areas);

    private static void AssertDefaults(PlacementDecision decision)
    {
        Assert.False(decision.UseSaved);
        Assert.False(decision.Maximize);
        Assert.Equal(1100, decision.Bounds.Width);
        Assert.Equal(720, decision.Bounds.Height);
    }

    [Fact]
    public void A1_InsideThePrimary_IsUsedAsSaved()
    {
        var decision = Decide(Saved(100, 100));

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(100, 100, 1200, 800), decision.Bounds);
    }

    [Fact]
    public void A2_AMonitorLeftOfThePrimary_NegativeLeftIsAccepted()
    {
        var left = new PlacementRect(-1920, 0, 1920, 1040);

        var decision = Decide(Saved(-1800, 100), Primary, left);

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(-1800, 100, 1200, 800), decision.Bounds);
    }

    [Fact]
    public void A3_AMonitorAboveThePrimary_NegativeTopIsAccepted()
    {
        var above = new PlacementRect(0, -1080, 1920, 1040); // (0, -1080)–(1920, -40)

        var decision = Decide(Saved(200, -1000), Primary, above);

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(200, -1000, 1200, 800), decision.Bounds);
    }

    [Fact]
    public void A4a_ZeroLeftAndTop_AreValid()
    {
        var decision = Decide(Saved(0, 0));

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(0, 0, 1200, 800), decision.Bounds);
    }

    public static TheoryData<double> BadCoordinates() => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 100_000.5, -100_001 };

    [Theory]
    [MemberData(nameof(BadCoordinates))]
    public void A4b_AnInvalidLeftOrTop_GivesDefaults(double bad)
    {
        AssertDefaults(Decide(Saved(bad, 100)));
        AssertDefaults(Decide(Saved(100, bad)));
    }

    public static TheoryData<double, double> BadSizes() => new()
    {
        { double.NaN, 800 }, { double.PositiveInfinity, 800 }, { double.NegativeInfinity, 800 }, { 0, 800 }, { -5, 800 }, { 719, 800 }, { 32_001, 800 },
        { 1200, double.NaN }, { 1200, double.PositiveInfinity }, { 1200, double.NegativeInfinity }, { 1200, 0 }, { 1200, -5 }, { 1200, 459 }, { 1200, 32_001 },
    };

    [Theory]
    [MemberData(nameof(BadSizes))]
    public void A4c_AnInvalidWidthOrHeight_GivesDefaults(double width, double height) =>
        AssertDefaults(Decide(Saved(100, 100, width, height)));

    [Fact]
    public void A4c_TheMinimumSize_IsValid()
    {
        var decision = Decide(Saved(100, 100, 720, 460));

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(100, 100, 720, 460), decision.Bounds);
    }

    [Fact]
    public void A4d_NeverSaved_GivesDefaults()
    {
        AssertDefaults(Decide(Saved(null, null)));
        AssertDefaults(Decide(Saved(100, null)));
        AssertDefaults(WindowPlacementPolicy.Decide(null, [Primary]));
    }

    [Fact]
    public void A5_TheSavedMonitorIsNoLongerConnected_GivesDefaults() =>
        AssertDefaults(Decide(Saved(2000, 100, maximized: true), Primary)); // was on (1920, 0)–(3840, 1040)

    [Fact]
    public void A6_OnlyAHundredDipOfTheStripIsVisible_GivesDefaults() =>
        AssertDefaults(Decide(Saved(-1100, 100))); // 1200 wide: only 100 DIP on the primary, the rest off every monitor

    [Fact]
    public void A7_Exactly120By32DipOfTheStripVisible_IsUsed_EvenWithTheBodyOffScreen()
    {
        var decision = Decide(Saved(-1080, 1040 - 32)); // 120 DIP wide, 32 DIP tall inside; the body hangs below and left

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(-1080, 1008, 1200, 800), decision.Bounds);
    }

    [Fact]
    public void A8_OnlyTwentyDipOfTheStripHeightIsInside_GivesDefaults() =>
        AssertDefaults(Decide(Saved(100, -12))); // nothing above the primary: 12 of the 32 DIP are off-screen

    [Fact]
    public void A9_AStripSplitSixtyAndSixtyAcrossTwoMonitors_GivesDefaults_PerMonitorNotSummed()
    {
        // The strip (940…1660) has 60 DIP on each work area and 600 DIP over the space between them: 120 in total.
        var a = new PlacementRect(0, 0, 1000, 1040);
        var b = new PlacementRect(1600, 0, 1000, 1040);

        AssertDefaults(Decide(Saved(940, 100, 720), a, b));
        Assert.True(Decide(Saved(880, 100, 720), a, b).UseSaved); // control: 120 DIP on one monitor passes
    }

    [Fact]
    public void A10_LargerThanItsMonitor_IsShrunkToTheWorkAreaAndMovedInside()
    {
        var decision = Decide(Saved(50, 0, 3000, 2000));

        Assert.True(decision.UseSaved);
        Assert.Equal(new PlacementRect(0, 0, 1920, 1040), decision.Bounds);
    }

    [Fact]
    public void A11_MaximizedWithValidBounds_OnTheLeftMonitor_AppliesTheBoundsThenMaximizes()
    {
        var left = new PlacementRect(-1920, 0, 1920, 1040);

        var decision = Decide(Saved(-1800, 100, maximized: true), Primary, left);

        Assert.True(decision.UseSaved);
        Assert.True(decision.Maximize);
        Assert.Equal(new PlacementRect(-1800, 100, 1200, 800), decision.Bounds);
    }

    [Fact]
    public void A11b_MaximizedWithInvalidBounds_GivesDefaults_NotMaximized() =>
        AssertDefaults(Decide(Saved(double.NaN, 100, maximized: true)));

    [Fact]
    public void A12_DeviceWorkAreaWithA150PercentTransform_IsConvertedToDips()
    {
        var dip = PlacementRect.FromDevice(-2880, 0, 0, 1560, 1 / 1.5, 1 / 1.5);

        Assert.Equal(-1920, dip.Left, 6);
        Assert.Equal(0, dip.Top, 6);
        Assert.Equal(1920, dip.Width, 6);
        Assert.Equal(1040, dip.Height, 6);
    }

    [Theory]
    [InlineData("Workspace", true)]
    [InlineData("workspace", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Owner", false)]
    [InlineData("Repository", false)]
    public void L10_OnlyTheExactValueWorkspace_OpensWorkspace(string? saved, bool opensWorkspace) =>
        Assert.Equal(opensWorkspace, UiSettings.OpensWorkspace(saved));
}
