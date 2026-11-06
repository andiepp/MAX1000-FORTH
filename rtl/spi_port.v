// An SPI master for the header pins (after ZYNQ-FPGA-FORTH32's rtl/spi.v, on the 16-bit
// bus): modes 0..3, 8 or 16 bits per transfer, MSB or LSB first, SCK = clk / (2 * half)
// with half >= 2 (at most 12 MHz at 48 MHz). Chip selects are ordinary pins driven by
// Forth. max1000_forth routes SCK, MOSI and MISO.
//
// Registers (reg_sel = word offset in the block):
//   0  data      write: start a transfer with this value (ignored while busy)
//                read: the value received by the last transfer
//   1  status    bit 0 busy
//   2  control   bit 0 CPHA, bit 1 CPOL, bit 2 LSB first, bit 4 16 bits (else 8)
//   3  half      clocks per half SCK period (at least 2; 24 = 1 MHz)
//   4  pins      bit 15 enable, bits 13..8 MOSI pin, bits 5..0 SCK pin
//   5  pins      bits 5..0 MISO pin
//
// MISO comes through the two-flop pin synchronizer and is sampled at the end of each bit,
// just before the next change edge: in every mode that is after the slave set the bit, and
// the synchronizer's two clocks of delay still leave (2 * half - 2) clocks for the slave.

`default_nettype none

module spi_port (
    input  wire        clk,
    input  wire        rst,
    input  wire        we,
    input  wire [2:0]  reg_sel,
    input  wire [2:0]  rd_sel,
    input  wire [15:0] wdata,
    output reg  [15:0] rdata,
    input  wire        miso_in,      // synchronized
    output reg         sck_out = 1'b0,
    output wire        mosi_out,
    output wire        enable,
    output wire [5:0]  sck_pin,
    output wire [5:0]  mosi_pin,
    output wire [5:0]  miso_pin
);

    reg [4:0]  control = 5'd0;
    reg [15:0] half = 16'd24;              // 1 MHz
    reg [15:0] pins = 16'd0;
    reg [5:0]  pins_b = 6'd0;
    reg [15:0] rx = 16'd0;

    assign enable   = pins[15];
    assign sck_pin  = pins[5:0];
    assign mosi_pin = pins[13:8];
    assign miso_pin = pins_b;

    wire       cpha  = control[0];
    wire       cpol  = control[1];
    wire       lsb   = control[2];
    wire       w16   = control[4];
    wire [15:0] hclk = half < 16'd2 ? 16'd2 : half;

    reg        busy = 1'b0;
    reg [15:0] tx = 16'd0, shift_in = 16'd0;
    reg [4:0]  bits = 5'd0;                // bits still to send, the current one included
    reg        second = 1'b0;              // second half of the bit
    reg [15:0] count = 16'd0;

    // the current output bit: bit 7 or 15 for MSB first, bit 0 for LSB first
    assign mosi_out = lsb ? tx[0] : (w16 ? tx[15] : tx[7]);

    always @(posedge clk) begin
        if (rst) begin
            control <= 5'd0;
            half    <= 16'd24;
            pins    <= 16'd0;
            pins_b  <= 6'd0;
        end else if (we) begin
            if (reg_sel == 3'd2) control <= wdata[4:0];
            if (reg_sel == 3'd3) half    <= wdata;
            if (reg_sel == 3'd4) pins    <= wdata;
            if (reg_sel == 3'd5) pins_b  <= wdata[5:0];
        end

        if (rst || !enable) begin
            busy    <= 1'b0;
            sck_out <= cpol;
        end else if (!busy) begin
            sck_out <= cpol;
            if (we && reg_sel == 3'd0) begin
                busy     <= 1'b1;
                tx       <= w16 ? wdata : {8'd0, wdata[7:0]};
                bits     <= w16 ? 5'd16 : 5'd8;
                second   <= 1'b0;
                count    <= hclk - 16'd1;
                shift_in <= 16'd0;
                sck_out  <= cpol ^ cpha;           // CPHA 1: the leading edge starts the bit
            end
        end else if (count != 16'd0) begin
            count <= count - 16'd1;
        end else begin
            count <= hclk - 16'd1;
            if (!second) begin
                second  <= 1'b1;
                sck_out <= cpol ^ !cpha;           // the middle of the bit
            end else begin
                // the end of the bit: sample MISO, move to the next bit
                second   <= 1'b0;
                shift_in <= lsb ? {miso_in, shift_in[15:1]} : {shift_in[14:0], miso_in};
                tx       <= lsb ? tx >> 1 : tx << 1;
                bits     <= bits - 5'd1;
                if (bits == 5'd1) begin
                    busy    <= 1'b0;
                    sck_out <= cpol;
                    if (lsb)
                        rx <= w16 ? {miso_in, shift_in[15:1]} : {8'd0, miso_in, shift_in[15:9]};
                    else
                        rx <= w16 ? {shift_in[14:0], miso_in} : {8'd0, shift_in[6:0], miso_in};
                end else begin
                    sck_out <= cpol ^ cpha;
                end
            end
        end
    end

    always @* begin
        case (rd_sel)
            3'd0:    rdata = rx;
            3'd1:    rdata = {15'd0, busy};
            3'd2:    rdata = {11'd0, control};
            3'd3:    rdata = half;
            3'd4:    rdata = pins;
            3'd5:    rdata = {10'd0, pins_b};
            default: rdata = 16'd0;
        endcase
    end

endmodule

`default_nettype wire
