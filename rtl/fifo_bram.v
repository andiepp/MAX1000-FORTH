// Single-clock first-word-fall-through FIFO in block RAM (M9K on MAX 10).
// dout is valid whenever !empty. The RAM read is synchronous, so a pushed byte becomes
// visible (empty drops) two clocks after the push, when dout already shows it.

`default_nettype none

module fifo_bram #(
    parameter integer WIDTH = 8,
    parameter integer DEPTH_LOG2 = 10
) (
    input  wire             clk,
    input  wire             rst,
    input  wire             push,
    input  wire [WIDTH-1:0] din,
    input  wire             pop,
    output reg  [WIDTH-1:0] dout,
    output wire             empty,
    output wire             full,
    output wire [DEPTH_LOG2:0] count    // bytes readable (as empty sees them)
);

    (* ramstyle = "M9K, no_rw_check" *) reg [WIDTH-1:0] mem [0:(1<<DEPTH_LOG2)-1];
    reg [DEPTH_LOG2:0] wr_ptr = 0, wr_vis = 0, rd_ptr = 0;

    assign empty = (wr_vis == rd_ptr);
    assign count = wr_vis - rd_ptr;
    assign full  = (wr_ptr[DEPTH_LOG2-1:0] == rd_ptr[DEPTH_LOG2-1:0]) &&
                   (wr_ptr[DEPTH_LOG2] != rd_ptr[DEPTH_LOG2]);

    wire               do_pop  = pop && !empty;
    wire [DEPTH_LOG2:0] rd_next = rd_ptr + {{DEPTH_LOG2{1'b0}}, do_pop};

    always @(posedge clk) begin
        if (push && !full)
            mem[wr_ptr[DEPTH_LOG2-1:0]] <= din;
        dout <= mem[rd_next[DEPTH_LOG2-1:0]];
    end

    always @(posedge clk) begin
        if (rst) begin
            wr_ptr <= 0;
            wr_vis <= 0;
            rd_ptr <= 0;
        end else begin
            if (push && !full)
                wr_ptr <= wr_ptr + 1'b1;
            wr_vis <= wr_ptr;
            rd_ptr <= rd_next;
        end
    end

endmodule

`default_nettype wire
