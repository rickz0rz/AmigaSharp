using System.Text;
using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Runtime;

/// <summary>
/// The base class of a program that the translator made from an AmigaOS executable.
/// </summary>
/// <remarks>
/// The translated code contains the relocated addresses as constants. So the executable must load at the addresses
/// that <see cref="HunkLayout"/> gives, and it must be the same file that the translator read.
/// </remarks>
public abstract class TranslatedProgram(Core core)
{
    protected readonly Core core = core;
    protected readonly CpuState cpu = core.Cpu;
    protected readonly Memory memory = core.Memory;

    /// <summary>Registers each translated function with <see cref="Core.RegisterFunction"/>.</summary>
    protected abstract void RegisterFunctions();

    /// <summary>
    /// Loads the executable and runs it as a CLI command, as AmigaDOS does. Returns the value of D0 when the program
    /// ends.
    /// </summary>
    /// <param name="executable">The contents of the executable that the translator read.</param>
    /// <param name="arguments">The command line arguments, without the command name.</param>
    /// <param name="commandName">The name of the command. The program can read it from the CLI structure.</param>
    public uint Run(byte[] executable, string arguments = "", string commandName = "program")
    {
        var file = HunkFile.Parse(executable);
        var bases = HunkLayout.Assign(file);
        foreach (var hunk in file.Hunks)
            core.Allocator.Reserve(bases[hunk.Index], hunk.Size);
        file.Load(memory, bases);
        RegisterFunctions();
        SetUpCli(commandName);

        // AmigaDOS starts a CLI command with A0 pointing to the arguments and D0 holding their length. The arguments
        // end with a newline. The stack size is at 4(SP), above the return address.
        var argumentBytes = Encoding.Latin1.GetBytes(arguments + "\n\0");
        cpu.A[0] = core.AllocateSystem(argumentBytes);
        cpu.D[0] = (uint)(argumentBytes.Length - 1);
        cpu.Push32(Core.StackSize);

        // The program starts at the first byte of hunk 0.
        core.CallAddress(Core.ExitAddress, bases[0]);
        cpu.Pop32();
        return cpu.D[0];
    }

    private void SetUpCli(string commandName)
    {
        var process = core.MainProcess;
        var cli = core.AllocateSystem(CliOffsets.Size);
        var name = Encoding.Latin1.GetBytes(commandName);
        var bstr = core.AllocateSystem([(byte)name.Length, .. name]);

        memory.Write32(cli + CliOffsets.CommandName, bstr >> 2);
        memory.Write32(cli + CliOffsets.DefaultStack, Core.StackSize / 4);
        memory.Write32(cli + CliOffsets.StandardInput, memory.Read32(process + ProcessOffsets.InputStream));
        memory.Write32(cli + CliOffsets.CurrentInput, memory.Read32(process + ProcessOffsets.InputStream));
        memory.Write32(cli + CliOffsets.StandardOutput, memory.Read32(process + ProcessOffsets.OutputStream));
        memory.Write32(cli + CliOffsets.CurrentOutput, memory.Read32(process + ProcessOffsets.OutputStream));
        memory.Write32(cli + CliOffsets.Interactive, 0xFFFF_FFFF);
        memory.Write32(process + ProcessOffsets.Cli, cli >> 2);
    }
}

/// <summary>A program that has no translated functions. The interpreter runs all of its code.</summary>
public sealed class InterpretedProgram(Core core) : TranslatedProgram(core)
{
    protected override void RegisterFunctions()
    {
    }
}
