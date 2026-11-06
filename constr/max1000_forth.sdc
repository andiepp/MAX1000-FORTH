create_clock -name CLK12M -period 83.333 [get_ports {CLK12M}]
derive_pll_clocks
derive_clock_uncertainty

# PLL c0 (10 MHz) only feeds the ADC hard block; everything else runs on c1 (CPU clock).
# PLL c2 (192 MHz) runs the DDS phase accumulator; its settings cross from c1 through a
# toggle handshake (rtl/dds.v).
set_clock_groups -asynchronous \
    -group [get_clocks {CLK12M}] \
    -group [get_clocks {*pll1|clk[0]}] \
    -group [get_clocks {*pll1|clk[1]}] \
    -group [get_clocks {*pll1|clk[2]}]

# Button, UART and pin inputs are synchronized in RTL; outputs are LEDs, UART and pins.
set_false_path -from [get_ports {USER_BTN UART_RXD D[*] PIO[*]}]
set_false_path -to   [get_ports {LED[*] UART_TXD D[*] PIO[*]}]

# Serial flash at SCK = 10 MHz: MISO is sampled two CPU clocks (50 ns) after the falling
# edge that changes it, MOSI changes two clocks before the rising edge; far from critical.
set_false_path -from [get_ports {F_DO SEN_SDO}]
set_false_path -to   [get_ports {F_S F_CLK F_DI SEN_CS SEN_SPC SEN_SDI}]
