using System.Text;
using AmigaSharp.Runtime;

namespace AmigaSharp;

/// <summary>
/// A hand translation of hello.s. It shows the form of the code that the recompiler will emit.
/// </summary>
public static class HelloWorldTranslated
{
    // SysBase			= 4
    private const uint SysBase = 4;
    // OpenLibrary		= -552
    private const short OpenLibrary = -552;
    // CloseLibrary		= -414
    private const short CloseLibrary = -414;
    // PutStr			= -948
    private const short PutStr = -948;

    public static uint Run(Core core)
    {
        var cpu = core.Cpu;
        var memory = core.Memory;

        // DosName			DC.B		"dos.library",0
        // Hello			DC.B		"Hello World!",10,0
        // TODO: Load these from the data hunk of the executable.
        var dosName = core.AllocateStatic(Encoding.Latin1.GetBytes("dos.library\0"));
        var hello = core.AllocateStatic(Encoding.Latin1.GetBytes("Hello World!\n\0"));

        // 			LEA	DosName,A1		;dos.library name string
        cpu.A[1] = dosName;
        // 			MOVEQ	#36,D0			;minimum required version (36 = Kick 2.0)
        cpu.D[0] = 36;
        cpu.SetLogicFlags32(cpu.D[0]);
        // 			MOVEA.L	SysBase,A6
        cpu.A[6] = memory.Read32(SysBase);
        // 			JSR	OpenLibrary(A6)
        core.CallVector(cpu.A[6], OpenLibrary);

        // 			TST.L	D0			;zero if OpenLibrary() failed
        cpu.SetLogicFlags32(cpu.D[0]);
        // 			BEQ.S	NoDos			;if failed, skip to exit
        if (cpu.Z)
            goto NoDos;

        // 			MOVE.L	#Hello,D1		;string to print
        cpu.D[1] = hello;
        cpu.SetLogicFlags32(cpu.D[1]);
        // 			MOVEA.L	D0,A6			;moving DOSBase to A6
        cpu.A[6] = cpu.D[0];
        // 			JSR	PutStr(A6)
        core.CallVector(cpu.A[6], PutStr);

        // 			MOVEA.L	A6,A1			;DOSBase, library to close
        cpu.A[1] = cpu.A[6];
        // 			MOVEA.L	SysBase,A6
        cpu.A[6] = memory.Read32(SysBase);
        // 			JSR	CloseLibrary(A6)
        core.CallVector(cpu.A[6], CloseLibrary);

        // NoDos			CLR.L	D0			;return 0 to the system
        NoDos:
        cpu.D[0] = 0;
        cpu.SetLogicFlags32(cpu.D[0]);
        // 			RTS
        return cpu.D[0];
    }
}
