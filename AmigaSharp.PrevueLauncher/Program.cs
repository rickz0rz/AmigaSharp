using AmigaSharp.Host;
using AmigaSharp.PrevueLauncher;

// The launcher with the parts for Prevue Guide (ESQ). It runs other AmigaOS programs too, as AmigaSharp.Launcher does.
return Launcher.Run(args, new LauncherApp("AmigaSharp.PrevueLauncher",
    "Runs Prevue Guide (ESQ), the program of the Prevue channel, or another AmigaOS executable. The picture of the\n" +
    "display shows in a window.",
    [new PrevueExtension()]));
