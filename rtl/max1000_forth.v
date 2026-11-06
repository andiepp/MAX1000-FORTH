// MAX1000-FORTH: the HF16 Forth CPU (from ZYNQ-FORTH) as a stand-alone system on the
// Trenz MAX1000 (TEI0001-02, 10M08SAU169C8G). The kernel image is part of the FPGA
// configuration; the CPU talks to the host over the FTDI channel-B UART (COM port).
// Machine model, instruction set and I/O map: docs/design.md.

`default_nettype none

module max1000_forth #(
    // CPU clock = 12 MHz * PLL_MUL / PLL_DIV = 48 MHz. The I/O read multiplexer works on
    // the registered address (io_addr), so it is not behind the stack RAM -> multiplier ->
    // ALU -> next address path.
    parameter integer PLL_MUL = 4,
    parameter integer PLL_DIV = 1,
    parameter integer BAUD    = 115_200,
    parameter         INIT_LO = "forth_lo.hex",   // kernel image, low bytes of each cell
    parameter         INIT_HI = "forth_hi.hex"    //               high bytes
) (
    input  wire        CLK12M,       // H6
    input  wire        USER_BTN,     // E6, low while pressed
    output reg  [7:0]  LED,          // user LEDs, active high
    output wire        UART_TXD,     // B4 -> FTDI BDBUS1
    input  wire        UART_RXD,     // A4 <- FTDI BDBUS0
    inout  wire [14:0] D,            // Arduino header D0..D14
    inout  wire [7:0]  PIO,          // Pmod PIO_01..PIO_08
    output wire        F_S,          // serial flash: chip select (low active)
    output wire        F_CLK,        //               clock
    output wire        F_DI,         //               data into the flash
    input  wire        F_DO,         //               data out of the flash
    output wire        SEN_CS,       // LIS3DH accelerometer: chip select (low active)
    output wire        SEN_SPC,      //                       clock
    output wire        SEN_SDI,      //                       data into the sensor
    input  wire        SEN_SDO       //                       data out of the sensor
);

    localparam integer CLK_HZ = 12_000_000 * PLL_MUL / PLL_DIV;

    // ------------------------------------------------------------------
    // Clocks: c0 = 10 MHz for the ADC, c1 = CPU clock, c2 = 192 MHz for the DDS
    // ------------------------------------------------------------------
    wire [4:0] pll_clk;
    wire       pll_locked;
    altpll #(
        .bandwidth_type          ("AUTO"),
        .clk0_divide_by          (6),
        .clk0_duty_cycle         (50),
        .clk0_multiply_by        (5),
        .clk0_phase_shift        ("0"),
        .clk1_divide_by          (PLL_DIV),
        .clk1_duty_cycle         (50),
        .clk1_multiply_by        (PLL_MUL),
        .clk1_phase_shift        ("0"),
        .clk2_divide_by          (1),                 // c2 = 192 MHz for the DDS
        .clk2_duty_cycle         (50),
        .clk2_multiply_by        (16),
        .clk2_phase_shift        ("0"),
        .compensate_clock        ("CLK1"),
        .inclk0_input_frequency  (83333),
        .intended_device_family  ("MAX 10"),
        .lpm_type                ("altpll"),
        .operation_mode          ("NORMAL"),
        .pll_type                ("AUTO"),
        .port_areset             ("PORT_UNUSED"),
        .port_inclk0             ("PORT_USED"),
        .port_inclk1             ("PORT_UNUSED"),
        .port_locked             ("PORT_USED"),
        .port_clk0               ("PORT_USED"),
        .port_clk1               ("PORT_USED"),
        .port_clk2               ("PORT_USED"),
        .port_clk3               ("PORT_UNUSED"),
        .port_clk4               ("PORT_UNUSED"),
        .width_clock             (5)
    ) u_pll (
        .inclk  ({1'b0, CLK12M}),
        .clk    (pll_clk),
        .locked (pll_locked)
    );

    wire clk = pll_clk[1];

    // power-on reset: held until the PLL has locked and 255 clocks more
    reg  [7:0] por = 8'd0;
    wire       sys_rst = ~&por;
    always @(posedge clk)
        if (!pll_locked)  por <= 8'd0;
        else if (sys_rst) por <= por + 8'd1;

    // ------------------------------------------------------------------
    // UART. Ctrl-R (0x12) from the host restarts the CPU and is not passed on.
    // ------------------------------------------------------------------
    localparam integer CLKS_PER_BIT = (CLK_HZ + BAUD / 2) / BAUD;

    wire [7:0] urx_data;
    wire       urx_valid;
    uart_rx #(.CLKS_PER_BIT(CLKS_PER_BIT)) u_urx (
        .clk(clk), .rst(sys_rst), .rxd(UART_RXD), .data(urx_data), .valid(urx_valid)
    );

    // also a store to 0x8008 (the kernel's answer to a "restart" request)
    reg  restart = 1'b0;
    wire sw_restart;
    always @(posedge clk)
        restart <= (urx_valid && urx_data == 8'h12) || sw_restart;

    wire cpu_rst = sys_rst | restart;

    // ------------------------------------------------------------------
    // CPU
    // ------------------------------------------------------------------
    wire [12:0] code_addr;
    wire [15:0] insn;
    wire [15:0] mem_addr, mem_wdata, mem_rdata, cpu_t;
    wire        mem_we, mem_byte, mem_rd;
    wire [12:0] cpu_pc;

    hf16_cpu cpu (
        .clk(clk), .rst(cpu_rst), .stall(1'b0),
        .code_addr(code_addr), .insn(insn),
        .mem_addr(mem_addr), .mem_we(mem_we), .mem_byte(mem_byte),
        .mem_wdata(mem_wdata), .mem_rdata(mem_rdata), .mem_rd(mem_rd),
        .t_out(cpu_t), .pc_out(cpu_pc)
    );

    // ------------------------------------------------------------------
    // 12K x 16 RAM (24 KB, 0x0000..0x5FFF) as two 12K x 8 halves (byte stores without
    // byte enables). Port A = instruction fetch, port B = data. Initialized with the
    // kernel. Code runs from the first 16 KB only (13-bit word addresses in jumps and
    // calls); 0x4000..0x5FFF is data. Stores to 0x6000..0x7FFF are ignored.
    // ------------------------------------------------------------------
    (* ramstyle = "M9K, no_rw_check" *) reg [7:0] ram_lo [0:12287];
    (* ramstyle = "M9K, no_rw_check" *) reg [7:0] ram_hi [0:12287];
    initial begin
        $readmemh(INIT_LO, ram_lo);
        $readmemh(INIT_HI, ram_hi);
    end

    reg  [7:0]  lo_a, hi_a, lo_b, hi_b;
    wire        ram_store = mem_we && !mem_addr[15] && mem_addr[14:13] != 2'b11;
    wire [13:0] addrb     = mem_addr[14:1];
    wire [7:0]  din_lo    = mem_wdata[7:0];
    wire [7:0]  din_hi    = mem_byte ? mem_wdata[7:0] : mem_wdata[15:8];
    wire        we_lo     = ram_store && (!mem_byte || !mem_addr[0]);
    wire        we_hi     = ram_store && (!mem_byte ||  mem_addr[0]);

    always @(posedge clk) begin
        lo_a <= ram_lo[code_addr];
        hi_a <= ram_hi[code_addr];
    end
    always @(posedge clk) begin
        if (we_lo) ram_lo[addrb] <= din_lo;
        lo_b <= ram_lo[addrb];
    end
    always @(posedge clk) begin
        if (we_hi) ram_hi[addrb] <= din_hi;
        hi_b <= ram_hi[addrb];
    end

    assign insn = {hi_a, lo_a};

    // ------------------------------------------------------------------
    // UART FIFOs: 8 KB in (store payloads arrive while the flash erases), 1 KB out
    // ------------------------------------------------------------------
    wire [7:0]  rx_dout, tx_dout;
    wire        rx_empty, rx_full, tx_empty, tx_full;
    wire [13:0] rx_count;
    wire        io_store = mem_we && mem_addr[15];
    assign      sw_restart = io_store && mem_addr[7:1] == 7'd4;
    wire       rx_pop   = mem_rd && cpu_t == 16'h8000;
    wire       tx_push  = io_store && mem_addr[7:1] == 7'd0;
    wire       utx_busy;
    reg        utx_start = 1'b0;

    fifo_bram #(.WIDTH(8), .DEPTH_LOG2(13)) rx_fifo (
        .clk(clk), .rst(cpu_rst), .push(urx_valid && urx_data != 8'h12), .din(urx_data),
        .pop(rx_pop), .dout(rx_dout), .empty(rx_empty), .full(rx_full), .count(rx_count)
    );

    fifo_bram #(.WIDTH(8), .DEPTH_LOG2(10)) tx_fifo (
        .clk(clk), .rst(cpu_rst), .push(tx_push), .din(mem_wdata[7:0]),
        .pop(utx_start), .dout(tx_dout), .empty(tx_empty), .full(tx_full), .count()
    );

    always @(posedge clk)
        utx_start <= !cpu_rst && !tx_empty && !utx_busy && !utx_start;

    uart_tx #(.CLKS_PER_BIT(CLKS_PER_BIT)) u_utx (
        .clk(clk), .rst(sys_rst), .start(utx_start), .data(tx_dout),
        .busy(utx_busy), .txd(UART_TXD)
    );

    // ------------------------------------------------------------------
    // User button (synchronized, sampled every 10 ms) and millisecond timer
    // ------------------------------------------------------------------
    localparam integer MS_DIV = CLK_HZ / 1000;

    reg        btn_meta = 1'b1, btn_sync = 1'b1, btn_state = 1'b0;
    reg [31:0] ms_div   = 0;
    reg [3:0]  ms_10    = 4'd0;
    reg [15:0] ms_count = 16'd0;

    always @(posedge clk) begin
        btn_meta <= USER_BTN;
        btn_sync <= btn_meta;
        if (ms_div == MS_DIV - 1) begin
            ms_div   <= 0;
            ms_count <= ms_count + 16'd1;
            if (ms_10 == 4'd9) begin
                ms_10     <= 4'd0;
                btn_state <= !btn_sync;          // 1 = pressed
            end else
                ms_10 <= ms_10 + 4'd1;
        end else
            ms_div <= ms_div + 1;
    end

    // ------------------------------------------------------------------
    // Pins: two 16-bit ports, pin n = bit n%16 of port n/16.
    //   pins 0..14  = D0..D14, pins 16..23 = PIO_01..PIO_08; 15 and 24..31 do not exist.
    // All pins are inputs after reset.
    // ------------------------------------------------------------------
    localparam [31:0] PIN_EXISTS = 32'h00FF_7FFF;

    reg  [31:0] pin_out = 32'd0, pin_dir = 32'd0;   // pin_dir: 1 = output
    reg  [31:0] pin_meta = 32'd0, pin_sync = 32'd0;
    wire [31:0] pin_exists = PIN_EXISTS;

    // The address of the I/O read in progress: registered, so the read multiplexer below
    // works in the cycle after the address was formed (like the RAM's address register)
    reg [15:0] io_addr = 16'd0;
    always @(posedge clk)
        io_addr <= mem_addr;

    // PWM: four channels at 0x8080 + 8c; an enabled channel takes over its pin
    localparam integer PWM_CH = 4;
    wire [15:0]         pwm_rdata;
    wire [PWM_CH-1:0]   pwm_en, pwm_level;
    wire [6*PWM_CH-1:0] pwm_pin;
    wire                io_pwm = mem_addr[7:5] == 3'b100;  // 0x8080..0x809F

    pwm #(.CHANNELS(PWM_CH)) pwm_unit (
        .clk(clk), .rst(cpu_rst), .we(io_store && io_pwm),
        .ch_sel(mem_addr[4:3]), .reg_sel(mem_addr[2:1]),
        .rd_ch(io_addr[4:3]), .rd_reg(io_addr[2:1]), .wdata(mem_wdata),
        .rdata(pwm_rdata), .enable(pwm_en), .pin(pwm_pin), .out(pwm_level)
    );

    // DDS square wave at 0x8060 (rtl/dds.v), on its own 192 MHz clock; like a PWM channel it
    // owns its pin while it is on
    wire [15:0] dds_rdata;
    wire        dds_en, dds_out;
    wire [5:0]  dds_pin;
    wire        io_dds = mem_addr[7:5] == 3'b011;          // 0x8060..0x807F

    dds dds_unit (
        .clk(clk), .rst(cpu_rst), .we(io_store && io_dds),
        .reg_sel(mem_addr[2:1]), .rd_sel(io_addr[2:1]), .wdata(mem_wdata), .rdata(dds_rdata),
        .dds_clk(pll_clk[2]), .out(dds_out), .enable(dds_en), .pin(dds_pin)
    );

    // ------------------------------------------------------------------
    // Serial interfaces on any pin (after ZYNQ-FPGA-FORTH32 3.1): UART 0/1 at 0x80A0/0x80B0
    // (rtl/uart_port.v), SPI 0/1 at 0x80C0/0x80D0 (rtl/spi_port.v), I2C at 0x80E0
    // (rtl/i2c_port.v). Inputs (RX, MISO, SCL, SDA) read the synchronized pin levels.
    // ------------------------------------------------------------------
    wire [63:0] pin_in = {32'd0, pin_sync};

    wire [15:0] u0_rdata, u1_rdata, s0_rdata, s1_rdata, i2c_rdata;
    wire        u0_tx, u1_tx, u0_en, u1_en;
    wire [5:0]  u0_txp, u0_rxp, u1_txp, u1_rxp;
    wire        s0_sck, s0_mosi, s0_en, s1_sck, s1_mosi, s1_en;
    wire [5:0]  s0_sckp, s0_mosip, s0_misop, s1_sckp, s1_mosip, s1_misop;
    wire        i2c_scl_low, i2c_sda_low, i2c_en;
    wire [5:0]  i2c_sclp, i2c_sdap;

    wire wr_u0  = io_store && mem_addr[7:4] == 4'hA;
    wire wr_u1  = io_store && mem_addr[7:4] == 4'hB;
    wire wr_s0  = io_store && mem_addr[7:4] == 4'hC;
    wire wr_s1  = io_store && mem_addr[7:4] == 4'hD;
    wire wr_i2c = io_store && mem_addr[7:4] == 4'hE;

    uart_port u0 (
        .clk(clk), .rst(cpu_rst), .we(wr_u0), .rd(mem_rd && cpu_t == 16'h80A0),
        .reg_sel(mem_addr[3:1]), .rd_sel(io_addr[3:1]), .wdata(mem_wdata), .rdata(u0_rdata),
        .rx_in(pin_in[u0_rxp]), .tx_out(u0_tx), .enable(u0_en), .tx_pin(u0_txp), .rx_pin(u0_rxp)
    );
    uart_port u1 (
        .clk(clk), .rst(cpu_rst), .we(wr_u1), .rd(mem_rd && cpu_t == 16'h80B0),
        .reg_sel(mem_addr[3:1]), .rd_sel(io_addr[3:1]), .wdata(mem_wdata), .rdata(u1_rdata),
        .rx_in(pin_in[u1_rxp]), .tx_out(u1_tx), .enable(u1_en), .tx_pin(u1_txp), .rx_pin(u1_rxp)
    );
    spi_port s0 (
        .clk(clk), .rst(cpu_rst), .we(wr_s0), .reg_sel(mem_addr[3:1]), .rd_sel(io_addr[3:1]),
        .wdata(mem_wdata), .rdata(s0_rdata), .miso_in(pin_in[s0_misop]),
        .sck_out(s0_sck), .mosi_out(s0_mosi), .enable(s0_en),
        .sck_pin(s0_sckp), .mosi_pin(s0_mosip), .miso_pin(s0_misop)
    );
    spi_port s1 (
        .clk(clk), .rst(cpu_rst), .we(wr_s1), .reg_sel(mem_addr[3:1]), .rd_sel(io_addr[3:1]),
        .wdata(mem_wdata), .rdata(s1_rdata), .miso_in(pin_in[s1_misop]),
        .sck_out(s1_sck), .mosi_out(s1_mosi), .enable(s1_en),
        .sck_pin(s1_sckp), .mosi_pin(s1_mosip), .miso_pin(s1_misop)
    );
    i2c_port i2c0 (
        .clk(clk), .rst(cpu_rst), .we(wr_i2c), .reg_sel(mem_addr[2:1]), .rd_sel(io_addr[2:1]),
        .wdata(mem_wdata), .rdata(i2c_rdata),
        .scl_in(pin_in[i2c_sclp]), .sda_in(pin_in[i2c_sdap]),
        .scl_low(i2c_scl_low), .sda_low(i2c_sda_low), .enable(i2c_en),
        .scl_pin(i2c_sclp), .sda_pin(i2c_sdap)
    );

    // ------------------------------------------------------------------
    // Pin router: every output of a peripheral names its pin; an enabled one owns that pin
    // (push-pull, or for I2C open drain: driven low or left to the pull-up). Owners of the
    // same pin are ORed; the Forth words refuse a pin that is taken. An owned pin's latch
    // and direction do not count.
    // ------------------------------------------------------------------
    localparam integer PP = PWM_CH + 7;      // PWM 0..3, DDS, UART TX 0/1, SPI SCK/MOSI 0/1
    wire [PP-1:0]   pp_en  = {s1_en, s1_en, s0_en, s0_en, u1_en, u0_en, dds_en, pwm_en};
    wire [PP-1:0]   pp_val = {s1_mosi, s1_sck, s0_mosi, s0_sck, u1_tx, u0_tx, dds_out, pwm_level};
    wire [6*PP-1:0] pp_pin = {s1_mosip, s1_sckp, s0_mosip, s0_sckp, u1_txp, u0_txp, dds_pin,
                              pwm_pin};

    reg [31:0] pp_owns, pp_value, od_owns, od_low;      // per pin
    integer    p, q;
    always @* begin
        pp_owns  = 32'd0;
        pp_value = 32'd0;
        od_owns  = 32'd0;
        od_low   = 32'd0;
        for (p = 0; p < 32; p = p + 1) begin
            for (q = 0; q < PP; q = q + 1)
                if (pp_en[q] && pp_pin[6*q +: 6] == p) begin
                    pp_owns[p]  = 1'b1;
                    pp_value[p] = pp_value[p] | pp_val[q];
                end
            if (i2c_en && i2c_sclp == p) begin
                od_owns[p] = 1'b1;
                od_low[p]  = od_low[p] | i2c_scl_low;
            end
            if (i2c_en && i2c_sdap == p) begin
                od_owns[p] = 1'b1;
                od_low[p]  = od_low[p] | i2c_sda_low;
            end
        end
    end

    wire [31:0] owned     = pp_owns | od_owns;
    wire [31:0] pin_drive = ((pin_dir & ~owned) | pp_owns | od_low) & pin_exists;
    wire [31:0] pin_level = (pin_out & ~owned) | (pp_value & pp_owns);

    genvar g;
    generate
        for (g = 0; g < 15; g = g + 1) begin : g_d
            assign D[g] = pin_drive[g] ? pin_level[g] : 1'bz;
        end
        for (g = 0; g < 8; g = g + 1) begin : g_pio
            assign PIO[g] = pin_drive[16 + g] ? pin_level[16 + g] : 1'bz;
        end
    endgenerate

    always @(posedge clk) begin
        pin_meta <= {8'd0, PIO, 1'b0, D};
        pin_sync <= pin_meta;
    end

    // ------------------------------------------------------------------
    // ADC: CH0..CH8 and the temperature sensing diode (slot 9) scanned continuously
    // (rtl/adc_scan.v)
    // ------------------------------------------------------------------
    wire [12*10-1:0] adc_codes;
    adc_scan u_adc (
        .clk(clk), .rst_n(!sys_rst), .pll_c0(pll_clk[0]), .pll_locked(pll_locked),
        .codes(adc_codes)
    );
    wire [12*16-1:0] adc_pad  = {{12*6{1'b0}}, adc_codes};
    wire [3:0]       adc_ch   = io_addr[4:1];
    wire [11:0]      adc_code = adc_pad[adc_ch*12 +: 12];

    // ------------------------------------------------------------------
    // SPI master for the serial flash and the LIS3DH accelerometer, CRC-32 unit,
    // at 0x8020..0x802A (rtl/spi_flash.v). Both devices share the shift engine; each
    // has its own pins and select.
    // ------------------------------------------------------------------
    wire        io_spi = mem_addr[7:4] == 4'h2;
    wire [15:0] spi_rdata;

    spi_flash u_spi (
        .clk(clk), .rst(cpu_rst), .we(io_store && io_spi), .reg_sel(mem_addr[3:1]),
        .rd_sel(io_addr[3:1]), .wdata(mem_wdata), .rdata(spi_rdata),
        .cs_n(F_S), .sck(F_CLK), .mosi(F_DI), .miso(F_DO),
        .cs2_n(SEN_CS), .sck2(SEN_SPC), .mosi2(SEN_SDI), .miso2(SEN_SDO)
    );

    // ------------------------------------------------------------------
    // I/O registers (CPU side). A read returns its value in the cycle after the address,
    // like the RAM: io_addr holds the address, the multiplexer below is combinational.
    //   0x8000..0x800E  UART, LEDs, button, restart (write), ms timer, RX peek, RX count
    //   0x8010 + 8p     port p (0..1): +0 pin levels, +2 output latch, +4 direction,
    //                   +6 pins driven now (read only: outputs and peripheral pins)
    //   0x8020..0x802A  SPI (flash, accelerometer) and CRC-32
    //   0x8040 + 2c     ADC channel c (0..8), 12-bit code; 0x8052 temperature diode
    //   0x8060..0x8064  DDS: tuning low/high, control (rtl/dds.v)
    //   0x8080 + 8c     PWM channel c: +0/+2 period, +4 duty, +6 control (rtl/pwm.v)
    //   0x80A0 + 16u    UART u (0..1): data, status, divider low/high, pins (rtl/uart_port.v)
    //   0x80C0 + 16s    SPI s (0..1): data, status, control, half, pins, MISO pin (spi_port.v)
    //   0x80E0..0x80E4  I2C: command/status, quarter, pins (rtl/i2c_port.v)
    // ------------------------------------------------------------------
    reg [15:0] io_rdata;
    reg        io_sel   = 1'b0;

    // stores: decoded from mem_addr (= T during a store)
    wire       io_gpio = mem_addr[7:6] == 2'b00 && (mem_addr[5:3] == 3'd2 || mem_addr[5:3] == 3'd3);
    wire       io_port = mem_addr[3];
    wire [4:0] io_bit0 = {io_port, 4'd0};
    // reads: decoded from io_addr
    wire       rd_gpio = io_addr[7:6] == 2'b00 && (io_addr[5:3] == 3'd2 || io_addr[5:3] == 3'd3);
    wire [4:0] rd_bit0 = {io_addr[3], 4'd0};

    always @(posedge clk) begin
        if (cpu_rst) begin
            LED     <= 8'd0;
            pin_out <= 32'd0;
            pin_dir <= 32'd0;
        end else if (io_store) begin
            if (mem_addr[7:4] == 4'd0 && mem_addr[3:1] == 3'd2)
                LED <= mem_wdata[7:0];
            if (io_gpio)
                case (mem_addr[2:1])
                    2'd1: pin_out[io_bit0 +: 16] <= mem_wdata;
                    2'd2: pin_dir[io_bit0 +: 16] <= mem_wdata;
                    default: ;
                endcase
        end

        io_sel <= mem_addr[15];
    end

    always @* begin
        if (io_addr[7:4] == 4'hA)
            io_rdata = u0_rdata;
        else if (io_addr[7:4] == 4'hB)
            io_rdata = u1_rdata;
        else if (io_addr[7:4] == 4'hC)
            io_rdata = s0_rdata;
        else if (io_addr[7:4] == 4'hD)
            io_rdata = s1_rdata;
        else if (io_addr[7:4] == 4'hE)
            io_rdata = i2c_rdata;
        else if (io_addr[7:5] == 3'b010)
            io_rdata = (io_addr[5:1] <= 5'd9) ? {4'd0, adc_code} : 16'd0;
        else if (io_addr[7:5] == 3'b011)
            io_rdata = dds_rdata;
        else if (io_addr[7:5] == 3'b100)
            io_rdata = pwm_rdata;
        else if (io_addr[7:4] == 4'h2)
            io_rdata = spi_rdata;
        else if (rd_gpio)
            case (io_addr[2:1])
                2'd0:    io_rdata = pin_sync[rd_bit0 +: 16] & pin_exists[rd_bit0 +: 16];
                2'd1:    io_rdata = pin_out[rd_bit0 +: 16]  & pin_exists[rd_bit0 +: 16];
                2'd2:    io_rdata = pin_dir[rd_bit0 +: 16]  & pin_exists[rd_bit0 +: 16];
                default: io_rdata = pin_drive[rd_bit0 +: 16];   // driven now (also by a peripheral)
            endcase
        else if (io_addr[7:4] == 4'd0)
            case (io_addr[3:1])
                3'd0:    io_rdata = {8'd0, rx_dout};
                3'd1:    io_rdata = {13'd0, tx_empty && !utx_busy && !utx_start, !tx_full, !rx_empty};
                3'd2:    io_rdata = {8'd0, LED};
                3'd3:    io_rdata = {15'd0, btn_state};
                3'd5:    io_rdata = ms_count;
                3'd6:    io_rdata = {8'd0, rx_dout};         // peek: next byte, not popped
                3'd7:    io_rdata = {2'b0, rx_count};        // bytes waiting in the RX FIFO
                default: io_rdata = 16'd0;
            endcase
        else
            io_rdata = 16'd0;
    end

    assign mem_rdata = io_sel ? io_rdata : {hi_b, lo_b};

endmodule

`default_nettype wire
