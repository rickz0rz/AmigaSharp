using M68KASMtoCSharp.AmigaCore;
using M68KASMtoCSharp.AmigaCore.CPU;

namespace M68KASMtoCSharp;

class Program
{
    // Fix cases where i'm expecting D or A registers and use correct instructions
    static int Main(string[] args)
    {
        Amiga amiga = new Amiga();

        // SysBase			= 4
        int CONSTANT_SysBase = 4;
        // OpenLibrary		= -552
        int CONSTANT_OpenLibrary = -552;
        // CloseLibrary		= -414
        int CONSTANT_CloseLibrary = -414;
        // PutStr			= -948
        int CONSTANT_PutStr = -948;

        amiga.AppendData("dos.library\0", "DosName");
        amiga.AppendData("Hello World!\n\0", "Hello");

        // 			LEA	DosName,A1		;dos.library name string
        amiga.Cpu.LoadEffectiveAddress(amiga.GetDataAddress("DosName"), AddressRegister.A1);
        // 			MOVEQ	#36,D0			;minimum required version (36 = Kick 2.0)
        amiga.Cpu.MoveQuick(36, DataRegister.D0);
        // 			MOVEA.L	SysBase,A6
        amiga.Cpu.MoveAddressLong(CONSTANT_SysBase, AddressRegister.A6);
        // 			JSR	OpenLibrary(A6)
        amiga.JumpSubroutine(CONSTANT_OpenLibrary + amiga.Cpu.GetAddressRegister(AddressRegister.A6));

        // 			TST.L	D0			;zero if OpenLibrary() failed
        // 			BEQ.S	NoDos			;if failed, skip to exit
        if (amiga.Cpu.GetDataRegister(DataRegister.D0) == 0)
        {
            goto NoDos;
        }

        // 			MOVE.L	#Hello,D1		;string to print
        amiga.Cpu.MoveLong(amiga.GetDataAddress("Hello"), DataRegister.D1);
        // 			MOVEA.L	D0,A6			;moving DOSBase to A6
        amiga.Cpu.MoveAddressLong(amiga.Cpu.GetDataRegister(DataRegister.D0), AddressRegister.A6);
        // 			JSR	PutStr(A6)
        amiga.JumpSubroutine(CONSTANT_PutStr + amiga.Cpu.GetAddressRegister(AddressRegister.A6));


        // 			MOVEA.L	A6,A1			;DOSBase, library to close
        amiga.Cpu.MoveAddressLong(amiga.Cpu.GetAddressRegister(AddressRegister.A6), AddressRegister.A1);
        // 			MOVEA.L	SysBase,A6
        amiga.Cpu.MoveAddressLong(CONSTANT_SysBase, AddressRegister.A6);
        // 			JSR	CloseLibrary(A6)
        amiga.JumpSubroutine(CONSTANT_CloseLibrary + amiga.Cpu.GetAddressRegister(AddressRegister.A6));

        // NoDos			CLR.L	D0			;return 0 to the system
        NoDos: amiga.Cpu.ClearLong(DataRegister.D0);
        // 			RTS
            return amiga.Cpu.GetDataRegister(DataRegister.D0); // if not in any subroutines...

        // # Moved up top.
        // DosName			DC.B		"dos.library",0
        // Hello			DC.B		"Hello World!",10,0
    }
}
