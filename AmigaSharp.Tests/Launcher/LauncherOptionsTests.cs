using AmigaSharp.Host;
using AmigaSharp.PrevueLauncher;

namespace AmigaSharp.Tests.Launcher;

public sealed class LauncherOptionsTests : IDisposable
{
    private readonly string _drive = Directory.CreateTempSubdirectory("AmigaSharp-drive-").FullName;

    public void Dispose() => Directory.Delete(_drive, recursive: true);

    [Fact]
    public void MachineOptions_MakeAPalAgaAmigaWithAFasterCpu()
    {
        var options = LauncherOptions.Parse(
            ["program", "--pal", "--chipset", "AGA", "--unaligned-access", "--cpu-mhz", "56"], []);

        Assert.True(options.Pal && options.Aga && options.UnalignedAccess);
        Assert.Equal(56, options.CpuMhz);
        Assert.Throws<ArgumentException>(() => LauncherOptions.Parse(["program", "--chipset", "ocs"], []));
    }

    [Fact]
    public void GenericLauncher_RefusesAnOptionForPrevue()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LauncherOptions.Parse(["program", "--prevue-ctrl-port", "8092"], []));

        Assert.Contains("--prevue-ctrl-port", error.Message);
    }

    [Fact]
    public void Extension_ReadsItsOptions_AndTheLauncherReadsTheOthers()
    {
        var options = LauncherOptions.Parse(
            ["program", "--prevue-ctrl-port", "8092", "--scale", "2"], [new PrevueExtension()]);

        Assert.Equal("program", options.ExecutablePath);
        Assert.Equal(2, options.Scale);
    }

    [Fact]
    public void PrevueDefaults_ComeFromTheDrive()
    {
        File.WriteAllBytes(Path.Combine(_drive, "ESQ"), []);
        var extension = new PrevueExtension();
        var options = LauncherOptions.Parse(["--drive", _drive], [extension]);

        extension.Complete(options);
        options.Check();

        Assert.Equal(Path.Combine(_drive, "ESQ"), options.ExecutablePath);
        Assert.Equal([("DH1", _drive)], options.Volumes);
        Assert.Equal([("DF0", "DH1:"), ("ENV", "DH1:")], options.Assigns);
        Assert.Equal("esq", options.CommandName);
        Assert.Equal(PrevueExtension.DefaultCode, options.Arguments);
        Assert.Equal(8, options.TurboSeconds);
    }

    [Fact]
    public void PrevueDefaults_DoNotChangeTheOptionsOfTheCommandLine()
    {
        var extension = new PrevueExtension();
        var options = LauncherOptions.Parse(
        [
            "ESQ", "--drive", _drive, "--assign", "DF0=SYS:", "--arguments", "XY12345", "--listing", "ESQ.lst",
            "--fast-cpu",
        ], [extension]);

        extension.Complete(options);

        Assert.Equal("ESQ", options.ExecutablePath);
        Assert.Equal([("DF0", "SYS:"), ("ENV", "DH1:")], options.Assigns);
        Assert.Equal("XY12345", options.Arguments);
        Assert.Null(options.TurboLabel);
        Assert.Null(options.TurboSeconds);
    }

    [Fact]
    public void PrevueFeedTrace_NeedsAListing()
    {
        var extension = new PrevueExtension();
        var options = LauncherOptions.Parse(["ESQ", "--prevue-feed-trace", "feed.log"], [extension]);

        Assert.Throws<ArgumentException>(() => extension.Complete(options));
    }
}
