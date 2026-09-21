using AmigaSharp.AmigaCore;
using AmigaSharp.AmigaCore.CPU;

namespace AmigaSharp;

public static class HelloWorldTranslated
{
    public static int Run(Amiga amiga)
    {
        // SysBase			= 4
        const int constantSysBase = 4;
        // OpenLibrary		= -552
        const int constantOpenLibrary = -552;
        // CloseLibrary		= -414
        const int constantCloseLibrary = -414;
        // PutStr			= -948
        const int constantPutStr = -948;

        amiga.Memory.Append("dos.library\0", "DosName"); // Transported from bottom
        amiga.Memory.Append("Hello World!\n\0", "Hello"); // Transported from bottom

        // 			LEA	DosName,A1		;dos.library name string
        amiga.Cpu.LoadEffectiveAddress(AddressRegister.A1, amiga.Memory.GetAddress("DosName"));
        // 			MOVEQ	#36,D0			;minimum required version (36 = Kick 2.0)
        amiga.Cpu.MoveQuick(DataRegister.D0, 36);
        // 			MOVEA.L	SysBase,A6
        amiga.Cpu.MoveAddressLong(AddressRegister.A6, constantSysBase);
        // 			JSR	OpenLibrary(A6)
        amiga.JumpSubroutine(constantOpenLibrary + amiga.Cpu.A[6]);

        // 			TST.L	D0			;zero if OpenLibrary() failed
        // 			BEQ.S	NoDos			;if failed, skip to exit
        if (amiga.Cpu.D[0] == 0)
            goto NoDos;

        // 			MOVE.L	#Hello,D1		;string to print
        amiga.Cpu.MoveLong(DataRegister.D1, amiga.Memory.GetAddress("Hello"));
        // 			MOVEA.L	D0,A6			;moving DOSBase to A6
        amiga.Cpu.MoveAddressLong(AddressRegister.A6, amiga.Cpu.D[0]);
        // 			JSR	PutStr(A6)
        amiga.JumpSubroutine(constantPutStr + amiga.Cpu.A[6]);


        // 			MOVEA.L	A6,A1			;DOSBase, library to close
        amiga.Cpu.MoveAddressLong(AddressRegister.A1, amiga.Cpu.A[6]);
        // 			MOVEA.L	SysBase,A6
        amiga.Cpu.MoveAddressLong(AddressRegister.A6, constantSysBase);
        // 			JSR	CloseLibrary(A6)
        amiga.JumpSubroutine(constantCloseLibrary + amiga.Cpu.A[6]);

        // NoDos			CLR.L	D0			;return 0 to the system
        NoDos: amiga.Cpu.ClearLong(DataRegister.D0);
        // 			RTS
        return amiga.Cpu.D[0]; // if not in any subroutines...

        // # Moved up top.
        // DosName			DC.B		"dos.library",0
        // Hello			DC.B		"Hello World!",10,0
    }
}
