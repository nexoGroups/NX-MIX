using NxMix.Audio.Apps;
using NxMix.Audio.Interop;

namespace NxMix.Tests.Audio;

/// <summary>Prozess-Wahl: Gruppierung nach Elternprozess mit einer Fake-Prozessliste (keine echten Prozesse).</summary>
public class AppProcessSelectorTests
{
    private static ProcInfo P(int pid, int parent, string exe, DateTime? start = null) => new(pid, parent, exe, start);

    [Fact]
    public void Browser_MainProcessWithChildren_IsOneGroup_RootIsParent()
    {
        var list = new[]
        {
            P(4, 0, "System"), P(50, 4, "explorer.exe"),
            P(100, 50, "chrome.exe"), P(101, 100, "chrome.exe"), P(102, 100, "chrome.exe"), P(103, 101, "chrome.exe"),
            P(300, 50, "notepad.exe"),
        };
        var groups = ProcessSelector.FindGroups(list, "chrome.exe");
        var g = Assert.Single(groups);
        Assert.Equal(100, g.RootPid);
        Assert.Equal(new[] { 100, 101, 102, 103 }, g.Members.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void TwoIndependentInstances_AreTwoGroups_OrderedByRootPid()
    {
        var list = new[]
        {
            P(200, 60, "chrome.exe"), P(201, 200, "chrome.exe"),
            P(100, 50, "chrome.exe"), P(101, 100, "chrome.exe"),
        };
        var groups = ProcessSelector.FindGroups(list, "chrome.exe");
        Assert.Equal(new[] { 100, 200 }, groups.Select(g => g.RootPid).ToArray());
        Assert.Equal(new[] { 100, 101 }, groups[0].Members.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 200, 201 }, groups[1].Members.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ChildOfOtherExe_IsNotMerged_AndNoDoubleCapture()
    {
        // Ein "chrome.exe" unter einem Launcher: der Launcher ist eine andere Exe -> das Kind ist selbst Wurzel
        var list = new[] { P(10, 4, "launcher.exe"), P(11, 10, "chrome.exe"), P(12, 11, "chrome.exe") };
        var groups = ProcessSelector.FindGroups(list, "chrome.exe");
        var g = Assert.Single(groups);
        Assert.Equal(11, g.RootPid);
        // Jeder Prozess steckt in genau einer Gruppe
        Assert.Equal(new[] { 11, 12 }, g.Members.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ReusedParentPid_WithNewerStartTime_StartsNewGroup()
    {
        DateTime t0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var list = new[]
        {
            // PID 100 ist heute ein NEUER chrome.exe (Start nach dem Kind 101): 101 gehoert nicht zu ihm
            P(100, 50, "chrome.exe", t0.AddHours(2)),
            P(101, 100, "chrome.exe", t0),
        };
        var groups = ProcessSelector.FindGroups(list, "chrome.exe");
        Assert.Equal(new[] { 100, 101 }, groups.Select(g => g.RootPid).ToArray());
    }

    [Fact]
    public void ParentOlderThanChild_StaysOneGroup()
    {
        DateTime t0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var list = new[] { P(100, 50, "chrome.exe", t0), P(101, 100, "chrome.exe", t0.AddSeconds(3)) };
        var g = Assert.Single(ProcessSelector.FindGroups(list, "chrome.exe"));
        Assert.Equal(100, g.RootPid);
    }

    [Theory]
    [InlineData("CHROME.EXE")]
    [InlineData("chrome")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("Chrome.exe")]
    public void Matching_IgnoresCaseExtensionAndPath(string wanted)
    {
        var list = new[] { P(100, 50, "chrome.exe"), P(5, 4, "chromedriver.exe") };
        var g = Assert.Single(ProcessSelector.FindGroups(list, wanted));
        Assert.Equal(100, g.RootPid);
    }

    [Fact]
    public void NoMatch_EmptyName_AndGarbage_ReturnEmpty()
    {
        var list = new[] { P(100, 50, "chrome.exe") };
        Assert.Empty(ProcessSelector.FindGroups(list, "spotify.exe"));
        Assert.Empty(ProcessSelector.FindGroups(list, ""));
        Assert.Empty(ProcessSelector.FindGroups(Array.Empty<ProcInfo>(), "chrome.exe"));
    }

    [Fact]
    public void ParentCycle_DoesNotHang()
    {
        var list = new[] { P(1, 2, "a.exe"), P(2, 1, "a.exe"), P(3, 3, "a.exe") };
        var groups = ProcessSelector.FindGroups(list, "a.exe");
        Assert.Equal(3, groups.Sum(g => g.Members.Count)); // jeder genau einmal
    }

    [Fact]
    public void EveryProcess_BelongsToExactlyOneGroup()
    {
        var rnd = new Random(42);
        var list = new List<ProcInfo>();
        for (int pid = 10; pid < 80; pid++)
            list.Add(P(pid, rnd.Next(5, 80), rnd.Next(3) == 0 ? "other.exe" : "app.exe"));
        var groups = ProcessSelector.FindGroups(list, "app.exe");
        int expected = list.Count(p => p.ExeName == "app.exe");
        Assert.Equal(expected, groups.Sum(g => g.Members.Count));
        Assert.Equal(expected, groups.SelectMany(g => g.Members).Distinct().Count());
    }

    [Fact]
    public void SystemProcessLister_ListsOwnProcess_WithParent()
    {
        var all = new SystemProcessLister().Snapshot();
        Assert.NotEmpty(all);
        ProcInfo me = Assert.Single(all, p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(me.ExeName));
        Assert.True(me.ParentPid > 0);
        Assert.NotNull(SystemProcessLister.StartTimeOf(Environment.ProcessId));
        Assert.False(string.IsNullOrEmpty(SystemProcessLister.ImagePathOf(Environment.ProcessId)));
    }
}
