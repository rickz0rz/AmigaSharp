using M68KASMtoCSharp.AmigaCore;
using M68KASMtoCSharp.AmigaCore.CPU;

namespace M68KASMtoCSharp.Libraries.Native;

public class ExecLibrary(Amiga amiga) : AbstractLibrary(amiga)
{
    public override string Name()
    {
        return "exec.library";
    }

    public override int? InitialOffset()
    {
        return 4;
    }

    // library = OpenLibrary(libName, version)
    // D0		      A1       D0
    // struct Library *OpenLibrary(STRPTR, ULONG);
    [LibraryFunctionOffset(-0x228)]
    public void OpenLibrary()
    {
        var result = 0;

        try
        {
            // Console.WriteLine("Opening library...");
            var libraryName = Tools.ReadStringUntilNull(amiga, amiga.Cpu.GetAddressRegister(AddressRegister.A1));
            // Console.WriteLine("Library name: {0}", libraryName);

            AbstractLibrary abstractLibrary = null;

            // maybe load the library name from abstractlibraries implementations Name()
            switch (libraryName)
            {
                case "dos.library":
                    abstractLibrary = new DosLibrary(amiga);
                    break;
                default:
                    throw new Exception("Unknown library name: " + libraryName);
            }

            result = amiga.OpenLibrary(abstractLibrary);

            // Console.WriteLine("Library opened: {0}", result);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        amiga.Cpu.SetDataRegister(DataRegister.D0, result);
    }

    // CloseLibrary(library)
    // 	     A1
    // void CloseLibrary(struct Library *);
    [LibraryFunctionOffset(-0x19e)]
    public void CloseLibrary()
    {
        try
        {
            // Console.WriteLine("Closing library...");
            var libraryAddress = amiga.Cpu.GetAddressRegister(AddressRegister.A1);
            var library = amiga.Libraries[libraryAddress];
            // Console.WriteLine("Library name: {0}", library.Name());
            amiga.CloseLibrary(libraryAddress);
            // Console.WriteLine("Library closed.");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}
