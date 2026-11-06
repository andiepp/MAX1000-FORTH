// A UART for the header pins (after ZYNQ-FPGA-FORTH32's rtl/uart.v, on the 16-bit bus):
// 8 data bits, no parity, 1 stop bit, any baud rate, 1 KB FIFOs in M9K in both directions.
// max1000_forth routes TX and RX to the pins named in the pins register.
//
// Registers (reg_sel = word offset in the block):
//   0  data      read: next received byte, bit 8 set when one was there (the read pops it)
//                write: send a byte (pushed into the TX FIFO; ignored when it is full)
//   1  status    read: bit 0 a byte received, bit 1 TX FIFO has room, bit 2 transmitter idle
//                (FIFO empty, last stop bit sent), bit 3 a byte was lost (RX FIFO full),
//                bit 4 framing error (no stop bit); write: clears bits 3 and 4
//   2  divider   clocks per bit, bits 15..0 (at least 8; 417 = 115200 Bd at 48 MHz)
//   3  divider   bits 23..16 (in bits 7..0)
//   4  pins      bit 15 enable, bits 13..8 RX pin, bits 5..0 TX pin
// Reset: disabled, FIFOs empty, 115200 Bd. While disabled the FIFOs are held empty.

`default_nettype none

module uart_port #(
    parameter [23:0] DEFAULT_DIV = 24'd417
) (
    input  wire        clk,
    input  wire        rst,
    input  wire        we,
    input  wire        rd,           // the CPU consumes the data register (pops RX)
    input  wire [2:0]  reg_sel,      // write: from the store address
    input  wire [2:0]  rd_sel,       // read: from the registered read address
    input  wire [15:0] wdata,
    output reg  [15:0] rdata,        // combinational read of the register rd_sel names
    input  wire        rx_in,        // RX pin level, synchronized
    output wire        tx_out,
    output wire        enable,
    output wire [5:0]  tx_pin,
    output wire [5:0]  rx_pin
);

    reg [23:0] div = DEFAULT_DIV;
    reg [15:0] pins = 16'd0;
    reg        overrun = 1'b0, frame_err = 1'b0;

    // clocks per bit as used: at least 8 (the register reads back as written)
    wire [23:0] bdiv = div[23:3] == 21'd0 ? 24'd8 : div;

    assign enable = pins[15];
    assign tx_pin = pins[5:0];
    assign rx_pin = pins[13:8];

    // FIFOs (first word fall through)
    wire [7:0] tx_dout, rx_dout;
    wire       tx_empty, tx_full, rx_empty, rx_full;
    reg        tx_pop = 1'b0, rx_push = 1'b0;
    reg  [7:0] rx_byte = 8'd0;

    fifo_bram #(.WIDTH(8), .DEPTH_LOG2(10)) txq (
        .clk(clk), .rst(rst || !enable), .push(we && reg_sel == 3'd0), .din(wdata[7:0]),
        .pop(tx_pop), .dout(tx_dout), .empty(tx_empty), .full(tx_full), .count()
    );
    fifo_bram #(.WIDTH(8), .DEPTH_LOG2(10)) rxq (
        .clk(clk), .rst(rst || !enable), .push(rx_push), .din(rx_byte),
        .pop(rd), .dout(rx_dout), .empty(rx_empty), .full(rx_full), .count()
    );

    // transmitter: start bit, 8 data bits LSB first, stop bit
    reg [9:0]  tx_shift = 10'h3FF;
    reg [3:0]  tx_bits = 4'd0;
    reg [23:0] tx_count = 24'd0;
    assign tx_out = tx_shift[0];

    always @(posedge clk) begin
        tx_pop <= 1'b0;
        if (rst || !enable) begin
            tx_shift <= 10'h3FF;
            tx_bits  <= 4'd0;
            tx_count <= 24'd0;
        end else if (tx_bits == 4'd0) begin
            if (!tx_empty && !tx_pop) begin
                tx_shift <= {1'b1, tx_dout, 1'b0};
                tx_bits  <= 4'd10;
                tx_count <= bdiv - 24'd1;
                tx_pop   <= 1'b1;
            end
        end else if (tx_count == 24'd0) begin
            tx_shift <= {1'b1, tx_shift[9:1]};
            tx_bits  <= tx_bits - 4'd1;
            tx_count <= bdiv - 24'd1;
        end else begin
            tx_count <= tx_count - 24'd1;
        end
    end

    // receiver: a falling edge starts a byte, every bit is sampled in its middle
    reg        rx_prev = 1'b1;
    reg [3:0]  rx_bits = 4'd0;           // 0 idle, 10 start bit ... 1 stop bit
    reg [23:0] rx_count = 24'd0;
    reg [7:0]  rx_shift = 8'd0;

    always @(posedge clk) begin
        rx_push <= 1'b0;
        rx_prev <= rx_in;
        if (rst || !enable) begin
            rx_bits <= 4'd0;
        end else if (rx_bits == 4'd0) begin
            if (rx_prev && !rx_in) begin
                rx_bits  <= 4'd10;
                rx_count <= {1'b0, bdiv[23:1]};  // to the middle of the start bit
            end
        end else if (rx_count != 24'd0) begin
            rx_count <= rx_count - 24'd1;
        end else begin
            rx_count <= bdiv - 24'd1;
            rx_bits  <= rx_bits - 4'd1;
            if (rx_bits == 4'd10) begin
                if (rx_in)
                    rx_bits <= 4'd0;            // a glitch, not a start bit
            end else if (rx_bits == 4'd1) begin
                if (!rx_in)
                    frame_err <= 1'b1;
                else if (rx_full)
                    overrun <= 1'b1;
                else begin
                    rx_byte <= rx_shift;
                    rx_push <= 1'b1;
                end
            end else begin
                rx_shift <= {rx_in, rx_shift[7:1]};
            end
        end

        if (we && reg_sel == 3'd1) begin
            overrun   <= 1'b0;
            frame_err <= 1'b0;
        end
        if (rst) begin
            overrun   <= 1'b0;
            frame_err <= 1'b0;
        end
    end

    // registers
    always @(posedge clk) begin
        if (rst) begin
            div  <= DEFAULT_DIV;
            pins <= 16'd0;
        end else if (we) begin
            if (reg_sel == 3'd2) div[15:0]  <= wdata;
            if (reg_sel == 3'd3) div[23:16] <= wdata[7:0];
            if (reg_sel == 3'd4) pins       <= wdata;
        end
    end

    always @* begin
        case (rd_sel)
            3'd0:    rdata = {7'd0, !rx_empty, rx_empty ? 8'd0 : rx_dout};
            3'd1:    rdata = {11'd0, frame_err, overrun, tx_empty && tx_bits == 4'd0 && !tx_pop,
                              !tx_full, !rx_empty};
            3'd2:    rdata = div[15:0];
            3'd3:    rdata = {8'd0, div[23:16]};
            3'd4:    rdata = pins;
            default: rdata = 16'd0;
        endcase
    end

endmodule

`default_nettype wire
