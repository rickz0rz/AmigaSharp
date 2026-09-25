; Writes a file, reads it back and prints it with exec.library and dos.library.
; Build: vasmm68k_mot -Fhunkexe -nosym -L fileio.lst -o fileio fileio.s
;
; The output is:
;   Read 11 bytes: hello, file
;   -12-002a-abc|de   |
;   IoErr 205

SysBase			= 4
_LVOAllocMem		= -198
_LVOFreeMem		= -210
_LVOCloseLibrary	= -414
_LVORawDoFmt		= -522
_LVOOpenLibrary		= -552
_LVOOpen		= -30
_LVOClose		= -36
_LVORead		= -42
_LVOWrite		= -48
_LVOSeek		= -66
_LVODeleteFile		= -72
_LVOIoErr		= -132
_LVOPutStr		= -948
_LVOVPrintf		= -954
MODE_OLDFILE		= 1005
MODE_NEWFILE		= 1006
OFFSET_BEGINNING	= -1
MEMF_PUBLIC_CLEAR	= $10001
BUFFER_SIZE		= 64

			SECTION	code,CODE

Start:			MOVEM.L	D2-D7/A2-A6,-(A7)
			MOVEA.L	SysBase,A6
			LEA	DosName(PC),A1
			MOVEQ	#36,D0
			JSR	_LVOOpenLibrary(A6)
			MOVE.L	D0,DosBase
			BEQ	.no_dos

; Write the text to a new file, then go back to the start of the file.
			MOVEA.L	D0,A6
			MOVE.L	#FileName,D1
			MOVE.L	#MODE_NEWFILE,D2
			JSR	_LVOOpen(A6)
			MOVE.L	D0,D7
			BEQ	.failed
			MOVE.L	D7,D1
			MOVE.L	#Text,D2
			MOVEQ	#TextLength,D3
			JSR	_LVOWrite(A6)
			MOVE.L	D7,D1
			MOVEQ	#0,D2
			MOVEQ	#OFFSET_BEGINNING,D3
			JSR	_LVOSeek(A6)

; Read the file into a clear buffer.
			MOVEA.L	SysBase,A6
			MOVEQ	#BUFFER_SIZE,D0
			MOVE.L	#MEMF_PUBLIC_CLEAR,D1
			JSR	_LVOAllocMem(A6)
			MOVEA.L	D0,A5
			MOVEA.L	DosBase,A6
			MOVE.L	D7,D1
			MOVE.L	A5,D2
			MOVEQ	#BUFFER_SIZE,D3
			JSR	_LVORead(A6)
			MOVE.L	D0,D6
			MOVE.L	D7,D1
			JSR	_LVOClose(A6)

; Print the result with VPrintf. The arguments are longs on the stack.
			MOVE.L	A5,-(A7)
			MOVE.L	D6,-(A7)
			MOVE.L	#ReadFormat,D1
			MOVE.L	A7,D2
			JSR	_LVOVPrintf(A6)
			ADDQ.L	#8,A7

; Format with RawDoFmt and a PutChProc in 68000 code, then print the buffer.
			LEA	FormatText(PC),A0
			LEA	FormatData(PC),A1
			LEA	StuffChar(PC),A2
			LEA	FormatBuffer,A3
			MOVEA.L	SysBase,A6
			JSR	_LVORawDoFmt(A6)
			MOVEA.L	DosBase,A6
			MOVE.L	#FormatBuffer,D1
			JSR	_LVOPutStr(A6)

; Open a file that does not exist and print the error code.
			MOVE.L	#MissingName,D1
			MOVE.L	#MODE_OLDFILE,D2
			JSR	_LVOOpen(A6)
			JSR	_LVOIoErr(A6)
			MOVE.L	D0,-(A7)
			MOVE.L	#ErrorFormat,D1
			MOVE.L	A7,D2
			JSR	_LVOVPrintf(A6)
			ADDQ.L	#4,A7

; Clean up.
			MOVE.L	#FileName,D1
			JSR	_LVODeleteFile(A6)
			MOVEA.L	SysBase,A6
			MOVEA.L	A5,A1
			MOVEQ	#BUFFER_SIZE,D0
			JSR	_LVOFreeMem(A6)
			MOVEA.L	DosBase,A1
			JSR	_LVOCloseLibrary(A6)
			MOVEQ	#0,D0
			BRA.S	.exit

.failed:		MOVEQ	#10,D0
			BRA.S	.exit
.no_dos:		MOVEQ	#20,D0
.exit:			MOVEM.L	(A7)+,D2-D7/A2-A6
			RTS

; The PutChProc of RawDoFmt: stores the character in D0 at (A3)+.
StuffChar:		MOVE.B	D0,(A3)+
			RTS

DosName:		DC.B	"dos.library",0
FileName:		DC.B	"test.txt",0
MissingName:		DC.B	"missing.txt",0
Text:			DC.B	"hello, file"
TextLength		= *-Text
			DC.B	0
ReadFormat:		DC.B	"Read %ld bytes: %s",10,0
FormatText:		DC.B	"%d-%04x-%s|%-5s|",10,0
ErrorFormat:		DC.B	"IoErr %ld",10,0
First:			DC.B	"abc",0
Second:			DC.B	"de",0
			EVEN
; RawDoFmt reads a word for %d and %x, and a long for %s.
FormatData:		DC.W	-12,$2A
			DC.L	First,Second

			SECTION	variables,BSS
DosBase:		DS.L	1
FormatBuffer:		DS.B	80
