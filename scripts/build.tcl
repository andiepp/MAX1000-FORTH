# Creates the Quartus project in build/ and runs the full compile.
# Run scripts/build.ps1, which first builds the kernel image (build/forth_lo.hex, forth_hi.hex).
load_package flow

set root  [file normalize [file join [file dirname [info script]] ..]]
set build [file join $root build]
file mkdir $build
cd $build

foreach f {forth_lo.hex forth_hi.hex} {
    if {![file exists [file join $build $f]]} {
        puts "ERROR: build/$f missing - build the kernel first (scripts/build.ps1)"
        exit 1
    }
}

set top max1000_forth
project_new $top -overwrite

set_global_assignment -name FAMILY "MAX 10"
set_global_assignment -name DEVICE 10M08SAU169C8G
set_global_assignment -name TOP_LEVEL_ENTITY $top
set_global_assignment -name PROJECT_OUTPUT_DIRECTORY output_files
set_global_assignment -name VCCA_USER_VOLTAGE 3.3V
set_global_assignment -name NOMINAL_CORE_SUPPLY_VOLTAGE 3.0V
set_global_assignment -name STRATIX_DEVICE_IO_STANDARD "3.3-V LVTTL"
set_global_assignment -name ENABLE_OCT_DONE ON
set_global_assignment -name CRC_ERROR_OPEN_DRAIN OFF
set_global_assignment -name RESERVE_ALL_UNUSED_PINS_WEAK_PULLUP "AS INPUT TRI-STATED WITH WEAK PULL-UP"
# RAM contents (the kernel) must be part of the flash image
set_global_assignment -name INTERNAL_FLASH_UPDATE_MODE "SINGLE IMAGE WITH ERAM"

foreach f {max1000_forth.v hf16_cpu.v fifo_bram.v uart.v pwm.v adc_scan.v spi_flash.v dds.v} {
    # (spi_flash.v drives the serial flash and the LIS3DH accelerometer)
    set_global_assignment -name VERILOG_FILE [file join $root rtl $f]
}

# Modular ADC control core, used in place from the Quartus installation
set adcip [file normalize [file join $::quartus(quartus_rootpath) .. ip altera altera_modular_adc control]]
foreach f {altera_modular_adc_control.v altera_modular_adc_control_fsm.v altera_modular_adc_control_avrg_fifo.v
           chsel_code_converter_sw_to_hw.v fiftyfivenm_adcblock_primitive_wrapper.v fiftyfivenm_adcblock_top_wrapper.v} {
    set_global_assignment -name VERILOG_FILE [file join $adcip $f]
}

set_global_assignment -name SDC_FILE [file join $root constr max1000_forth.sdc]
set_parameter -name INIT_LO [file join $build forth_lo.hex]
set_parameter -name INIT_HI [file join $build forth_hi.hex]

# Pins (TEI0001-02)
set pins {
    CLK12M   H6   USER_BTN E6   UART_TXD B4   UART_RXD A4
    LED[0]   A8   LED[1]   A9   LED[2]   A11  LED[3]   A10
    LED[4]   B10  LED[5]   C9   LED[6]   C10  LED[7]   D8
    D[0]     H8   D[1]     K10  D[2]     H5   D[3]     H4   D[4]  J1
    D[5]     J2   D[6]     L12  D[7]     J12  D[8]     J13  D[9]  K11
    D[10]    K12  D[11]    J10  D[12]    H10  D[13]    H13  D[14] G12
    PIO[0]   M3   PIO[1]   L3   PIO[2]   M2   PIO[3]   M1
    PIO[4]   N3   PIO[5]   N2   PIO[6]   K2   PIO[7]   K1
    F_S      B3   F_CLK    A3   F_DI     A2   F_DO     B2
    SEN_CS   L5   SEN_SPC  J6   SEN_SDI  J7   SEN_SDO  K5
}
foreach {sig pin} $pins {
    set_location_assignment PIN_$pin -to $sig
    set_instance_assignment -name IO_STANDARD "3.3-V LVTTL" -to $sig
}
set_instance_assignment -name IO_STANDARD "3.3 V SCHMITT TRIGGER" -to USER_BTN

# Weak pull-ups on D0..D14 and PIO1..PIO8 (build.ps1 -PullUps). The MAX 10 sets them in
# the configuration; Forth cannot switch them. Off by default: with the bench's LEDs on
# D3..D5 (1 k to GND) a pull-up would leave them faintly lit while the pin is an input.
if {[info exists ::env(MAX1000_PULLUPS)] && $::env(MAX1000_PULLUPS) eq "1"} {
    puts "weak pull-ups on D\[*\] and PIO\[*\]"
    set_instance_assignment -name WEAK_PULL_UP_RESISTOR ON -to D[*]
    set_instance_assignment -name WEAK_PULL_UP_RESISTOR ON -to PIO[*]
}

export_assignments

if {[catch {execute_flow -compile} err]} {
    puts "ERROR: compile failed: $err"
    project_close
    exit 1
}
project_close
puts "BUILD OK: [file join $build output_files $top.sof]"
