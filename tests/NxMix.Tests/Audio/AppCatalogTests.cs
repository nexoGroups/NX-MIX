using NxMix.Audio;
using Xunit.Abstractions;

namespace NxMix.Tests.Audio;

[Collection("AudioDevices")]
public class AppCatalogTests
{
    private readonly ITestOutputHelper _out;

    public AppCatalogTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void List_DoesNotThrow_AndEntriesArePlausible()
    {
        IReadOnlyList<AppSessionInfo> list = new AppCatalog().List();
        Assert.NotNull(list);
        foreach (AppSessionInfo a in list)
        {
            _out.WriteLine($"{(a.IsPlaying ? "spielt " : "       ")} pid={a.ProcessId,6} {a.ExeName,-28} '{a.DisplayName}'  {a.ExePath}");
            Assert.True(a.ProcessId > 4);
            Assert.NotEqual(Environment.ProcessId, a.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(a.ExeName));
            Assert.False(string.IsNullOrWhiteSpace(a.DisplayName));
        }
        // ein Eintrag je Programm
        Assert.Equal(list.Count, list.Select(a => a.ExeName.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void List_WorksFromStaThread_AndRepeatedly()
    {
        IReadOnlyList<AppSessionInfo>? list = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { new AppCatalog().List(); list = new AppCatalog().List(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(60000));
        Assert.Null(error);
        Assert.NotNull(list);
    }
}
