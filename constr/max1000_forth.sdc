create_clock -name CLK12M -period 83.333 [get_ports {CLK12M}]
derive_pll_clocks
derive_clock_uncertainty

# PLL c0 (10 MHz) only feeds the ADC hard block; everything else runs on c1 (CPU clock).
set_clock_groups -asynchronous \
    -group [get_clocks {CLK12M}] \
    -group [get_clocks {*pll1|clk[0]}] \
    -group [get_clocks {*pll1|clk[1]}]

# Button, UART and pin inputs are synchronized in RTL; outputs are LEDs, UART and pins.
set_false_path -from [get_ports {USER_BTN UART_RXD D[*] PIO[*]}]
set_false_path -to   [get_ports {LED[*] UART_TXD D[*] PIO[*]}]
