\ Bench demo for the wired test environment: potentiometer on AIN3, button on
\ D6 (pull-up, pressed = low), LEDs on D3/D4/D5. The pot sets the user-LED bar
\ and the brightness of D3 (PWM), D4 lights while D6 is pressed, D5 blinks and
\ a status line comes every 500 ms. Any key stops it.
: pot ( -- mv ) 3 mv@ ;
: bar ( mv -- ) 8 3300 */ 1 swap lshift 1- led! ;
: show ( -- ) ." pot " pot . ." mV  D6 " 6 pin@ . ." user " btn@ . cr ;
: bench-off ( -- ) 0 pwm-off 0 led! 4 low 5 low ;
: bench ( -- )
  4 output 5 output 1000 0 pwm-hz 3 0 pwm-pin
  ms@ begin
    pot dup bar 1000 3300 */ 0 pwm-duty
    6 pin@ 0= 4 pin!
    ms@ over - 499 > if 5 toggle show drop ms@ then
  key? until drop key drop bench-off ;
