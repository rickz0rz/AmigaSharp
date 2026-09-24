; Tests the control flow of translated code. The program returns 1111 in D0.
; Build: vasmm68k_mot -Fhunkexe -nosym -L controlflow.lst -o controlflow controlflow.s

			SECTION	code,CODE

; Adds 1 + 10 + 1000 (the jump table cases) and 100 (the stack frame) to D7.
; Escape then returns to the caller of Start through the saved stack pointer.
Start:			MOVE.L	A7,SavedSp
			MOVEQ	#0,D7
			MOVEQ	#2,D0
.cases:			BSR	Dispatch
			DBRA	D0,.cases
			BSR	Frame
			BSR	Escape
			MOVEQ	#-1,D0			;never gets here
			RTS

; Runs case D0 through a PC-relative jump table. The cases are local labels,
; so they are not functions. The runtime runs them in the interpreter.
Dispatch:		MOVEM.L	D0-D1,-(A7)
			ADD.W	D0,D0
			MOVE.W	.table(PC,D0.W),D1
			JMP	.table(PC,D1.W)
.table:			DC.W	.case0-.table,.case1-.table,.case2-.table
.case0:			ADDQ.L	#1,D7
			BRA.S	.done
.case1:			BSR	AddTen			;interpreted code calls a translated function
			BRA.S	.done
.case2:			ADD.L	#1000,D7
.done:			MOVEM.L	(A7)+,D0-D1
			RTS

AddTen:			ADD.L	#10,D7
			RTS

; Uses a stack frame for a local variable.
Frame:			LINK	A5,#-8
			MOVE.L	#100,-4(A5)
			ADD.L	-4(A5),D7
			UNLK	A5
			RTS

; Restores the stack pointer of Start and returns from the program with D0 = D7.
Escape:			BSR.S	.deeper
			MOVEQ	#-2,D0			;never gets here
			RTS
.deeper:		MOVEA.L	SavedSp,A7
			MOVE.L	D7,D0
			RTS

			SECTION	data,DATA
SavedSp:		DC.L	0
