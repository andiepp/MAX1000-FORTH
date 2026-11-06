// DDS square-wave generator (after ZYNQ-FPGA-FORTH32's rtl/dds.v): a 32-bit phase
// accumulator on its own fast clock (dds_clk, 192 MHz) whose top bit is a square wave on a
// pin: f = tuning * f_dds / 2^32 (0.045 Hz steps at 192 MHz), edges on the dds_clk grid.
// The top level routes the output to the pin in the control register, like a PWM channel.
//
// Registers (word offsets from 0x8060, as the CPU sees them):
//   +0  tuning, low 16 bits     (held until the high half is written)
//   +2  tuning, high 16 bits    (writing it takes both halves)
//   +4  control: bit 15 on, bits 5..0 pin;  read: bit 14 = busy (a change not yet taken
//       over by the generator clock)
// A change goes to the generator clock through a toggle handshake; one written while the
// previous is in flight is sent again afterwards, so the last value always arrives. Turning
// it on starts the phase at 0. A CPU reset turns it off.

`default_nettype none

module dds (
    input  wire        clk,
    input  wire        rst,
    input  wire        we,          // a CPU store to this block
    input  wire [1:0]  reg_sel,     // mem_addr[2:1] of a store
    input  wire [1:0]  rd_sel,      // the register being read (io_addr[2:1])
    input  wire [15:0] wdata,
    output reg  [15:0] rdata,
    input  wire        dds_clk,
    output reg         out = 1'b0,  // dds_clk domain
    output wire        enable,
    output wire [5:0]  pin
);

    reg [15:0] lo_hold = 16'd0;
    reg [31:0] tuning  = 32'd0;
    reg [15:0] control = 16'd0;
    assign enable = control[15];
    assign pin    = control[5:0];

    // CPU clock: the handshake
    reg ack_q = 1'b0;                      // generator clock: the last request taken
    reg req = 1'b0, dirty = 1'b0;
    reg ack_m = 1'b0, ack_s = 1'b0;
    wire busy = req != ack_s;
    wire change = we && reg_sel != 2'd0;  // the high half or the control register

    always @(posedge clk) begin
        ack_m <= ack_q;
        ack_s <= ack_m;
        if (rst) begin
            lo_hold <= 16'd0;
            tuning  <= 32'd0;
            control <= 16'd0;
            if (!busy) req <= !req;        // tell the generator clock: off
            dirty   <= busy;
        end else begin
            if (we)
                case (reg_sel)
                    2'd0: lo_hold <= wdata;
                    2'd1: tuning  <= {wdata, lo_hold};
                    2'd2: control <= wdata;
                    default: ;
                endcase
            if ((change || dirty) && !busy) begin
                req   <= !req;
                dirty <= 1'b0;
            end else if (change) begin
                dirty <= 1'b1;
            end
        end
    end

    always @* begin
        case (rd_sel)
            2'd0:    rdata = tuning[15:0];
            2'd1:    rdata = tuning[31:16];
            2'd2:    rdata = {control[15], busy || dirty, control[13:0]};
            default: rdata = 16'd0;
        endcase
    end

    // generator clock
    reg        req_m = 1'b0, req_s = 1'b0, req_q = 1'b0;
    reg [31:0] tw = 32'd0, phase = 32'd0;
    reg        on = 1'b0;

    always @(posedge dds_clk) begin
        req_m <= req;
        req_s <= req_m;
        req_q <= req_s;
        if (req_s != req_q) begin
            tw    <= tuning;               // stable: the CPU side waits for the ack
            on    <= control[15];
            ack_q <= req_s;
            if (!on || !control[15])
                phase <= 32'd0;
        end else if (on) begin
            phase <= phase + tw;
        end
        out <= on && phase[31];
    end

endmodule

`default_nettype wire
