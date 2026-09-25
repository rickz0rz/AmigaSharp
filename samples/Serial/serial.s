; Reads a line from the serial port with an RBF interrupt handler, and sends it back in upper case with
; serial.device. Returns the length of the line in D0.
; Build: vasmm68k_mot -Fhunkexe -nosym -L serial.lst -o serial serial.s
;
; The program opens serial.device at 19200 baud and installs its own RBF handler, as Prevue does. The handler
; stores each byte, and at the end of the line it signals the main task. The main task waits for that signal.

SysBase			= 4
_LVOSetIntVector	= -162
_LVOFindTask		= -294
_LVOWait		= -318
_LVOSignal		= -324
_LVOAllocSignal		= -330
_LVOFreeSignal		= -336
_LVOOpenDevice		= -444
_LVOCloseDevice		= -450
_LVODoIO		= -456
_LVOCreateIORequest	= -654
_LVODeleteIORequest	= -660
_LVOCreateMsgPort	= -666
_LVODeleteMsgPort	= -672
INTB_RBF		= 11
NT_INTERRUPT		= 2
SERDATR			= $018			;offsets from the custom chip base in A0
INTREQ			= $09C
INTF_RBF		= $0800
CMD_WRITE		= 3
SDCMD_SETPARAMS		= 11
IO_COMMAND		= 28
IO_LENGTH		= 36
IO_DATA			= 40
IO_BAUD			= 60
IOEXTSER_SIZE		= 82

			SECTION	code,CODE

Start:			MOVEM.L	D2-D7/A2-A6,-(A7)
			MOVEA.L	SysBase,A6
			SUBA.L	A1,A1
			JSR	_LVOFindTask(A6)
			MOVE.L	D0,Task
			MOVEQ	#-1,D0
			JSR	_LVOAllocSignal(A6)
			MOVE.L	D0,D7
			BMI	.failed
			MOVEQ	#0,D6
			BSET	D7,D6
			MOVE.L	D6,Mask

; Open serial.device and set the baud rate.
			JSR	_LVOCreateMsgPort(A6)
			MOVEA.L	D0,A2
			MOVEA.L	A2,A0
			MOVEQ	#IOEXTSER_SIZE,D0
			JSR	_LVOCreateIORequest(A6)
			MOVEA.L	D0,A3
			LEA	SerialName(PC),A0
			MOVEQ	#0,D0
			MOVEA.L	A3,A1
			MOVEQ	#0,D1
			JSR	_LVOOpenDevice(A6)
			TST.L	D0
			BNE	.failed
			MOVE.L	#19200,IO_BAUD(A3)
			MOVE.W	#SDCMD_SETPARAMS,IO_COMMAND(A3)
			MOVEA.L	A3,A1
			JSR	_LVODoIO(A6)

; Install the handler and wait for the line.
			MOVEQ	#INTB_RBF,D0
			LEA	Interrupt,A1
			JSR	_LVOSetIntVector(A6)
			MOVE.L	D0,OldInterrupt
			MOVE.L	Mask,D0
			JSR	_LVOWait(A6)
			MOVEQ	#INTB_RBF,D0
			MOVEA.L	OldInterrupt,A1
			JSR	_LVOSetIntVector(A6)

; Change the line to upper case.
			LEA	Line,A0
			MOVE.L	Count,D0
			SUBQ.L	#1,D0
.upper:			MOVE.B	(A0),D1
			CMPI.B	#'a',D1
			BLT.S	.next
			CMPI.B	#'z',D1
			BGT.S	.next
			SUBI.B	#32,D1
			MOVE.B	D1,(A0)
.next:			ADDQ.L	#1,A0
			DBRA	D0,.upper

; Send the line back.
			MOVE.W	#CMD_WRITE,IO_COMMAND(A3)
			MOVE.L	Count,IO_LENGTH(A3)
			MOVE.L	#Line,IO_DATA(A3)
			MOVEA.L	A3,A1
			JSR	_LVODoIO(A6)

			MOVEA.L	A3,A1
			JSR	_LVOCloseDevice(A6)
			MOVEA.L	A3,A0
			JSR	_LVODeleteIORequest(A6)
			MOVEA.L	A2,A0
			JSR	_LVODeleteMsgPort(A6)
			MOVE.L	D7,D0
			JSR	_LVOFreeSignal(A6)
			MOVE.L	Count,D0
			BRA.S	.exit

.failed:		MOVEQ	#-1,D0
.exit:			MOVEM.L	(A7)+,D2-D7/A2-A6
			RTS

; The RBF handler. Exec calls it with A0 = the custom chips, A1 = is_Data and A6 = ExecBase. It reads the byte,
; clears the interrupt request, and signals the main task at the end of the line.
RbfHandler:		MOVE.W	SERDATR(A0),D0
			MOVE.W	#INTF_RBF,INTREQ(A0)
			MOVE.L	Count,D1
			LEA	Line,A1
			MOVE.B	D0,0(A1,D1.L)
			ADDQ.L	#1,D1
			MOVE.L	D1,Count
			CMPI.B	#10,D0
			BNE.S	.done
			MOVEA.L	Task,A1
			MOVE.L	Mask,D0
			JSR	_LVOSignal(A6)
.done:			RTS

SerialName:		DC.B	"serial.device",0
InterruptName:		DC.B	"line reader",0
			EVEN

			SECTION	data,DATA
; struct Interrupt
Interrupt:		DC.L	0,0			;ln_Succ, ln_Pred
			DC.B	NT_INTERRUPT,0		;ln_Type, ln_Pri
			DC.L	InterruptName		;ln_Name
			DC.L	0			;is_Data
			DC.L	RbfHandler		;is_Code

			SECTION	variables,BSS
Task:			DS.L	1
Mask:			DS.L	1
OldInterrupt:		DS.L	1
Count:			DS.L	1
Line:			DS.B	80
