; Draws into a bitmap with graphics.library and returns the address of the BitMap in D0.
; Build: vasmm68k_mot -Fhunkexe -nosym -L graphics.lst -o graphics graphics.s
;
; The drawing, in order:
;   1. A rectangle in pen 1 from (2,2) to (20,12).
;   2. A line in pen 2 from (24,2) to (40,18).
;   3. The text "Hi" in pen 3 with JAM1, on the baseline at (44,10).
;   4. A rectangle from (10,8) to (30,16) in COMPLEMENT mode, which inverts the bits.
;   5. A copy of the area from (0,0) to (23,15) to (64,16).

SysBase			= 4
_LVOCloseLibrary	= -414
_LVOOpenLibrary		= -552
_LVOText		= -60
_LVOInitRastPort	= -198
_LVOSetRast		= -234
_LVOMove		= -240
_LVODraw		= -246
_LVORectFill		= -306
_LVOSetAPen		= -342
_LVOSetDrMd		= -354
_LVOInitBitMap		= -390
_LVOAllocRaster		= -492
_LVOBltBitMapRastPort	= -606
JAM1			= 0
COMPLEMENT		= 2
WIDTH			= 96
HEIGHT			= 32
DEPTH			= 2
bm_Planes		= 8
rp_BitMap		= 4

			SECTION	code,CODE

Start:			MOVEM.L	D2-D7/A2-A6,-(A7)
			MOVEA.L	SysBase,A6
			LEA	GraphicsName(PC),A1
			MOVEQ	#0,D0
			JSR	_LVOOpenLibrary(A6)
			TST.L	D0
			BEQ	.failed
			MOVEA.L	D0,A6

; Make the bitmap and its two planes, and a RastPort for it.
			LEA	Bitmap,A0
			MOVEQ	#DEPTH,D0
			MOVEQ	#WIDTH,D1
			MOVEQ	#HEIGHT,D2
			JSR	_LVOInitBitMap(A6)
			MOVEQ	#WIDTH,D0
			MOVEQ	#HEIGHT,D1
			JSR	_LVOAllocRaster(A6)
			MOVE.L	D0,Bitmap+bm_Planes
			MOVEQ	#WIDTH,D0
			MOVEQ	#HEIGHT,D1
			JSR	_LVOAllocRaster(A6)
			MOVE.L	D0,Bitmap+bm_Planes+4
			LEA	Port,A1
			JSR	_LVOInitRastPort(A6)
			LEA	Port,A2
			MOVE.L	#Bitmap,rp_BitMap(A2)
			MOVEA.L	A2,A1
			MOVEQ	#0,D0
			JSR	_LVOSetRast(A6)

; 1. The rectangle.
			MOVEA.L	A2,A1
			MOVEQ	#1,D0
			JSR	_LVOSetAPen(A6)
			MOVEA.L	A2,A1
			MOVEQ	#2,D0
			MOVEQ	#2,D1
			MOVEQ	#20,D2
			MOVEQ	#12,D3
			JSR	_LVORectFill(A6)

; 2. The line.
			MOVEA.L	A2,A1
			MOVEQ	#2,D0
			JSR	_LVOSetAPen(A6)
			MOVEA.L	A2,A1
			MOVEQ	#24,D0
			MOVEQ	#2,D1
			JSR	_LVOMove(A6)
			MOVEA.L	A2,A1
			MOVEQ	#40,D0
			MOVEQ	#18,D1
			JSR	_LVODraw(A6)

; 3. The text.
			MOVEA.L	A2,A1
			MOVEQ	#3,D0
			JSR	_LVOSetAPen(A6)
			MOVEA.L	A2,A1
			MOVEQ	#JAM1,D0
			JSR	_LVOSetDrMd(A6)
			MOVEA.L	A2,A1
			MOVEQ	#44,D0
			MOVEQ	#10,D1
			JSR	_LVOMove(A6)
			MOVEA.L	A2,A1
			LEA	HiText(PC),A0
			MOVEQ	#2,D0
			JSR	_LVOText(A6)

; 4. The inverted rectangle.
			MOVEA.L	A2,A1
			MOVEQ	#COMPLEMENT,D0
			JSR	_LVOSetDrMd(A6)
			MOVEA.L	A2,A1
			MOVEQ	#10,D0
			MOVEQ	#8,D1
			MOVEQ	#30,D2
			MOVEQ	#16,D3
			JSR	_LVORectFill(A6)

; 5. The copy. Minterm $C0 copies the source.
			LEA	Bitmap,A0
			MOVEQ	#0,D0
			MOVEQ	#0,D1
			MOVEA.L	A2,A1
			MOVEQ	#64,D2
			MOVEQ	#16,D3
			MOVEQ	#24,D4
			MOVEQ	#16,D5
			MOVE.L	#$C0,D6
			JSR	_LVOBltBitMapRastPort(A6)

			MOVEA.L	A6,A1
			MOVEA.L	SysBase,A6
			JSR	_LVOCloseLibrary(A6)
			MOVE.L	#Bitmap,D0
			BRA.S	.exit

.failed:		MOVEQ	#0,D0
.exit:			MOVEM.L	(A7)+,D2-D7/A2-A6
			RTS

GraphicsName:		DC.B	"graphics.library",0
HiText:			DC.B	"Hi"

			SECTION	variables,BSS
Bitmap:			DS.B	40
Port:			DS.B	100
