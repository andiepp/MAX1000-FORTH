\ HF16 Forth kernel, cross-compiled by tools/hf16.py.
\
\ Host rules (see tools/hf16.py): outside definitions only numbers, : :inline
\ variable constant create allot , c, immediate and comments are understood.
\ Inside definitions primitives are compiled inline; IF ELSE THEN BEGIN UNTIL
\ AGAIN WHILE REPEAT DO LOOP +LOOP I UNLOOP RECURSE ." [CHAR] ['] [OP] are
\ handled by the host. Calls to words defined later are allowed.

\ ---------------------------------------------------------------------------
\ Primitives as target words. :INLINE words are copied into new definitions
\ by the target compiler (needed for words that touch the return stack).
\ ---------------------------------------------------------------------------

:inline dup dup ;          :inline drop drop ;        :inline swap swap ;
:inline over over ;        :inline nip nip ;          :inline >r >r ;
:inline r> r> ;            :inline r@ r@ ;            :inline + + ;
:inline - - ;              :inline and and ;          :inline or or ;
:inline xor xor ;          :inline = = ;              :inline < < ;
:inline u< u< ;            :inline rshift rshift ;    :inline lshift lshift ;
:inline * * ;              :inline invert invert ;    :inline 1+ 1+ ;
:inline 1- 1- ;            :inline 0= 0= ;            :inline 0< 0< ;
:inline 2/ 2/ ;            :inline negate negate ;    :inline @ @ ;
:inline c@ c@ ;            :inline ! ! ;              :inline c! c! ;
:inline depth depth ;      :inline i r@ ;
:inline unloop r> drop r> drop ;

\ EXIT cannot be an :INLINE word (inline copying stops at the first EXIT)
: exit [op] exit , ; immediate

: execute ( xt -- ) >r ;

\ ---------------------------------------------------------------------------
\ Stack and arithmetic
\ ---------------------------------------------------------------------------

