// 8N1 UART transmitter and receiver.

`default_nettype none

module uart_tx #(
    parameter integer CLKS_PER_BIT = 417       // 48 MHz / 115200
) (
    input  wire       clk,
    input  wire       rst,
    input  wire       start,                   // data is taken in the cycle start is high
    input  wire [7:0] data,
    output reg        busy,
    output reg        txd
);
    reg [9:0]  shreg;                          // {stop, data[7:0], start}
    reg [3:0]  nbits;
    reg [15:0] cnt;
    always @(posedge clk)
        if (rst) begin
            shreg <= 10'h3FF;
            nbits <= 4'd0;
            cnt   <= 16'd0;
            busy  <= 1'b0;
            txd   <= 1'b1;
        end else if (!busy) begin
            txd <= 1'b1;
            if (start) begin
                shreg <= {1'b1, data, 1'b0};
                nbits <= 4'd10;
                cnt   <= 16'd0;
                busy  <= 1'b1;
            end
        end else if (cnt == 16'd0) begin
            if (nbits == 4'd0) begin
                busy <= 1'b0;
                txd  <= 1'b1;
            end else begin
                txd   <= shreg[0];
                shreg <= {1'b1, shreg[9:1]};
                nbits <= nbits - 4'd1;
                cnt   <= CLKS_PER_BIT - 1;
            end
        end else
            cnt <= cnt - 16'd1;
endmodule

module uart_rx #(
    parameter integer CLKS_PER_BIT = 417
) (
    input  wire       clk,
    input  wire       rst,
    input  wire       rxd,
    output reg  [7:0] data,
    output reg        valid                    // one clock per received byte
);
    localparam IDLE = 2'd0, START = 2'd1, DATA = 2'd2, STOP = 2'd3;
    reg [1:0]  sync = 2'b11;
    reg [1:0]  state;
    reg [15:0] cnt;
    reg [2:0]  bitn;
    reg [7:0]  shreg;
    wire       rx = sync[1];

    always @(posedge clk)
        sync <= {sync[0], rxd};

    always @(posedge clk)
        if (rst) begin
            state <= IDLE;
            cnt   <= 16'd0;
            bitn  <= 3'd0;
            shreg <= 8'd0;
            data  <= 8'd0;
            valid <= 1'b0;
        end else begin
            valid <= 1'b0;
            case (state)
                IDLE:
                    if (!rx) begin
                        state <= START;
                        cnt   <= CLKS_PER_BIT / 2 - 1;
                    end
                START:
                    if (cnt == 16'd0) begin
                        if (!rx) begin
                            state <= DATA;
                            cnt   <= CLKS_PER_BIT - 1;
                            bitn  <= 3'd0;
                        end else
                            state <= IDLE;
                    end else
                        cnt <= cnt - 16'd1;
                DATA:
                    if (cnt == 16'd0) begin
                        shreg <= {rx, shreg[7:1]};
                        cnt   <= CLKS_PER_BIT - 1;
                        if (bitn == 3'd7)
                            state <= STOP;
                        else
                            bitn <= bitn + 3'd1;
                    end else
                        cnt <= cnt - 16'd1;
                STOP:
                    if (cnt == 16'd0) begin
                        state <= IDLE;
                        if (rx) begin
                            data  <= shreg;
                            valid <= 1'b1;
                        end
                    end else
                        cnt <= cnt - 16'd1;
            endcase
        end
endmodule

`default_nettype wire
