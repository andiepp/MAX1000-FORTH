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
variable kcheck      \ sum of the kernel image's cells (set by the cross-compiler)
variable starts      \ starts since the configuration was loaded
variable last-start  \ 0 none, 1 the saved source ran, 2 it was skipped
variable skip1       \ the next start skips the saved source ("restart skip")
variable serving     \ a service answer is being sent
variable counting    \ an answer's payload is being counted, not sent
variable acount
variable state
variable base
variable >in
variable #tib
variable 'a
variable 'n
\ RAM 0x0000..0x3FFF holds code and data, 0x4000..0x5FFF data only (jumps and
\ calls reach 16 KB). The kernel's buffers sit at the top of the data RAM; BUFFER:
\ hands out the rest, 0x4000..0x5BFF.
$5D00 constant tib       \ 82 bytes: the input line
$4000 constant data0     \ the data RAM BUFFER: hands out
$5C00 constant data-end
variable dp2             \ next free byte of the data RAM (COLD resets it)

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

\ Service requests (SOH ... LF, docs/Protocol.md) arrive in the same FIFO as
\ terminal input. SERVICE? answers those at the head of the FIFO; KEY, KEY?, MS
\ and EMIT call it while they wait, so the app gets answers at the prompt and
\ while a program waits, polls KEY? or prints.
: rx? ( -- f ) $8002 @ 1 and 0<> ;            \ a byte is waiting (raw)
: soh? ( -- f ) rx? if $800C @ 1 = else 0 then ;
\ true when a terminal byte is next; a request at the head of the FIFO is served
\ first (deciding on the head byte itself leaves no window for a SOH to slip by).
\ While a word keeps the flash or the accelerometer selected a request cannot be
\ served: it waits at the head, and KEY? answers false instead of looping.
: key? ( -- f )
  serving @ if rx? exit then
  begin service? rx? while
    $800C @ 1 <> if -1 exit then
    $8022 @ 5 and if 0 exit then
  repeat 0 ;
\ KEY reads the terminal, or the saved source while LOAD-SOURCE feeds it
: key ( -- c )
  fcr @ if 0 fcr ! 13 exit then
  fsrc @ if fkey exit then
  begin key? until $8000 @ ;
: tx ( c -- ) begin $8002 @ 2 and 0= while service? repeat $8000 ! ;
\ EMIT: terminal output doubles a 0x01; while an answer is counted it only counts
: emit ( c -- )
  counting @ if drop 1 acount +! exit then
  dup 1 = serving @ 0= and if dup tx then tx ;
: led! ( n -- ) $8004 ! ;              \ 8 user LEDs, bit 0 = LED1
: led@ ( -- n ) $8004 @ ;
: led-mask ( n -- mask )                \ LED n (1..8) as its bit in LED!
  dup 1 9 within 0= if ."  bad led" cr abort then 1- 1 swap lshift ;
: led-on ( n -- ) led-mask led@ or led! ;
: led-off ( n -- ) led-mask invert led@ and led! ;
: led-toggle ( n -- ) led-mask led@ xor led! ;
: btn@ ( -- f ) $8006 @ 1 and 0<> ;    \ user button, true while pressed
: ms@ ( -- n ) $800A @ ;
: ms ( n -- ) ms@ + begin service? dup ms@ - 0< until drop ;

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

\ A pin given to a peripheral belongs to it until the peripheral lets it go: its
\ latch and direction no longer count. Owners: 1..4 PWM 0..3, 5 UART 0, 6 UART 1,
\ 7 SPI 0, 8 SPI 1, 9 I2C, 10 DDS. COLD clears the table (a restart resets the
\ peripherals).
$5E20 constant pin-owner  \ 32 bytes: the owner of each pin, 0 = none
: owner ( pin -- id ) dup pin 2drop pin-owner + c@ ;
\ (free) refuses a pin another owner has; the open words check every new pin
\ first, then release their old pins and claim the new ones, so a refused one
\ leaves everything as it was
: (free) ( pin id -- pin ) over owner ?dup if <> if ."  pin taken" cr abort then else drop then ;
: claim ( pin id -- ) 2dup (free) drop swap pin-owner + c! ;
: (claim) ( pin id -- pin ) over swap claim ;
: release ( id -- ) 32 0 do i pin-owner + c@ over = if 0 i pin-owner + c! then loop drop ;

