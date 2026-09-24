; Prints "Hello World!" with dos.library PutStr and returns 0.
; Build: vasmm68k_mot -Fhunkexe -nosym -L hello.lst -o hello hello.s

SysBase			= 4
OpenLibrary		= -552
CloseLibrary		= -414
PutStr			= -948

			SECTION	code,CODE

Start:			LEA	DosName,A1		;dos.library name string
			MOVEQ	#36,D0			;minimum required version (36 = Kick 2.0)
			MOVEA.L	SysBase,A6
			JSR	OpenLibrary(A6)
			TST.L	D0			;zero if OpenLibrary() failed
			BEQ.S	NoDos			;if failed, skip to exit

			MOVE.L	#Hello,D1		;string to print
			MOVEA.L	D0,A6			;moving DOSBase to A6
			JSR	PutStr(A6)

			MOVEA.L	A6,A1			;DOSBase, library to close
			MOVEA.L	SysBase,A6
			JSR	CloseLibrary(A6)

NoDos:			CLR.L	D0			;return 0 to the system
			RTS

DosName:		DC.B	"dos.library",0
Hello:			DC.B	"Hello World!",10,0
