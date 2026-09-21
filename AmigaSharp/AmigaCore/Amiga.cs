using AmigaSharp.AmigaCore.CPU;
using AmigaSharp.AmigaCore.Libraries;
using AmigaSharp.AmigaCore.Libraries.Native;

namespace AmigaSharp.AmigaCore;

public class Amiga
{
    public Dictionary<int, AbstractLibrary> Libraries { get; }
    private readonly Dictionary<int, Action> _calculatedSubroutines;

    public M68K Cpu { get;}
    public Memory Memory { get;}

    public Amiga()
    {
        Libraries = new Dictionary<int, AbstractLibrary>();
        _calculatedSubroutines = new Dictionary<int, Action>();

        // Always start with exec.library loaded.
        OpenLibrary(new ExecLibrary(this));

        Cpu = new M68K();
        Memory = new Memory();
    }

    public int OpenLibrary(AbstractLibrary abstractLibrary)
    {
        try
        {
            var offset = abstractLibrary.InitialOffset() ?? CalculateNextLibraryOffset();
            Libraries.Add(offset, abstractLibrary);
            RebuildPrecompiledLibraryOffsets();
            return offset;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return 0;
        }
    }

    public void CloseLibrary(int libraryAddress)
    {
        Libraries.Remove(libraryAddress);
        RebuildPrecompiledLibraryOffsets();
    }

    private void RebuildPrecompiledLibraryOffsets()
    {
        _calculatedSubroutines.Clear();

        foreach (var libraryOffset in Libraries.Keys)
        {
            // Get attributes from library.
            var library =  Libraries[libraryOffset];
            foreach (var libraryMethod in library.GetType().GetMethods())
            {
                foreach (var customAttribute in
                         libraryMethod.GetCustomAttributes(typeof(LibraryFunctionOffsetAttribute), false))
                {
                    var libraryFunctionOffsetAttribute = (LibraryFunctionOffsetAttribute)customAttribute;
                    _calculatedSubroutines.Add(libraryFunctionOffsetAttribute.Offset + libraryOffset, () =>
                    {
                        libraryMethod.Invoke(Libraries[libraryOffset], []);
                    });
                }
            }
        }
    }

    private int CalculateNextLibraryOffset()
    {
        // Ignore me.... testing.
        return Libraries.Count() * -10000;
    }

    public void JumpSubroutine(int address)
    {
        _calculatedSubroutines[address]();
    }
}