\ ---------------------------------------------------------------------------
\ PWM: four channels at $8080 + 8c, each on any pin. The period counts
\ 48 MHz clocks (32 bits), the duty is a fraction in 1/65536 (rtl/pwm.v).
\ ---------------------------------------------------------------------------

: pwm ( ch -- a ) dup 4 u< 0= if ."  bad channel" cr abort then 3 lshift $8080 + ;
: pwm-clocks ( ud ch -- ) pwm rot over ! 2 + ! ;
: ud/u ( ud u -- ud' ) >r 0 r@ um/mod r> swap >r um/mod nip r> ;
: pwm-hz ( u ch -- )
  over 0= if ."  bad frequency" cr abort then >r >r $6C00 $02DC r> ud/u r> pwm-clocks ;
: pwm-khz ( u ch -- )
  over 1 24001 within 0= if ."  bad frequency" cr abort then
  >r >r 48000 0 r> um/mod nip 0 r> pwm-clocks ;
: pwm-duty ( permille ch -- ) >r 0 max 1000 min 65535 um* 1000 um/mod nip r> pwm 4 + ! ;
: pwm-pin ( pin ch -- )
  dup pwm drop >r r@ 1+ (free) r@ 1+ release r@ 1+ (claim) $8000 or r> pwm 6 + ! ;
: pwm-off ( ch -- ) dup pwm 6 + 0 swap ! 1+ release ;

\ ---------------------------------------------------------------------------
\ DDS: a square wave on any pin from a 32-bit phase accumulator on its own
\ 192 MHz clock (rtl/dds.v): f = tuning * 192 MHz / 2^32, steps of 0.045 Hz,
\ edges on a 5.2 ns grid. $8060/$8062 tuning (the low half is held until the
\ high half is written), $8064 control (bit 15 on, bits 5..0 pin; read: bit 14
\ = a change not yet taken over). Like a PWM channel it owns its pin while on.
\ ---------------------------------------------------------------------------

: dds-sync ( -- ) begin $8064 @ $4000 and 0= until ;
: dds-tuning ( ud -- ) swap $8060 ! $8062 ! dds-sync ;
46875 constant dds-d                  \ tuning = hz * 2^20 / 46875 at 192 MHz
\ (hz*2^20 + 46875/2) / 46875 by long division, 16 bits at a time
: (dds-hz) ( ud -- )
  over 12 rshift over 4 lshift or >r 12 rshift >r 4 lshift
  r> 0 dds-d um/mod drop
  r> swap dds-d um/mod drop
  dds-d um/mod
  swap 23437 swap dds-d um/mod nip swap dds-tuning ;
: dds-hz ( u -- ) 0 (dds-hz) ;        \ 0..65535 Hz
: dds-khz ( u -- ) 1000 um* (dds-hz) ; \ 0..65535 kHz
: dds-pin ( pin -- ) 10 (free) 10 release 10 (claim) $8000 or $8064 ! dds-sync ;
: dds-off ( -- ) 0 $8064 ! 10 release dds-sync ;

\ ---------------------------------------------------------------------------
\ Serial interfaces on any pin (rtl/uart_port.v, spi_port.v, i2c_port.v), as in
\ ZYNQ-FPGA-FORTH32. UART u = 0, 1 at $80A0 + 16u: 8N1, 1 KB FIFOs; the baud
\ rate is a double number (115200.). SPI s = 0, 1 at $80C0 + 16s: master, modes
\ 0..3, 8 or 16 bits, up to 12 MHz; chip selects are ordinary pins. I2C at
\ $80E0: master, open drain (pull-up resistors needed), clock stretching.
\ ---------------------------------------------------------------------------

variable cid                          \ the owner id of the unit being opened
: uart-a ( u -- a ) dup 2 u< 0= if ."  bad uart" cr abort then 4 lshift $80A0 + ;
: d2/u ( ud -- ud' ) dup 15 lshift rot 1 rshift or swap 1 rshift ;
: m+u ( ud u -- ud' ) rot over + tuck u> rot swap - ;
\ clocks per bit = 48000000 / baud, rounded; both halve until the baud fits in a cell
: uart-div ( ud-baud u -- )
  uart-a >r 2dup or 0= if ."  bad baud" cr abort then
  >r >r $6C00 $02DC r> r>
  begin dup while d2/u >r >r d2/u r> r> repeat drop
  dup >r 1 rshift m+u r> ud/u
  dup $FF00 and if ."  bad baud" cr abort then r@ 6 + ! r> 4 + ! ;
: uart-open ( ud-baud tx rx u -- )
  dup uart-a drop 5 + cid ! cid @ (free) swap cid @ (free) swap
  >r >r cid @ 5 - uart-div r> r> cid @ release
  cid @ (claim) 8 lshift swap cid @ (claim) or $8000 or cid @ 5 - uart-a 8 + ! ;
: uart-close ( u -- ) dup uart-a 8 + 0 swap ! 5 + release ;
: uart-emit ( c u -- ) uart-a begin service? dup 2 + @ 2 and until ! ;
: uart-key? ( u -- f ) uart-a 2 + @ 1 and 0<> ;
: uart-key ( u -- c ) uart-a begin service? dup 2 + @ 1 and until @ $FF and ;
: uart-type ( a n u -- ) >r begin dup while over c@ r@ uart-emit 1 /string repeat 2drop r> drop ;
\ bit 2 transmitter idle, bit 3 a received byte was lost, bit 4 framing error;
\ reading clears bits 3 and 4
: uart-status ( u -- bits ) uart-a 2 + dup @ $1C and 0 rot ! ;

: spi-a ( s -- a ) dup 2 u< 0= if ."  bad spi" cr abort then 4 lshift $80C0 + ;
: spi-open ( sck mosi miso s -- )
  dup spi-a drop 7 + cid ! cid @ (free) >r cid @ (free) >r cid @ (free) r> r>
  cid @ release cid @ (claim) >r cid @ (claim) 8 lshift swap cid @ (claim) or $8000 or
  r> cid @ 7 - spi-a dup >r 10 + ! r> 8 + ! ;
: spi-close ( s -- ) dup spi-a 8 + 0 over ! 0 swap 2 + ! 7 + release ;
: spi-khz ( khz s -- )
  >r dup 1 12001 within 0= if ."  bad frequency" cr abort then
  24000 swap u/mod swap if 1+ then r> spi-a 6 + ! ;
: spi-ctl ( bits mask s -- ) spi-a 4 + dup >r @ swap invert and or r> ! ;
: spi-mode ( mode s -- ) >r 3 and 3 r> spi-ctl ;
: spi-lsb ( f s -- ) >r 0<> 4 and 4 r> spi-ctl ;
: spi-bits ( n s -- )
  >r dup 16 = if drop $10 else 8 <> if ."  bad bits" cr abort then 0 then $10 r> spi-ctl ;
: spi-xfer ( out s -- in ) spi-a tuck ! begin dup 2 + @ 1 and 0= until @ ;
: spi-write ( a n s -- ) >r begin dup while over c@ r@ spi-xfer drop 1 /string repeat 2drop r> drop ;
: spi-read ( a n s -- )
  >r begin dup while 0 r@ spi-xfer >r over r> swap c! 1 /string repeat 2drop r> drop ;

: (i2c) ( cmd -- status )
  $80E0 ! begin $80E0 @ dup 0< while drop repeat
  dup $200 and if ."  i2c timeout" cr abort then ;
: i2c-open ( khz scl sda -- )
  rot dup 1 1001 within 0= if ."  bad frequency" cr abort then
  12000 swap u/mod swap if 1+ then >r
  9 (free) swap 9 (free) swap 9 release
  9 (claim) 8 lshift swap 9 (claim) or $8000 or r> $80E2 ! $80E4 ! ;
: i2c-close ( -- ) 0 $80E4 ! 9 release ;
: i2c-start ( -- ) $100 (i2c) drop ;
: i2c-stop ( -- ) $200 (i2c) drop ;
: i2c-write ( b -- ack? ) $FF and $300 or (i2c) $100 and 0<> ;
: i2c-read ( ack? -- b ) if $400 else $500 then (i2c) $FF and ;
\ start (or a repeated start), then the address byte: 7-bit address, rw 1 = read
: i2c-addr ( addr rw -- ack? ) i2c-start swap 2* or i2c-write ;
: (ack?) ( f -- ) 0= if i2c-stop ."  no ack" cr abort then ;
: i2c! ( b reg addr -- ) 0 i2c-addr (ack?) i2c-write (ack?) i2c-write (ack?) i2c-stop ;
: i2c@ ( reg addr -- b )
  dup >r 0 i2c-addr (ack?) i2c-write (ack?) r> 1 i2c-addr (ack?) 0 i2c-read i2c-stop ;
: i2c-probe ( addr -- f ) 0 i2c-addr i2c-stop ;
: i2c-scan ( -- )
  base @ >r hex 120 8 do i i2c-probe if i . then loop r> base ! cr ;
\ COLD switches every peripheral that owns pins off (a restart has reset them
\ already), so the owner table and the hardware agree
: units-off ( -- )
  4 0 do i pwm-off loop dds-off 2 0 do i uart-close i spi-close loop i2c-close ;

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
\ The temperature sensing diode: a running average of 64 samples in slot 9. Its code
\ falls as the chip warms; TEMP@ interpolates between two points of Intel's table
\ (-40 C = 3798, 96 C = 3510), so it is approximate.
: temp-code@ ( -- u ) 9 adc-ch@ ;
: temp@ ( -- n ) 3798 temp-code@ - 1360 288 */ 400 - ;     \ tenths of a degree C
: .temp ( -- )
  temp@ dup 0< if 45 emit negate then 10 u/mod (u.) 46 emit (u.) ."  C " ;

\ ---------------------------------------------------------------------------
\ LIS3DH accelerometer on its own SPI pins (the flash's SPI master, select bit 1
\ at $8022). ACCEL-ON (run by COLD): 100 Hz, X/Y/Z, block update, high
\ resolution, +-2 g, so one digit is 1 mg.
\ ---------------------------------------------------------------------------

: acc-sel ( -- ) 2 $8022 ! ;
: acc-end ( -- ) 0 $8022 ! ;
: acc@ ( reg -- c ) acc-sel $80 or spi drop 0 spi acc-end ;
: acc! ( c reg -- ) acc-sel spi drop spi drop acc-end ;
: accel? ( -- f ) $0F acc@ $33 = ;                \ WHO_AM_I answers $33
: accel-on ( -- ) $57 $20 acc! $88 $23 acc! ;
: mg ( lo hi -- mg ) 8 lshift or 2/ 2/ 2/ 2/ ;    \ 12 bits, left-justified
: accel@ ( -- x y z )
  acc-sel $E8 spi drop                          \ read OUT_X_L.. with auto-increment
  0 spi 0 spi mg 0 spi 0 spi mg 0 spi 0 spi mg acc-end ;

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

$5E10 constant hdr      \ 16 bytes: the saved source's header
$5C00 constant pbuf     \ 256 bytes: one flash page
$5F00 constant lbuf     \ 256 bytes: the piece of saved source being interpreted
variable fsrc        \ bytes of saved source still to be read by KEY
variable fcr         \ KEY owes a CR (the source did not end in one)
variable loff        \ flash offset of the next piece
variable lpos        \ next byte in LBUF
variable lend        \ bytes in LBUF
variable slen
variable soff
variable s-state     \ the saved source as last checked: 0 none, 1 stored, 2 damaged
variable s-len
variable s-auto
variable s-crclo
variable s-crchi

: hdr@ ( -- ) 0 0 f-read 16 0 do 0 spi hdr i + c! loop f-end ;
: hdr-ok? ( -- f ) hdr @ $4648 = hdr 2 + @ $3631 = and hdr 6 + @ 0= and ;
: text-crc ( len -- lo hi )
  crc-reset 0 1 f-read begin dup while 0 spi crc+ 1- repeat drop f-end crc@ ;
\ read the header and check the text's CRC; sets S-STATE, S-LEN, S-AUTO, S-CRC..
: scan-source ( -- )
  0 s-state ! 0 s-len ! 0 s-auto ! 0 s-crclo ! 0 s-crchi !
  hdr@ hdr-ok? 0= if exit then
  hdr 4 + @ dup s-len ! hdr 8 + @ s-crclo ! hdr 10 + @ s-crchi !
  hdr 12 + @ 1 and s-auto !
  text-crc s-crchi @ = swap s-crclo @ = and if 1 else 2 then s-state ! ;
\ length of the saved source if header and CRC are right, else 0
: source? ( -- len | 0 ) scan-source s-state @ 1 = if s-len @ else 0 then ;
\ The saved source is read in pieces of 256 bytes, so the flash is selected only
\ while a piece is copied: the source itself may use the flash and the accelerometer.
: lfill ( -- )
  fsrc @ 256 umin lend ! 0 lpos !
  loff @ 1 f-read lend @ 0 do 0 spi lbuf i + c! loop f-end
  lend @ loff +! ;
: fkey ( -- c )
  lpos @ lend @ = if lfill then
  lbuf lpos @ + c@ 1 lpos +! -1 fsrc +!
  fsrc @ 0= if dup 13 <> fcr ! then ;
: echo? ( -- f ) fsrc @ fcr @ or 0= ;
: (load) ( len -- ) ?dup if fsrc ! 0 loff ! 0 lpos ! 0 lend ! then ;
: load-source ( -- ) source? (load) ;
: write-hdr ( -- ) 0 0 f-erase hdr 16 0 0 f-prog ;
: make-hdr ( flags -- )
  $4648 hdr ! $3631 hdr 2 + ! slen @ hdr 4 + ! 0 hdr 6 + !
  crc@ hdr 10 + ! hdr 8 + ! hdr 12 + ! 0 hdr 14 + ! ;
\ a received byte within 2 s, or -1
variable dl
: rx-t ( -- c | -1 )
  ms@ 2000 + dl ! begin rx? if $8000 @ exit then dl @ ms@ - 0< until -1 ;
\ n bytes from the serial port into PBUF (and the CRC); false on a timeout
: page-in ( n -- f )
  0 begin 2dup > while
    rx-t dup 0< if drop 2drop 0 exit then
    dup crc+ over pbuf + c! 1+
  repeat 2drop -1 ;
\ len raw bytes from the serial port into the text area, erasing 4 KB sectors
\ as it goes; false on a timeout
: receive ( len -- f )
  slen ! 0 soff ! crc-reset
  begin soff @ slen @ u< while
    soff @ $FFF and 0= if soff @ 1 f-erase then
    slen @ soff @ - 256 umin dup >r       \ unsigned: lengths go up to 65535
    page-in 0= if r> drop 0 exit then
    pbuf r@ soff @ 1 f-prog r> soff +!
  repeat -1 ;
\ STORE prints "ready", then reads len raw bytes (tools/mforth.py --store)
: store ( len flags -- )
  >r 0 0 f-erase ."  ready" cr
  receive 0= if r> drop ."  store timeout" cr scan-source exit then
  r> make-hdr write-hdr
  source? slen @ = if ."  stored " slen @ u. ." bytes" else ."  store failed" then cr ;
: autostart ( f -- )
  source? 0= if drop ."  no saved source" cr exit then
  1 and hdr 12 + ! write-hdr scan-source ;
: erase-source ( -- ) 0 0 f-erase scan-source ;
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
  0 last-start ! scan-source
  s-state @ 1 = s-auto @ and 0= if exit then
  skip1 @ 0 skip1 ! esc? or btn@ or if     \ btn@ after the wait: the button is
    ."  autostart skipped" cr 2 last-start ! exit   \ sampled every 10 ms
  then
  1 last-start ! s-len @ (load) ;

\ ---------------------------------------------------------------------------
\ Service requests (protocol 1 of ZYNQ-FORTH, docs/Protocol.md): SOH, a line,
\ LF; answered "SOH ok <name> <length> LF <payload>" or "SOH err <name>
\ <reason> LF". The payload is generated twice: once to count it, once to send.
\ ---------------------------------------------------------------------------

$5D60 constant rq       \ 64 bytes: a service request
variable rq#         \ request length
variable rq>         \ parse position
variable rqn-a       \ request name
variable rqn-n
variable ea          \ argument kept for the payload
variable en
variable rlen
variable rclo
variable rchi
variable rflag
variable pp
variable s-in        \ status: input bytes, taken once so both passes print the same
variable dacc

: noop ( -- ) ;
: lf ( -- ) 10 emit ;
: hexd ( u -- ) 15 and digit emit ;
: hex4 ( u -- ) dup 12 rshift hexd dup 8 rshift hexd dup 4 rshift hexd hexd ;
: hex8 ( lo hi -- ) hex4 hex4 ;
: =s ( a n a2 n2 -- f ) rot over = if same? else drop 2drop 0 then ;
: hexnum ( a n -- u ) base @ >r hex number? drop r> base ! ;

: rq-line ( -- f )   \ the request text up to LF; false on a 2 s timeout
  0 rq# !
  begin rx-t dup 0< if drop 0 exit then dup 10 <> while
    rq# @ 63 < if rq rq# @ + c! 1 rq# +! else drop then
  repeat drop -1 ;
: rq-c ( -- c ) rq rq> @ + c@ ;
: rq-word ( -- a n )
  begin rq> @ rq# @ < if rq-c 32 = else 0 then while 1 rq> +! repeat
  rq rq> @ +
  begin rq> @ rq# @ < if rq-c 32 <> else 0 then while 1 rq> +! repeat
  rq rq> @ + over - ;
: rq= ( a n -- f ) rqn-a @ rqn-n @ =s ;

: answer ( xt -- )
  base @ >r decimal
  -1 counting ! 0 acount ! dup execute 0 counting !
  1 tx ." ok " rqn-a @ rqn-n @ type space acount @ (u.) lf
  execute r> base ! ;
: answer-n ( xt n -- )   \ the length is known: generate once
  base @ >r decimal
  1 tx ." ok " rqn-a @ rqn-n @ type space (u.) lf
  execute r> base ! ;
: refuse ( a n -- ) 1 tx ." err " rqn-a @ rqn-n @ type space type lf ;

: p-echo ( -- ) ea @ en @ type ;
: p-version ( -- )
  ." firmware max1000-forth 1.4" lf ." protocol 1" lf
  ." kernel " dp0 @ 2/ (u.) space kcheck @ hex4 lf ;
: p-status ( -- )
  ." state running" lf ." pc 0000" lf
  ." input " s-in @ (u.) lf
  ." starts " starts @ (u.) lf
  ." source " s-state @ dup 0= if ." none" then dup 1 = if ." stored" then
  2 = if ." damaged" then lf
  ." length " s-state @ 1 = if s-len @ else 0 then (u.) lf
  ." crc " s-crclo @ s-crchi @ hex8 lf
  ." autostart " s-auto @ if ." on" else ." off" then lf
  ." last " last-start @ dup 0= if ." none" then dup 1 = if ." fed" then
  2 = if ." skipped" then lf ;
: p-words ( -- )
  ." here " here hex4 lf
  latest @ begin dup while
    dup name>xt hex4 space
    dup 2 + c@ dup $E0 and 0= if 45 emit then
    dup $80 and if 105 emit then dup $40 and if 104 emit then
    $20 and if 110 emit then space
    dup name>count type lf
  @ repeat drop ;
: p-stack ( -- )
  sdepth @ dup (u.) 0 begin 2dup > while space dup cells ssnap + @ hex4 1+ repeat
  2drop lf ;
: pwm-ctl ( c -- ctl ) 3 lshift $8086 + @ ;
\ levels and the pins driven now (outputs and peripheral pins) of port p
: port-word ( p -- lo hi ) 3 lshift $8010 + dup @ swap 6 + @ ;
: pwm-word ( -- lo hi )
  0 4 0 do i pwm-ctl $8000 and if 1 i lshift or then loop
  0 pwm-ctl $3F and 4 lshift or 1 pwm-ctl $3F and 10 lshift or
  2 pwm-ctl $3F and 3 pwm-ctl $3F and 6 lshift or ;
\ pin functions: bit 15 on, bits 5..0 the pin; FN2 splits a pins register into
\ its two functions (bits 5..0 and 13..8)
: fnx ( x -- ) space hex4 ;
: fn2 ( pins -- ) dup $803F and fnx dup $8000 and swap 8 rshift $3F and or fnx ;
: p-board ( -- )
  ." board " led@ $FF and btn@ 1 and hex8 lf
  ." ports " 0 port-word hex8 space 1 port-word hex8 lf
  ." pwm " pwm-word hex8 lf
  ." adc" 9 0 do space i adc-ch@ hex4 loop lf
  ." temp " temp-code@ hex4 lf
  ." accel " accel@ rot hex4 space swap hex4 space hex4 lf
  ." dds " $8064 @ hex4 space $8060 @ $8062 @ hex8 lf
  ." fn" $80A8 @ fn2 $80B8 @ fn2
  $80C8 @ dup fn2 $8000 and $80CA @ $3F and or fnx
  $80D8 @ dup fn2 $8000 and $80DA @ $3F and or fnx
  $80E4 @ fn2 lf ;
: p-source ( -- )
  s-len @ 0 1 f-read begin dup while 0 spi emit 1- repeat drop f-end ;
: tx-idle ( -- ) begin $8002 @ 4 and until ;

: rq-restart ( -- )
  rq-word s" skip" =s skip1 !
  ['] noop answer tx-idle 0 $8008 ! ;
: rq-source ( -- )
  scan-source s-state @ 0= if s" none" refuse exit then
  s-state @ 2 = if s" damaged" refuse exit then
  ['] p-source s-len @ answer-n ;
: on/off ( -- f ok? )
  rq-word 2dup s" on" =s if 2drop -1 -1 exit then
  s" off" =s if 0 -1 else 0 0 then ;
\ decimal digits as a number; not ok for anything else or a value above 65535
: dec16 ( a n -- u ok? )
  0 dacc ! begin dup while
    over c@ 48 - dup 9 u> if drop 2drop 0 0 exit then
    dacc @ 10 um* if 2drop 2drop 0 0 exit then
    over + tuck u> if drop 2drop 0 0 exit then dacc !
    1 /string
  repeat 2drop dacc @ -1 ;
: rq-store ( -- )
  rq-word dup 1 6 within 0= if 2drop s" args" refuse exit then
  dec16 0= if drop s" args" refuse exit then rlen !
  rq-word dup 8 <> if 2drop s" args" refuse exit then
  drop dup 4 hexnum rchi ! 4 + 4 hexnum rclo !
  on/off 0= if drop s" args" refuse exit then 1 and rflag !
  0 0 f-erase
  rlen @ receive 0= if scan-source s" timeout" refuse exit then
  crc@ rchi @ = swap rclo @ = and 0= if scan-source s" crc" refuse exit then
  rflag @ make-hdr write-hdr scan-source
  s-state @ 1 = if ['] noop answer else s" flash" refuse then ;
: rq-autostart ( -- )
  on/off 0= if drop s" args" refuse exit then
  scan-source s-state @ 1 = 0= if drop s" none" refuse exit then
  1 and hdr 12 + ! write-hdr scan-source ['] noop answer ;

\ mem <addr> <cells>: RAM cells as hex (4 digits each), for the app's decompiler.
\ Hex address, decimal count 1..256; RAM only ($0000..$5FFF), so no I/O
\ register is read (a read of $8000 or a UART's data register takes a byte).
variable maddr  variable mcells
: p-mem ( -- ) maddr @ mcells @ 0 do dup @ hex4 2 + loop drop lf ;
: rq-mem ( -- )
  rq-word dup 0= if 2drop s" args" refuse exit then
  base @ >r hex number? r> base ! 0= if drop s" args" refuse exit then -2 and maddr !
  rq-word dec16 0= if drop s" args" refuse exit then
  dup 1 257 within 0= if drop s" args" refuse exit then mcells !
  maddr @ mcells @ 2* + $6000 u> maddr @ $6000 u< 0= or if s" range" refuse exit then
  ['] p-mem mcells @ 4 * 1+ answer-n ;

: dispatch ( -- )
  s" echo" rq= if rq-word en ! ea ! ['] p-echo answer exit then
  s" mem" rq= if rq-mem exit then
  s" version" rq= if ['] p-version answer exit then
  s" status" rq= if $800E @ fsrc @ + s-in ! ['] p-status answer exit then
  s" words" rq= if ['] p-words answer exit then
  s" stack" rq= if sdepth @ 0< if s" busy" refuse exit then ['] p-stack answer exit then
  s" board" rq= if ['] p-board answer exit then
  s" source" rq= if rq-source exit then
  s" store" rq= if rq-store exit then
  s" erase" rq= if erase-source ['] noop answer exit then
  s" autostart" rq= if rq-autostart exit then
  s" restart" rq= if rq-restart exit then
  s" unknown" refuse ;
: serve ( -- )
  $8000 @ drop -1 serving !
  rq-line if 0 rq> ! rq-word rqn-n ! rqn-a ! dispatch then
  0 serving ! ;
: service? ( -- )
  serving @ if exit then
  $8022 @ 5 and if exit then        \ a word is talking to the flash or the accelerometer
  begin soh? while serve repeat ;

\ ---------------------------------------------------------------------------
\ Output
\ ---------------------------------------------------------------------------

: cr 13 emit 10 emit ;
: space 32 emit ;
: spaces ( n -- ) begin dup 0> while space 1- repeat drop ;
: type ( a n -- ) begin dup while swap dup c@ emit 1+ swap 1- repeat 2drop ;
: (.") ( -- ) r> dup c@ swap 1+ swap 2dup type + aligned >r ;
: (s") ( -- a n ) r> dup c@ swap 1+ swap 2dup + aligned >r ;
: digit ( u -- c ) dup 9 > if 7 + then 48 + ;
: (u.) ( u -- ) base @ u/mod ?dup if recurse then digit emit ;
: u. ( u -- ) (u.) space ;
: . ( n -- ) dup 0< if 45 emit negate then u. ;
\ .S parks the stack in SBUF (not on the 32-deep return stack), prints it
\ bottom first and pushes it back
$5DA0 constant sbuf     \ 64 bytes: .S parks the stack here
variable sn
: .s ( -- )
  depth dup sn ! 60 emit (u.) 62 emit space
  sn @ begin dup while 1- swap over cells sbuf + ! repeat drop
  sn @ 0 begin 2dup > while dup cells sbuf + @ dup . -rot 1+ repeat 2drop ;
: ? ( a -- ) @ . ;

\ Free code space (below 0x4000) and data RAM; BUFFER: name gives n bytes of data RAM
: unused ( -- n ) $4000 here - ;
: unused-data ( -- n ) data-end dp2 @ - ;
: buffer: ( n "name" -- )      \ the size is rounded up to even: cells stay aligned
  dup unused-data u> if drop ."  data RAM full" cr abort then
  1+ -2 and dp2 @ swap dp2 +! constant ;

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
\ A number with a trailing dot is an unsigned double cell ( lo hi ), for values
\ above 65535 such as baud rates: 115200. ($ prefix for hex as with numbers)
: d*base+ ( lo hi n -- lo' hi' )
  >r base @ * swap base @ um* rot + r> m+u ;
: >dnumber ( lo hi a n -- lo' hi' f )
  begin dup while
    over c@ digit? 0= if drop 2drop 0 exit then
    >r 1 /string r> -rot >r >r d*base+ r> r>
  repeat 2drop -1 ;
: dnumber? ( a n -- lo hi f )
  dup 2 < if 2drop 0 0 0 exit then
  2dup + 1- c@ 46 <> if 2drop 0 0 0 exit then
  1- base @ >r
  over c@ 36 = over 1 > and if 16 base ! 1 /string then
  >r >r 0 0 r> r> >dnumber
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
      drop 2dup dnumber? if
        >r >r 2drop r> r> state @ if swap literal literal then
      else
        2drop type ."  ?" cr abort
      then
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
$5DE0 constant ssnap    \ 40 bytes: the stack at the last prompt (stack request)
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
  dp0 @ dp ! latest0 @ latest ! 0 fsrc ! 0 fcr ! 0 serving ! 0 counting !
  1 starts +! data0 dp2 ! accel-on 32 0 do 0 i pin-owner + c! loop units-off
  decimal 0 state !
  cr ." HF16 Forth on MAX1000" cr
  1 led!
  autoload quit ;
