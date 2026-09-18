using M68KASMtoCSharp.AmigaCore;
using M68KASMtoCSharp.AmigaCore.CPU;

namespace M68KASMtoCSharp.Libraries.Native;

public class DosLibrary(Amiga amiga) : AbstractLibrary(amiga)
{
    public override string Name()
    {
        return "dos.library";
    }

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
        var result = -1;

        try
        {
            var d1 = amiga.Cpu.GetDataRegister(DataRegister.D1);
            var stringValue = Tools.ReadStringUntilNull(amiga, d1);
            Console.WriteLine(stringValue);
            result = 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }

        amiga.Cpu.SetDataRegister(DataRegister.D0, result);
    }
}
