namespace AmigaSharp.Runtime.Libraries.Native;

public class ExecLibrary(Core core) : AbstractLibrary
{
    public override string Name => "exec.library";
    public override ushort Version => 40;

    /// <summary>The size of <c>struct ExecBase</c> in V40.</summary>
    public override ushort PositiveSize => 632;

    // library = OpenLibrary(libName, version)
    // D0                    A1       D0
    // struct Library *OpenLibrary(STRPTR, ULONG);
    [LibraryFunctionOffset(-0x228)]
    public void OpenLibrary()
    {
        var name = core.Memory.ReadCString(core.Cpu.A[1]);
        var library = core.OpenLibrary(name, core.Cpu.D[0]);
        core.Cpu.D[0] = library?.Base ?? 0;
    }

    // CloseLibrary(library)
    //              A1
    // void CloseLibrary(struct Library *);
    [LibraryFunctionOffset(-0x19e)]
    public void CloseLibrary()
    {
        // CloseLibrary accepts NULL since V36.
        if (core.Cpu.A[1] != 0)
            core.CloseLibrary(core.Cpu.A[1]);
    }
}
