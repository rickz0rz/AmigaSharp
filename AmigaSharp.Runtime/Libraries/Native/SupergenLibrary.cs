namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// supergen.library: the control library of the SuperGen genlock of Digital Creations. Sneak Prevue opens it, and
/// stops when it does not exist.
/// </summary>
/// <remarks>
/// The functions of the library are not documented here. Each function writes its registers to the log, and returns 0.
/// </remarks>
public class SupergenLibrary(Core core) : AbstractLibrary
{
    public override string Name => "supergen.library";
    public override ushort Version => 1;
    public override short LowestOffset => -60;

    private uint Call(int offset)
    {
        var cpu = core.Cpu;
        core.Log.WriteLine($"supergen.library {offset}: D0=${cpu.D[0]:X} D1=${cpu.D[1]:X} A0=${cpu.A[0]:X} A1=${cpu.A[1]:X}");
        return 0;
    }

    [LibraryFunctionOffset(-30)] public uint Function30() => Call(-30);
    [LibraryFunctionOffset(-36)] public uint Function36() => Call(-36);
    [LibraryFunctionOffset(-42)] public uint Function42() => Call(-42);
    [LibraryFunctionOffset(-48)] public uint Function48() => Call(-48);
    [LibraryFunctionOffset(-54)] public uint Function54() => Call(-54);
    [LibraryFunctionOffset(-60)] public uint Function60() => Call(-60);
}
