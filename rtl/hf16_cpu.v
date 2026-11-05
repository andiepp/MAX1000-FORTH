// HF16: 16-bit dual-stack Forth CPU, one instruction per clock.
// Encoding and semantics are documented in docs/design.md.
//
// Memory is a synchronous dual-port RAM outside this module:
//   code port: code_addr is the *next* PC, insn arrives with the PC (registered RAM output)
//   data port: mem_addr is the *next* T, so mem_rdata holds [T] during the following
//              instruction. A store cycle drives mem_addr = T instead (store hazard, see doc).
//
// stall freezes the CPU between instructions: no register changes, no store, no I/O pop;
// the code port re-reads the current instruction and the data port re-reads [T], so the
// RAM outputs are valid again one cycle after the data port was lent to someone else.

`default_nettype none

module hf16_cpu (
    input  wire        clk,
    input  wire        rst,
    input  wire        stall,

    output wire [12:0] code_addr,
    input  wire [15:0] insn,

    output wire [15:0] mem_addr,
    output wire        mem_we,
    output wire        mem_byte,
    output wire [15:0] mem_wdata,
    input  wire [15:0] mem_rdata,
    output wire        mem_rd,     // this instruction consumes [T] (for I/O pop side effects)

    output wire [15:0] t_out,      // T, the address of the current load/store
    output wire [12:0] pc_out
);

    reg  [12:0] pc  = 13'd0;
    reg  [15:0] st0 = 16'd0;       // T
    reg  [4:0]  dsp = 5'd0;
    reg  [4:0]  rsp = 5'd0;

    reg  [15:0] dstack [0:31];
    reg  [15:0] rstack [0:31];

    wire [15:0] st1  = dstack[dsp]; // N
    wire [15:0] rst0 = rstack[rsp]; // R

    wire        is_alu = (insn[15:13] == 3'b011);
    wire [4:0]  op     = insn[11:7];

    // ------------------------------------------------------------------
    // ALU: new T for ALU instructions
    // ------------------------------------------------------------------
    wire        shift_big = |st0[15:4];
    wire [31:0] product   = st1 * st0;   // unsigned; the low half is also the signed product
    reg  [15:0] alu;

    always @* begin
        case (op)
            5'd0:  alu = st0;
            5'd1:  alu = st1;
            5'd2:  alu = st1 + st0;
            5'd3:  alu = st1 & st0;
            5'd4:  alu = st1 | st0;
            5'd5:  alu = st1 ^ st0;
            5'd6:  alu = ~st0;
            5'd7:  alu = {16{st1 == st0}};
            5'd8:  alu = {16{$signed(st1) < $signed(st0)}};
            5'd9:  alu = {16{st1 < st0}};
            5'd10: alu = shift_big ? 16'd0 : st1 >> st0[3:0];
            5'd11: alu = shift_big ? 16'd0 : st1 << st0[3:0];
            5'd12: alu = rst0;
            5'd13: alu = mem_rdata;
            5'd14: alu = {8'd0, st0[0] ? mem_rdata[15:8] : mem_rdata[7:0]};
            5'd15: alu = {11'd0, dsp};
            5'd16: alu = st1 - st0;
            5'd17: alu = st0 + 16'd1;
            5'd18: alu = st0 - 16'd1;
            5'd19: alu = {16{st0 == 16'd0}};
            5'd20: alu = {16{st0[15]}};
            5'd21: alu = product[15:0];
            5'd22: alu = {st0[15], st0[15:1]};
            5'd23: alu = 16'd0 - st0;
            5'd24: alu = product[31:16];
            default: alu = st0;
        endcase
    end

    // ------------------------------------------------------------------
    // Next state
    // ------------------------------------------------------------------
    wire [12:0] pc_plus1 = pc + 13'd1;

    reg  [15:0] st0_n;
    reg  [4:0]  dsp_n, rsp_n;
    reg  [12:0] pc_n;
    reg         dstk_we, rstk_we;
    reg  [15:0] rstk_wd;

    always @* begin
        st0_n   = st0;
        dsp_n   = dsp;
        rsp_n   = rsp;
        pc_n    = pc_plus1;
        dstk_we = 1'b0;
        rstk_we = 1'b0;
        rstk_wd = st0;

        if (insn[15]) begin                     // literal
            st0_n   = {1'b0, insn[14:0]};
            dsp_n   = dsp + 5'd1;
            dstk_we = 1'b1;
        end else begin
            case (insn[14:13])
                2'b00: pc_n = insn[12:0];        // jump
                2'b01: begin                     // 0branch
                    st0_n = st1;
                    dsp_n = dsp - 5'd1;
                    if (st0 == 16'd0)
                        pc_n = insn[12:0];
                end
                2'b10: begin                     // call
                    rsp_n   = rsp + 5'd1;
                    rstk_we = 1'b1;
                    rstk_wd = {2'b00, pc_plus1, 1'b0};
                    pc_n    = insn[12:0];
                end
                2'b11: begin                     // ALU
                    st0_n   = alu;
                    dsp_n   = dsp + {{3{insn[1]}}, insn[1:0]};
                    rsp_n   = rsp + {{3{insn[3]}}, insn[3:2]};
                    dstk_we = insn[6] | (insn[1:0] == 2'b01);
                    rstk_we = (insn[3:2] == 2'b01);
                    if (insn[12])
                        pc_n = rst0[13:1];
                end
            endcase
        end

        if (rst)
            pc_n = 13'd0;
    end

    always @(posedge clk) begin
        if (rst) begin
            pc  <= 13'd0;
            st0 <= 16'd0;
            dsp <= 5'd0;
            rsp <= 5'd0;
        end else if (!stall) begin
            pc  <= pc_n;
            st0 <= st0_n;
            dsp <= dsp_n;
            rsp <= rsp_n;
            if (dstk_we) dstack[dsp_n] <= st0;
            if (rstk_we) rstack[rsp_n] <= rstk_wd;
        end
    end

    // ------------------------------------------------------------------
    // Memory ports
    // ------------------------------------------------------------------
    assign code_addr = stall ? pc : pc_n;
    assign mem_we    = !rst && !stall && is_alu && insn[5];
    assign mem_byte  = insn[4];
    assign mem_wdata = st1;
    assign mem_addr  = (mem_we || stall) ? st0 : st0_n;
    assign mem_rd    = !rst && !stall && is_alu && (op == 5'd13 || op == 5'd14);
    assign t_out     = st0;
    assign pc_out    = pc;

endmodule

`default_nettype wire
