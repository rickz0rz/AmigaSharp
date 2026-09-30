using AmigaSharp.Host;

// The launcher for any AmigaOS program. AmigaSharp.PrevueLauncher is the same launcher with the parts for Prevue.
return Launcher.Run(args, new LauncherApp("AmigaSharp.Launcher",
    "Translates an AmigaOS executable, compiles it and runs it. The picture of the display shows in a window.", []));
