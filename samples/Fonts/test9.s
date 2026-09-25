; A disk font for the tests: "test.font", size 9. It has the characters A, B and C, and a box for the
; characters that it does not have. The characters have different widths, and B has a kern of 1.
; Build: vasmm68k_mot -Fhunkexe -nosym -o test/9 test9.s

NT_FONT			= 12
FPF_DISKFONT		= $02
FPF_PROPORTIONAL	= $20

			SECTION	font,CODE

; A font file starts with code that returns an error, in case a program runs it.
			MOVEQ	#-1,D0
			RTS

; struct DiskFontHeader
Header:			DC.L	0,0			;ln_Succ, ln_Pred
			DC.B	NT_FONT,0		;ln_Type, ln_Pri
			DC.L	FontName		;ln_Name
			DC.W	$0F80			;dfh_FileID
			DC.W	1			;dfh_Revision
			DC.L	0			;dfh_Segment
FontName:		DC.B	"test.font",0
			DCB.B	32-10,0			;dfh_Name is 32 bytes

; struct TextFont, at offset 54 in the DiskFontHeader
Font:			DC.L	0,0			;ln_Succ, ln_Pred
			DC.B	NT_FONT,0		;ln_Type, ln_Pri
			DC.L	FontName		;ln_Name
			DC.L	0			;mn_ReplyPort
			DC.W	0			;mn_Length
			DC.W	9			;tf_YSize
			DC.B	0			;tf_Style
			DC.B	FPF_DISKFONT|FPF_PROPORTIONAL ;tf_Flags
			DC.W	6			;tf_XSize
			DC.W	7			;tf_Baseline
			DC.W	1			;tf_BoldSmear
			DC.W	0			;tf_Accessors
			DC.B	"A","C"			;tf_LoChar, tf_HiChar
			DC.L	CharData		;tf_CharData
			DC.W	2			;tf_Modulo
			DC.L	CharLoc			;tf_CharLoc
			DC.L	CharSpace		;tf_CharSpace
			DC.L	CharKern		;tf_CharKern

; The bit offset and the width of A, B, C and the box.
CharLoc:		DC.W	0,5,5,4,9,3,12,4
CharSpace:		DC.W	6,5,4,5
CharKern:		DC.W	0,1,0,0

; 9 rows of 16 bits: A (5 bits), B (4 bits), C (3 bits) and the box (4 bits).
CharData:
			DC.W	%0010011100111111	;..#..|###.|.##|####
			DC.W	%0101010011001001	;.#.#.|#..#|#..|#..#
			DC.W	%1000111101001001	;#...#|###.|#..|#..#
			DC.W	%1000110011001001	;#...#|#..#|#..|#..#
			DC.W	%1111110011001001	;#####|#..#|#..|#..#
			DC.W	%1000111100111111	;#...#|###.|.##|####
			DC.W	%1000100000000000	;#...#|....|...|....
			DC.W	%0000000000000000	;.....|....|...|....
			DC.W	%0000000000000000	;.....|....|...|....