: rot ( a b c -- b c a ) >r swap r> swap ;
: -rot ( a b c -- c a b ) swap >r swap r> ;
: tuck ( a b -- b a b ) swap over ;
: 2dup ( a b -- a b a b ) over over ;
: 2drop ( a b -- ) drop drop ;
: ?dup ( x -- x x | 0 ) dup if dup then ;
: <> ( a b -- f ) = invert ;
: > ( a b -- f ) swap < ;
: u> ( a b -- f ) swap u< ;
: 0<> ( x -- f ) 0= invert ;
: 0> ( n -- f ) 0 > ;
: 2* ( n -- n*2 ) dup + ;
: abs ( n -- u ) dup 0< if negate then ;
: min ( a b -- min ) 2dup > if swap then drop ;
: max ( a b -- max ) 2dup < if swap then drop ;
: umin ( u1 u2 -- u ) 2dup u> if swap then drop ;
: +! ( n addr -- ) dup >r @ + r> ! ;
: cell+ ( a -- a+2 ) 2 + ;
: cells ( n -- n*2 ) 2* ;
: aligned ( a -- a' ) 1+ -2 and ;
: /string ( a n k -- a+k n-k ) rot over + -rot - ;

\ Unsigned 16/16 division by shift and subtract: ( d q r ) during the loop
: u/mod ( u d -- rem quot )
  swap 0
  16 0 do
    over 0< if 2* 1+ else 2* then
    swap 2* swap
    rot 2dup u< if -rot else tuck - rot 1+ swap then
  loop
  rot drop swap ;

\ Symmetric signed division
: /mod ( n d -- rem quot )
  2dup xor >r over >r
  abs swap abs swap u/mod
  r> 0< if swap negate swap then
  r> 0< if negate then ;
: / ( n d -- quot ) /mod nip ;
: mod ( n d -- rem ) /mod drop ;

\ Mixed precision: a double cell is ( low high ) with the high cell on top
: um* ( u1 u2 -- ud ) 2dup * -rot (um*) ;

\ Unsigned 32/16 division, the quotient must fit in 16 bits: ( d lo hi ) during
\ the loop, the bit shifted out of HI is kept on the return stack
: um/mod ( ud u -- rem quot )
  -rot
  16 0 do
    dup 0< >r 2* over 0< if 1+ then swap 2* swap
    rot 2dup u< 0= r> or if tuck - rot 1+ swap else -rot then
  loop
  rot drop swap ;

\ n1*n2/n3 with a 32-bit intermediate product, symmetric like /
: */ ( n1 n2 n3 -- n4 )
  >r 2dup xor r@ xor >r
  abs swap abs um*
  r> r> swap >r abs um/mod nip
  r> 0< if negate then ;

: within ( n lo hi -- f ) over - >r - r> u< ;

\ DO LOOP runtime. Return stack inside a loop: index on top, then limit.
: (loop) ( -- done? ) r> r> 1+ dup r@ = swap >r swap >r ;
: (+loop) ( n -- done? ) r> swap r> + dup r@ < invert swap >r swap >r ;
: j ( -- n ) r> r> r> r@ swap >r swap >r swap >r ;

\ ---------------------------------------------------------------------------
\ Variables
\ ---------------------------------------------------------------------------

variable dp          \ next free byte (set by the cross-compiler)
variable latest      \ newest header (set by the cross-compiler)
variable dp0         \ their values for a fresh kernel (set by the cross-compiler,
variable latest0     \ restored by COLD)
variable state
variable base
variable >in
variable #tib
variable 'a
variable 'n
create tib 82 allot

: here ( -- a ) dp @ ;
: allot ( n -- ) dp +! ;
: align ( -- ) here aligned dp ! ;
: , ( x -- ) align here ! 2 allot ;
: c, ( c -- ) here c! 1 allot ;
: hex 16 base ! ;
: decimal 10 base ! ;

\ ---------------------------------------------------------------------------
\ Board I/O
\ ---------------------------------------------------------------------------

: key? ( -- f ) $8002 @ 1 and 0<> ;
\ KEY reads the terminal, or the saved source while LOAD-SOURCE feeds it
: key ( -- c )
  fcr @ if 0 fcr ! 13 exit then
  fsrc @ if fkey exit then
  begin key? until $8000 @ ;
: emit ( c -- ) begin $8002 @ 2 and until $8000 ! ;
: led! ( n -- ) $8004 ! ;              \ 8 user LEDs, bit 0 = LED1
: led@ ( -- n ) $8004 @ ;
: btn@ ( -- f ) $8006 @ 1 and 0<> ;    \ user button, true while pressed
: ms@ ( -- n ) $800A @ ;
: ms ( n -- ) ms@ + begin dup ms@ - 0< until drop ;

\ ---------------------------------------------------------------------------
\ Digital pins. Pin numbers: 0..14 = D0..D14 (Arduino header), 16..23 =
\ PIO_01..PIO_08 (Pmod). Pin n is bit n%16 of port n/16 at $8010+8*(n/16):
\ +0 pin levels, +2 output latch, +4 direction (1 = output). Every pin is an
\ input after reset (no pull-ups: an open input reads at random).
\ ---------------------------------------------------------------------------

: bset ( mask a -- ) dup @ rot or swap ! ;
: bclr ( mask a -- ) dup @ rot invert and swap ! ;
: pin ( n -- mask a )
  dup 0 15 within over 16 24 within or 0= if ."  bad pin" cr abort then
  dup 15 and 1 swap lshift swap 4 rshift 3 lshift $8010 + ;
: output ( n -- ) pin 4 + bset ;
: input ( n -- ) pin 4 + bclr ;
: high ( n -- ) pin 2 + bset ;
: low ( n -- ) pin 2 + bclr ;
: toggle ( n -- ) pin 2 + dup @ rot xor swap ! ;
: pin@ ( n -- f ) pin @ and 0<> ;
: pin! ( f n -- ) swap if high else low then ;

\ ---------------------------------------------------------------------------
\ PWM: four channels at $8080 + 8c, each on any pin. The period counts
\ 40 MHz clocks (32 bits), the duty is a fraction in 1/65536 (rtl/pwm.v).
\ ---------------------------------------------------------------------------

: pwm ( ch -- a ) dup 4 u< 0= if ."  bad channel" cr abort then 3 lshift $8080 + ;
: pwm-clocks ( ud ch -- ) pwm rot over ! 2 + ! ;
: ud/u ( ud u -- ud' ) >r 0 r@ um/mod r> swap >r um/mod nip r> ;
: pwm-hz ( u ch -- )
  over 0= if ."  bad frequency" cr abort then >r >r $5A00 $0262 r> ud/u r> pwm-clocks ;
: pwm-khz ( u ch -- )
  over 1 20001 within 0= if ."  bad frequency" cr abort then
  >r >r 40000 0 r> um/mod nip 0 r> pwm-clocks ;
: pwm-duty ( permille ch -- ) >r 0 max 1000 min 65535 um* 1000 um/mod nip r> pwm 4 + ! ;
: pwm-pin ( pin ch -- ) >r dup pin 2drop $8000 or r> pwm 6 + ! ;
: pwm-off ( ch -- ) pwm 6 + 0 swap ! ;

\ ---------------------------------------------------------------------------
\ Analog inputs: ADC1 of the MAX 10, 12 bits, 0..3.3 V (internal reference =
\ VCCA), all channels converted continuously (rtl/adc_scan.v). ADC channel c
\ (0..8) reads at $8040+2c. Inputs by header label: 0..7 = AIN0..AIN7,
\ 8 = AIN (the dedicated analog input); AIN maps them to ADC channels.
\ ---------------------------------------------------------------------------

: adc-ch@ ( c -- u ) 15 and 2* $8040 + @ ;
create ain 8 c, 2 c, 5 c, 1 c, 3 c, 4 c, 7 c, 6 c, 0 c,
: adc@ ( n -- u )
  dup 9 u< 0= if ."  bad input" cr abort then ain + c@ adc-ch@ ;
: mv@ ( n -- mv ) adc@ 3300 4096 */ ;

\ ---------------------------------------------------------------------------
\ Serial flash (Winbond W25Q64, 8 MB) through the SPI master at $8020 and the
\ CRC-32 unit at $8024 (rtl/spi_flash.v). Flash addresses have 24 bits and are
\ given as ( lo hi ): lo = bits 15..0, hi = bits 23..16.
\ ---------------------------------------------------------------------------

: spi ( c -- c' ) $8020 ! begin $8022 @ 2 and 0= until $8020 @ ;
: spi-cs ( f -- ) 1 and $8022 ! ;
: f-cmd ( c -- ) 1 spi-cs spi drop ;
: f-end ( -- ) 0 spi-cs ;
: f-adr ( lo hi -- ) spi drop dup 8 rshift spi drop spi drop ;
: f-wait ( -- ) begin 5 f-cmd 0 spi f-end 1 and 0= until ;
: f-wren ( -- ) 6 f-cmd f-end ;
: f-read ( lo hi -- ) 3 f-cmd f-adr ;          \ then 0 SPI per byte, F-END
: f-erase ( lo hi -- ) f-wren $20 f-cmd f-adr f-end f-wait ;     \ 4 KB sector
: f-erase64 ( lo hi -- ) f-wren $D8 f-cmd f-adr f-end f-wait ;   \ 64 KB block
\ program n bytes from a; they must stay within one 256-byte page
: f-prog ( a n lo hi -- )
  f-wren 2 f-cmd f-adr
  begin dup while over c@ spi drop 1 /string repeat 2drop f-end f-wait ;
: flash-id ( -- mfr type size ) $9F f-cmd 0 spi 0 spi 0 spi f-end ;
: crc-reset ( -- ) 0 $8024 ! ;
: crc+ ( c -- ) $8026 ! ;
: crc@ ( -- lo hi ) $8028 @ $802A @ ;

\ ---------------------------------------------------------------------------
\ Saved source. The text (at most 65535 bytes, lines end in CR) is in the 64 KB
\ block at $010000, a 16-byte header in the 4 KB sector at 0: magic "HF16",
\ length (32 bits), CRC-32 of the text, flags (bit 0 = autostart), all
\ little-endian. STORE takes the text from the serial port (tools/mforth.py
\ --store). At power-up and after Ctrl-R, COLD runs it if autostart is on,
\ unless the user button is held or an ESC arrives within 200 ms.
\ ---------------------------------------------------------------------------

create hdr 16 allot
create pbuf 256 allot
variable fsrc        \ bytes of saved source still to be read by KEY
variable fcr         \ KEY owes a CR (the source did not end in one)
variable slen
variable soff

: hdr@ ( -- ) 0 0 f-read 16 0 do 0 spi hdr i + c! loop f-end ;
: hdr-ok? ( -- f ) hdr @ $4648 = hdr 2 + @ $3631 = and hdr 6 + @ 0= and ;
: text-crc ( len -- lo hi )
  crc-reset 0 1 f-read begin dup while 0 spi crc+ 1- repeat drop f-end crc@ ;
\ length of the saved source if header and CRC are right, else 0
: source? ( -- len | 0 )
  hdr@ hdr-ok? 0= if 0 exit then
  hdr 4 + @ dup text-crc hdr 10 + @ = swap hdr 8 + @ = and 0= if drop 0 then ;
: fkey ( -- c ) -1 fsrc +! 0 spi fsrc @ 0= if f-end dup 13 <> fcr ! then ;
: echo? ( -- f ) fsrc @ fcr @ or 0= ;
: load-source ( -- ) source? ?dup if fsrc ! 0 1 f-read then ;
: write-hdr ( -- ) 0 0 f-erase hdr 16 0 0 f-prog ;
\ STORE prints "ready" once the flash is erased, then reads len raw bytes
: store ( len flags -- )
  >r slen ! 0 soff !
  0 0 f-erase 0 1 f-erase64 crc-reset
  ."  ready" cr
  begin soff @ slen @ u< while
    slen @ soff @ - 256 umin dup >r       \ unsigned: lengths go up to 65535
    0 do key dup crc+ pbuf i + c! loop
    pbuf r@ soff @ 1 f-prog r> soff +!
  repeat
  $4648 hdr ! $3631 hdr 2 + ! slen @ hdr 4 + ! 0 hdr 6 + !
  crc@ hdr 10 + ! hdr 8 + ! r> hdr 12 + ! 0 hdr 14 + !
  write-hdr
  source? slen @ = if ."  stored " slen @ u. ." bytes" else ."  store failed" then cr ;
: autostart ( f -- )
  source? 0= if drop ."  no saved source" cr exit then
  1 and hdr 12 + ! write-hdr ;
: erase-source ( -- ) 0 0 f-erase ;
: .source ( -- )
  source? ?dup if
    0 1 f-read
    begin dup while 0 spi dup 13 = if drop cr else emit then 1- repeat
    drop f-end
  then ;
\ an ESC within 200 ms ($800C shows the next received byte without taking it)
: esc? ( -- f )
  ms@ 200 + begin
    key? if $800C @ 27 = if $8000 @ 2drop -1 exit then then
    dup ms@ - 0<
  until drop 0 ;
: autoload ( -- )
  source? 0= if exit then hdr 12 + @ 1 and 0= if exit then
  esc? btn@ or if ."  autostart skipped" cr exit then   \ btn@ after the wait:
  \ the button is sampled every 10 ms and reads "not pressed" until the first sample
  load-source ;

\ ---------------------------------------------------------------------------
\ Output
\ ---------------------------------------------------------------------------

: cr 13 emit 10 emit ;
: space 32 emit ;
: spaces ( n -- ) begin dup 0> while space 1- repeat drop ;
: type ( a n -- ) begin dup while swap dup c@ emit 1+ swap 1- repeat 2drop ;
: (.") ( -- ) r> dup c@ swap 1+ swap 2dup type + aligned >r ;
: digit ( u -- c ) dup 9 > if 7 + then 48 + ;
: (u.) ( u -- ) base @ u/mod ?dup if recurse then digit emit ;
: u. ( u -- ) (u.) space ;
: . ( n -- ) dup 0< if 45 emit negate then u. ;
\ .S parks the stack in SBUF (not on the 32-deep return stack), prints it
\ bottom first and pushes it back
create sbuf 64 allot
variable sn
: .s ( -- )
  depth dup sn ! 60 emit (u.) 62 emit space
  sn @ begin dup while 1- swap over cells sbuf + ! repeat drop
  sn @ 0 begin 2dup > while dup cells sbuf + @ dup . -rot 1+ repeat 2drop ;
: ? ( a -- ) @ . ;

\ ---------------------------------------------------------------------------
\ Parsing
\ ---------------------------------------------------------------------------

: more? ( -- f ) >in @ #tib @ < ;
: in-char ( -- c ) tib >in @ + c@ ;
: parse-name ( -- a n )
  begin more? if in-char 33 < else 0 then while 1 >in +! repeat
  tib >in @ +
  begin more? if in-char 32 > else 0 then while 1 >in +! repeat
  tib >in @ + over - ;
: parse ( c -- a n )
  >r more? if in-char 32 = if 1 >in +! then then
  tib >in @ +
  begin more? if in-char r@ <> else 0 then while 1 >in +! repeat
  r> drop
  tib >in @ + over -
  more? if 1 >in +! then ;

\ ---------------------------------------------------------------------------
\ Dictionary search
\ header: link, count byte (bits 4..0 length, $80 immediate, $40 hidden,
\ $20 inline), name, padding to even
\ ---------------------------------------------------------------------------

: upper ( c -- c' ) dup 97 < invert over 122 > invert and if 32 - then ;
: same? ( a1 a2 n -- f )
  begin dup while
    >r over c@ upper over c@ upper <> if r> drop 2drop 0 exit then
    1+ swap 1+ swap r> 1-
  repeat drop 2drop -1 ;
: name>count ( h -- a n ) 2 + dup 1+ swap c@ 31 and ;
: name>xt ( h -- xt ) 2 + dup c@ 31 and + 1+ aligned ;
: match? ( h -- f )
  dup 2 + c@ dup 64 and if 2drop 0 exit then
  31 and 'n @ = if 3 + 'a @ 'n @ same? exit then
  drop 0 ;
: find-name ( a n -- h | 0 )
  'n ! 'a ! latest @
  begin dup while dup match? if exit then @ repeat ;

\ ---------------------------------------------------------------------------
\ Numbers: optional - then $ (hex) prefix, digits in BASE
\ ---------------------------------------------------------------------------

: digit? ( c -- n f )
  upper 48 - dup 9 > if 7 - dup 10 < if drop -1 then then
  dup base @ u< ;
: >number ( acc a n -- acc' a' n' )
  begin
    dup 0= if exit then
    over c@ digit? 0= if drop exit then
    >r rot base @ * r> + -rot 1 /string
  again ;
: number? ( a n -- x f )
  base @ >r
  over c@ 45 = over 1 > and dup >r if 1 /string then
  over c@ 36 = over 1 > and if 16 base ! 1 /string then
  0 -rot >number nip 0=
  r> if swap negate swap then
  r> base ! ;

\ ---------------------------------------------------------------------------
\ Compiler
\ ---------------------------------------------------------------------------

: compile-call ( xt -- ) 2/ $4000 or , ;
: inline, ( xt -- ) begin dup @ dup [op] exit <> while , 2 + repeat 2drop ;
: compile, ( xt -- ) compile-call ;
: literal ( n -- )
  dup 0< if invert $8000 or , [op] invert , else $8000 or , then ; immediate
: cmove ( src dst n -- )
  begin dup while >r over c@ over c! 1+ swap 1+ swap r> 1- repeat drop 2drop ;
: header ( a n -- )
  align here >r
  latest @ ,
  dup c,
  here swap dup allot cmove
  align
  r> latest ! ;
: flags! ( mask -- ) latest @ 2 + dup c@ rot or swap c! ;
: immediate $80 flags! ;
: hide $40 flags! ;
: reveal latest @ 2 + dup c@ $BF and swap c! ;
: [ 0 state ! ; immediate
: ] -1 state ! ;
: : parse-name header hide ] ;
: ; [op] exit , reveal 0 state ! ; immediate
: create parse-name header here 4 + literal [op] exit , ;
: variable create 0 , ;
: constant ( n -- ) parse-name header literal [op] exit , ;
: ' ( -- xt | 0 ) parse-name find-name dup if name>xt then ;
: ['] ' literal ; immediate
: recurse latest @ name>xt compile-call ; immediate
: ( 41 parse 2drop ; immediate
: \ #tib @ >in ! ; immediate
: ." ['] (.") compile-call 34 parse dup c, here swap dup allot cmove align ; immediate
: .( 41 parse type ; immediate

\ Control structures. Branch placeholders carry their opcode ($2000 0branch,
\ $0000 jump); >resolve ORs in the target word address.
: >resolve ( orig -- ) dup @ here 2/ or swap ! ;
: if here $2000 , ; immediate
: else here 0 , swap >resolve ; immediate
: then >resolve ; immediate
: begin here ; immediate
: until 2/ $2000 or , ; immediate
: again 2/ , ; immediate
: while here $2000 , swap ; immediate
: repeat 2/ , >resolve ; immediate
: do ['] swap inline, ['] >r inline, ['] >r inline, here ; immediate
: loop ['] (loop) compile-call 2/ $2000 or , ['] unloop inline, ; immediate
: +loop ['] (+loop) compile-call 2/ $2000 or , ['] unloop inline, ; immediate

\ ---------------------------------------------------------------------------
\ Interpreter
\ ---------------------------------------------------------------------------

: words ( -- )
  latest @ begin dup while dup name>count type space @ repeat drop cr ;

: abort ( -- )
  fsrc @ fcr @ or if 0 fsrc ! 0 fcr ! f-end ."  (in the saved source)" cr then
  begin depth while drop repeat quit ;

\ The data stack has 32 entries and wraps silently. Looking up a word needs up
\ to 9 more (find-name -> same? -> upper), so more than 20 user items count as
\ overflow; a wrapped-around depth (above 28) means underflow.
: ?stack ( -- )
  depth 28 > if ."  stack underflow" cr abort then
  depth 20 > if ."  stack overflow" cr abort then ;

: interpret-word ( a n -- )
  2dup find-name ?dup if
    nip nip dup name>xt swap 2 + c@      ( xt count )
    state @ if
      dup $80 and if drop execute
      else $20 and if inline, else compile, then then
    else drop execute then
  else
    2dup number? if
      nip nip state @ if literal then
    else
      drop type ."  ?" cr abort
    then
  then ;

: interpret ( -- )
  begin parse-name dup while interpret-word ?stack repeat 2drop ;

: accept-line ( -- )
  0 #tib ! 0 >in !
  begin key dup 13 = over 10 = or 0= while
    dup 8 = over 127 = or if
      drop #tib @ if -1 #tib +! echo? if 8 emit space 8 emit then then
    else
      #tib @ 80 < if echo? if dup emit then tib #tib @ + c! 1 #tib +! else drop then
    then
  repeat drop echo? if space then ;

\ SNAP copies the data stack into SSNAP (bottom first) before each line, for the
\ app's stack view. SDEPTH is -1 while the copy is in progress.
create ssnap 40 allot
variable sdepth
: snap ( -- )
  -1 sdepth ! depth dup >r
  begin dup while 1- swap over cells ssnap + ! repeat drop
  r@ 0 begin 2dup > while dup cells ssnap + @ -rot 1+ repeat 2drop
  r> sdepth ! ;

: quit ( -- )
  0 state !
  begin snap accept-line interpret echo? if state @ 0= if ."  ok" then cr then again ;

: cold ( -- )
  dp0 @ dp ! latest0 @ latest ! 0 fsrc ! 0 fcr !
  decimal 0 state !
  cr ." HF16 Forth on MAX1000" cr
  1 led!
  autoload quit ;
