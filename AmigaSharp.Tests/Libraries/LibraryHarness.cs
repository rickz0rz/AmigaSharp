using System.Text;
using AmigaSharp.Runtime;

namespace AmigaSharp.Tests.Libraries;

/// <summary>
/// A core for the tests of a library. The calls go through the jump table of the library, as a JSR from a program
/// does.
/// </summary>
public sealed class LibraryHarness : IDisposable
{
    public LibraryHarness()
    {
        Root = Directory.CreateTempSubdirectory("AmigaSharp-test-").FullName;
        Core = new Core(Output, new MemoryStream(), Root) { Log = TextWriter.Null };
        DosBase = Core.OpenLibrary("dos.library", 0)!.Base;
    }

    public Core Core { get; }
    public Memory Memory => Core.Memory;
    public MemoryStream Output { get; } = new();

    /// <summary>The host directory of SYS:.</summary>
    public string Root { get; }

    public uint ExecBase => Core.ExecBase;
    public uint DosBase { get; }

    public string OutputText => Encoding.Latin1.GetString(Output.ToArray());

    /// <summary>Calls the function at the offset. The registers are "D0", "A1" and so on. Returns D0.</summary>
    public uint Call(uint libraryBase, short offset, params (string Register, uint Value)[] registers)
    {
        foreach (var (register, value) in registers)
        {
            var number = register[1] - '0';
            if (register[0] == 'D')
                Core.Cpu.D[number] = value;
            else
                Core.Cpu.A[number] = value;
        }

        Core.CallVector(libraryBase, offset);
        return Core.Cpu.D[0];
    }

    public uint String(string text) => Core.AllocateSystem(Encoding.Latin1.GetBytes(text + "\0"));

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
