// SPI master for the MAX1000's serial flash and its LIS3DH accelerometer, plus a CRC-32
// unit, all driven by Forth. The two devices share the shift engine; each has its own pins
// (clock and data out go to both, data in comes from the selected one).
//
// Registers (word offsets from 0x8020, as the CPU sees them):
//   +0  write: send a byte (starts an 8-bit exchange)   read: the byte received last
//   +2  write: bit 0 = select the flash (F_S low),       read: bit 0 = flash selected,
//              bit 1 = select the accelerometer                bit 1 = busy,
//              (SEN_CS low)                                    bit 2 = accelerometer selected
//   +4  write: reset the CRC
//   +6  write: add the low byte to the CRC
//   +8  read: CRC-32 bits 15..0                           (standard CRC-32: reflected,
//   +A  read: CRC-32 bits 31..16                           poly 0xEDB88320, init/xorout ~0)
//
// SPI mode 0, SCK = clk / 6 (8 MHz at 48 MHz; the LIS3DH allows 10 MHz). MOSI changes after the falling edge, MISO is
// taken at the rising edge.

`default_nettype none

module spi_flash (
    input  wire        clk,
    input  wire        rst,
    input  wire        we,          // a CPU store to this block
    input  wire [2:0]  reg_sel,     // mem_addr[3:1] of a store
    input  wire [2:0]  rd_sel,      // the register being read (io_addr[3:1])
    input  wire [15:0] wdata,
    output reg  [15:0] rdata,       // combinational read of the addressed register
    output reg         cs_n,        // flash
    output wire        sck,
    output wire        mosi,
    input  wire        miso,
    output reg         cs2_n,       // accelerometer
    output wire        sck2,
    output wire        mosi2,
    input  wire        miso2
);

    reg sck_r = 1'b0;
    assign sck   = sck_r;
    assign sck2  = sck_r;
    wire   miso_sel = cs2_n ? miso : miso2;

    reg  [7:0] shreg = 8'd0;
    reg  [3:0] nbits = 4'd0;
    reg  [1:0] half  = 2'd0;     // clocks in the current half period (3 per half)
    reg        rx_bit = 1'b0;
    wire       busy  = nbits != 4'd0;
    reg [31:0] crc   = 32'hFFFF_FFFF;

    assign mosi  = shreg[7];
    assign mosi2 = shreg[7];

    function [31:0] crc_byte(input [31:0] c, input [7:0] b);
        integer i;
        reg [31:0] x;
        begin
            x = c ^ {24'd0, b};
            for (i = 0; i < 8; i = i + 1)
                x = x[0] ? (x >> 1) ^ 32'hEDB8_8320 : (x >> 1);
            crc_byte = x;
        end
    endfunction

    always @(posedge clk) begin
        if (rst) begin
            cs_n  <= 1'b1;
            cs2_n <= 1'b1;
            sck_r <= 1'b0;
            shreg <= 8'd0;
            nbits <= 4'd0;
            half  <= 2'd0;
            crc   <= 32'hFFFF_FFFF;
        end else begin
            if (we)
                case (reg_sel)
                    3'd0: if (!busy) begin
                        shreg <= wdata[7:0];
                        nbits <= 4'd8;
                        half  <= 2'd0;
                        sck_r <= 1'b0;
                    end
                    3'd1: begin
                        cs_n  <= !wdata[0];
                        cs2_n <= !wdata[1];
                    end
                    3'd2: crc  <= 32'hFFFF_FFFF;
                    3'd3: crc  <= crc_byte(crc, wdata[7:0]);
                    default: ;
                endcase

            if (busy) begin
                half <= (half == 2'd2) ? 2'd0 : half + 2'd1;
                if (half == 2'd2) begin
                    if (!sck_r) begin
                        sck_r  <= 1'b1;
                        rx_bit <= miso_sel;
                    end else begin
                        sck_r <= 1'b0;
                        shreg <= {shreg[6:0], rx_bit};
                        nbits <= nbits - 4'd1;
                    end
                end
            end
        end
    end

    always @* begin
        case (rd_sel)
            3'd0:    rdata = {8'd0, shreg};
            3'd1:    rdata = {13'd0, !cs2_n, busy, !cs_n};
            3'd4:    rdata = ~crc[15:0];
            3'd5:    rdata = ~crc[31:16];
            default: rdata = 16'd0;
        endcase
    end

endmodule

`default_nettype wire
