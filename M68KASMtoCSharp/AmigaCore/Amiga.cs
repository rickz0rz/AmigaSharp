using M68KASMtoCSharp.AmigaCore.CPU;
using M68KASMtoCSharp.Libraries;
using M68KASMtoCSharp.Libraries.Native;

namespace M68KASMtoCSharp.AmigaCore;

public class Amiga
{
    private readonly Dictionary<string, int> _dataLocations;
    private readonly List<byte> _data;
    public Dictionary<int, AbstractLibrary> Libraries { get; }
    private readonly Dictionary<int, Action> _calculatedSubroutines;

    public M68K Cpu { get;}

    public Amiga()
    {
        _dataLocations = new Dictionary<string, int>();
        _data = [];
        Libraries = new Dictionary<int, AbstractLibrary>();
        _calculatedSubroutines = new Dictionary<int, Action>();

        // Always start with exec.library loaded.
        OpenLibrary(new ExecLibrary(this));

        Cpu = new M68K();
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

    public void AppendData(string stringData, string name)
    {
        _dataLocations.Add(name, _data.Count);
        _data.AddRange(stringData.ToCharArray().Select(c => (byte)c));
    }

    public int GetDataAddress(string name)
    {
        return _dataLocations[name];
    }

    public void JumpSubroutine(int address)
    {
        _calculatedSubroutines[address]();
    }

    public byte ReadByteMemory(int address)
    {
        return _data[address];
    }
}
