namespace AmigaSharp.Runtime.Libraries.Native;

public class DosLibrary(Core core) : AbstractLibrary
{
    public override string Name => "dos.library";
    public override ushort Version => 40;

    // https://d0.se/autodocs/dos.library/PutStr
    //
    // error = PutStr(str)
    // D0             D1
    // LONG PutStr(STRPTR)
    //
    // INPUTS
    // str   - Null-terminated string to be written to default output
    //
    // RESULT
    // error - 0 for success, -1 for any error.  NOTE: this is opposite
    // most Dos function returns!
    [LibraryFunctionOffset(-0x3b4)]
    public void PutStr()
    {
        var bytes = core.Memory.ReadCStringBytes(core.Cpu.D[1]);
        try
        {
            core.Output.Write(bytes);
            core.Output.Flush();
            core.Cpu.D[0] = 0;
        }
        catch (IOException)
        {
            core.Cpu.D[0] = unchecked((uint)-1);
        }
    }
}
