; Starts a second process with CreateProc, as SAS/C programs do, and returns 1000 in D0 if the two tasks share the
; CPU correctly.
; Build: vasmm68k_mot -Fhunkexe -nosym -L tasks.lst -o tasks tasks.s
;
; The two tasks wait for each other in busy loops, so they work only if the scheduler switches tasks at safe points.
; At the end, the child signals the main task, and the main task waits for the signal.

SysBase			= 4
_LVOFindTask		= -294
_LVOWait		= -318
_LVOSignal		= -324
_LVOAllocSignal		= -330
_LVOCloseLibrary	= -414
_LVOOpenLibrary		= -552
_LVOCreateProc		= -138

			SECTION	code,CODE

Start:			MOVEM.L	D2-D7/A2-A6,-(A7)
			MOVEA.L	SysBase,A6
			LEA	DosName(PC),A1
			MOVEQ	#0,D0
			JSR	_LVOOpenLibrary(A6)
			MOVE.L	D0,DosBase
			SUBA.L	A1,A1
			JSR	_LVOFindTask(A6)
			MOVE.L	D0,MainTask
			MOVEQ	#-1,D0
			JSR	_LVOAllocSignal(A6)
			MOVEQ	#0,D1
			BSET	D0,D1
			MOVE.L	D1,Mask

; The segment list is a BPTR to a long that has the next segment (none), and the code follows it.
			MOVE.L	#SegmentList+4,D3
			LSR.L	#2,D3
			MOVE.L	#ChildName,D1
			MOVEQ	#0,D2
			MOVE.L	#4096,D4
			MOVEA.L	DosBase,A6
			JSR	_LVOCreateProc(A6)

; Wait in a busy loop until the child runs, then let it continue.
.spin:			TST.L	Started
			BEQ.S	.spin
			MOVE.L	#1,Go

; Wait for the signal of the child.
			MOVEA.L	SysBase,A6
			MOVE.L	Mask,D0
			JSR	_LVOWait(A6)

			MOVEA.L	DosBase,A1
			JSR	_LVOCloseLibrary(A6)
			MOVE.L	Counter,D0
			MOVE.L	ChildTask,D1
			CMP.L	MainTask,D1
			BNE.S	.exit
			MOVEQ	#0,D0			;the child ran as the main task
.exit:			MOVEM.L	(A7)+,D2-D7/A2-A6
			RTS

; The code of the child process.
Child:			MOVEA.L	SysBase,A6
			SUBA.L	A1,A1
			JSR	_LVOFindTask(A6)
			MOVE.L	D0,ChildTask
			MOVE.L	#1,Started
.wait:			TST.L	Go
			BEQ.S	.wait
			MOVE.L	#1000,Counter
			MOVEA.L	MainTask,A1
			MOVE.L	Mask,D0
			JSR	_LVOSignal(A6)
			RTS

DosName:		DC.B	"dos.library",0
ChildName:		DC.B	"child task",0

			SECTION	data,DATA
; A memory block with the size first, then the segment: the BPTR to the next segment, and a JMP to the child.
SegmentList:		DC.L	16
			DC.L	0
			DC.W	$4EF9
			DC.L	Child

			SECTION	variables,BSS
DosBase:		DS.L	1
MainTask:		DS.L	1
ChildTask:		DS.L	1
Mask:			DS.L	1
Started:		DS.L	1
Go:			DS.L	1
Counter:		DS.L	1
