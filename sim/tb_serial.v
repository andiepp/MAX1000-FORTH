// Unit testbench of the serial interfaces (rtl/uart_port.v, spi_port.v, i2c_port.v) at the
// 48 MHz CPU clock, after ZYNQ-FPGA-FORTH32's sim/tb_periph.v. Pin inputs go through a
// two-flop synchronizer as in max1000_forth.v; I2C is open drain with a pull-up.
//   UART 0 TX -> UART 1 RX at 1 MBd and at the smallest divider, all byte values, a FIFO
//        overrun, a framing error, the divider registers
//   SPI  MOSI -> MISO loopback, and a slave model that changes MISO on its shift edges, in
//        modes 0..3, 8/16 bits, MSB and LSB first, at the fastest clock (12 MHz)
//   I2C  against a 24Cxx-style slave model at 0x50 that stretches SCL after each ACK, at
//        400 and 100 kHz
// Run: sim/run_serial.ps1

`timescale 1ns / 1ps

module tb_serial;

    reg clk = 0, rst = 1;
    always #10.4167 clk = ~clk;       // 48 MHz

    integer errors = 0, checks = 0;
    task check(input cond, input [8*64-1:0] what);
        begin
            checks = checks + 1;
            if (!cond) begin
                errors = errors + 1;
                $display("FAIL %0s at %0t", what, $time);
            end
        end
    endtask

    // ------------------------------------------------------------------ UARTs
    reg        u0_we = 0, u1_we = 0, u1_rd = 0;
    reg [2:0]  u_sel = 0;
    reg [15:0] u_wdata = 0;
    wire [15:0] u0_rdata, u1_rdata;
    wire       u0_tx, u1_tx, u0_en, u1_en;
    wire [5:0] p0, p1, p2, p3;
    reg  [1:0] u1_rx_sync = 2'b11;
    always @(posedge clk) u1_rx_sync <= {u1_rx_sync[0], u0_tx};

    uart_port u0 (.clk(clk), .rst(rst), .we(u0_we), .rd(1'b0), .reg_sel(u_sel), .rd_sel(u_sel),
                  .wdata(u_wdata), .rdata(u0_rdata), .rx_in(1'b1), .tx_out(u0_tx),
                  .enable(u0_en), .tx_pin(p0), .rx_pin(p1));
    uart_port u1 (.clk(clk), .rst(rst), .we(u1_we), .rd(u1_rd), .reg_sel(u_sel), .rd_sel(u_sel),
                  .wdata(u_wdata), .rdata(u1_rdata), .rx_in(u1_rx_sync[1]), .tx_out(u1_tx),
                  .enable(u1_en), .tx_pin(p2), .rx_pin(p3));

    task uwrite(input which, input [2:0] r, input [15:0] d);
        begin
            @(negedge clk); u_sel = r; u_wdata = d;
            if (which) u1_we = 1; else u0_we = 1;
            @(negedge clk); u0_we = 0; u1_we = 0;
        end
    endtask

    integer n;
    // take every byte waiting in UART 1, checking that they count up from n
    task udrain;
        begin
            u_sel = 3'd0; @(negedge clk);
            while (u1_rdata[8]) begin
                check(u1_rdata[7:0] == n[7:0], "uart byte value");
                n = n + 1;
                u1_rd = 1; @(negedge clk); u1_rd = 0; @(negedge clk);
            end
        end
    endtask

    // wait until UART 0 has sent everything, then take what UART 1 received
    task uflush;
        begin
            u_sel = 3'd1; @(negedge clk);
            while (!u0_rdata[2]) begin
                udrain;
                u_sel = 3'd1; @(negedge clk);
            end
            repeat (1000) @(negedge clk);
            udrain;
        end
    endtask

    // ------------------------------------------------------------------ SPI
    reg        s_we = 0;
    reg [2:0]  s_sel = 0;
    reg [15:0] s_wdata = 0;
    wire [15:0] s_rdata;
    wire       sck, mosi, s_en;
    wire [5:0] q0, q1, q2;
    reg        use_slave = 0;
    reg        sl_out = 0;
    reg  [1:0] miso_sync = 0;
    always @(posedge clk) miso_sync <= {miso_sync[0], use_slave ? sl_out : mosi};
    spi_port s0 (.clk(clk), .rst(rst), .we(s_we), .reg_sel(s_sel), .rd_sel(s_sel),
                 .wdata(s_wdata), .rdata(s_rdata), .miso_in(miso_sync[1]), .sck_out(sck),
                 .mosi_out(mosi), .enable(s_en), .sck_pin(q0), .mosi_pin(q1), .miso_pin(q2));

    task swrite(input [2:0] r, input [15:0] d);
        begin
            @(negedge clk); s_sel = r; s_wdata = d; s_we = 1;
            @(negedge clk); s_we = 0;
        end
    endtask

    // SCK edges during a transfer, counted for the mode check
    integer sck_edges = 0;
    reg sck_prev = 0;
    always @(posedge clk) begin
        if (sck !== sck_prev) sck_edges = sck_edges + 1;
        sck_prev = sck;
    end

    // slave model: sends sl_val (sl_w bits, MSB or LSB first); with CPHA 0 the first bit is
    // out before the first edge and each trailing edge brings the next one, with CPHA 1
    // each leading edge does; it also captures MOSI on its sampling edges
    reg [15:0] sl_val = 0, sl_got = 0;
    integer    sl_k = 0, sl_w = 8;
    reg        sl_cpol = 0, sl_cpha = 0, sl_lsb = 0, sl_q = 0;
    function sl_bit(input integer k);
        sl_bit = (k < 0 || k >= sl_w) ? 1'b0 : sl_lsb ? sl_val[k] : sl_val[sl_w - 1 - k];
    endfunction
    task sl_load(input [15:0] v);
        begin
            sl_val = v; sl_got = 0;
            sl_k = sl_cpha ? -1 : 0;
            sl_out = sl_bit(sl_k);
        end
    endtask
    always @(posedge clk) begin
        if (sck !== sl_q) begin
            if ((sck != sl_cpol) == sl_cpha) begin          // shift edge
                sl_k = sl_k + 1;
                sl_out = sl_bit(sl_k);
            end else begin                                  // sample edge
                sl_got = sl_lsb ? {mosi, sl_got[15:1]} : {sl_got[14:0], mosi};
            end
        end
        sl_q = sck;
    end

    // ------------------------------------------------------------------ I2C
    reg        i_we = 0;
    reg [1:0]  i_sel = 0;
    reg [15:0] i_wdata = 0;
    wire [15:0] i_rdata;
    wire       m_scl_low, m_sda_low, i_en;
    wire [5:0] r0, r1;
    reg        s_scl_low = 0, s_sda_low = 0;
    wire       scl = !(m_scl_low || s_scl_low);
    wire       sda = !(m_sda_low || s_sda_low);
    reg  [1:0] scl_sync = 2'b11, sda_sync = 2'b11;
    always @(posedge clk) begin scl_sync <= {scl_sync[0], scl}; sda_sync <= {sda_sync[0], sda}; end
    i2c_port i0 (.clk(clk), .rst(rst), .we(i_we), .reg_sel(i_sel), .rd_sel(i_sel),
                 .wdata(i_wdata), .rdata(i_rdata), .scl_in(scl_sync[1]), .sda_in(sda_sync[1]),
                 .scl_low(m_scl_low), .sda_low(m_sda_low), .enable(i_en), .scl_pin(r0),
                 .sda_pin(r1));

    task iwrite(input [1:0] r, input [15:0] d);
        begin
            @(negedge clk); i_sel = r; i_wdata = d; i_we = 1;
            @(negedge clk); i_we = 0;
        end
    endtask
    task icmd(input [2:0] c, input [7:0] b);
        begin
            iwrite(2'd0, {5'd0, c, b});
            @(negedge clk); i_sel = 2'd0;
            while (i_rdata[15]) @(negedge clk);
        end
    endtask

    // slave model: 24Cxx style at address 0x50, an address pointer, 256 bytes; it holds SCL
    // low for 3 us after each ACK it gives (clock stretching)
    reg [7:0] mem [0:255];
    reg [7:0] ptr = 0, sh = 0;
    integer   bitc = 0, state = 0;   // 0 idle, 1 address, 2 pointer, 3 data in, 4 data out
    integer   phase = 0;             // 0 data bits, 1 our ACK bit, 2 the master's ACK bit
    reg       master_ack = 0;
    always @(negedge sda) if (scl) begin state = 1; bitc = 0; phase = 0; s_sda_low = 0; end
    always @(posedge sda) if (scl) begin state = 0; phase = 0; s_sda_low = 0; end
    always @(posedge scl) begin
        if (phase == 0 && state >= 1 && state <= 3) begin
            sh = {sh[6:0], sda};
            bitc = bitc + 1;
        end else if (phase == 2) begin
            master_ack = !sda;
        end
    end
    always @(negedge scl) begin
        if (state == 0) begin
            s_sda_low = 0;
        end else if (phase == 1) begin           // our ACK bit is over
            phase = 0;
            s_sda_low = 0;
            if (state == 4) begin sh = mem[ptr]; bitc = 0; s_sda_low = !sh[7]; end
        end else if (phase == 2) begin           // the master's ACK bit is over
            phase = 0;
            if (master_ack) begin ptr = ptr + 1; sh = mem[ptr]; bitc = 0; s_sda_low = !sh[7]; end
            else begin state = 0; s_sda_low = 0; end
        end else if (state == 4) begin           // a bit of ours was read
            bitc = bitc + 1;
            if (bitc == 8) begin s_sda_low = 0; phase = 2; end
            else s_sda_low = !sh[7 - bitc];
        end else if (bitc == 8) begin            // a byte for us is complete
            bitc = 0;
            case (state)
                1: if (sh[7:1] == 7'h50) begin
                       phase = 1; s_sda_low = 1;
                       state = sh[0] ? 4 : 2;
                   end else state = 0;
                2: begin ptr = sh; phase = 1; s_sda_low = 1; state = 3; end
                3: begin mem[ptr] = sh; ptr = ptr + 1; phase = 1; s_sda_low = 1; end
            endcase
            if (phase == 1) begin
                s_scl_low = 1; #3000; s_scl_low = 0;
            end
        end
    end

    // I2C transaction: write 3 bytes at pointer p, read them back with a repeated start
    task i2c_round(input [7:0] p, input [7:0] a, input [7:0] b, input [7:0] c);
        begin
            icmd(3'd1, 0);                       // start
            icmd(3'd3, 8'hA0);                   // 0x50, write
            check(i_rdata[8] == 1, "i2c address ack");
            icmd(3'd3, p);                       // pointer
            icmd(3'd3, a); icmd(3'd3, b); icmd(3'd3, c);
            check(i_rdata[8] == 1, "i2c data ack");
            icmd(3'd2, 0);                       // stop
            check(mem[p] == a && mem[p + 8'd1] == b && mem[p + 8'd2] == c, "i2c slave got the bytes");
            icmd(3'd1, 0); icmd(3'd3, 8'hA0); icmd(3'd3, p);
            icmd(3'd1, 0);                       // repeated start
            icmd(3'd3, 8'hA1);                   // 0x50, read
            check(i_rdata[8] == 1, "i2c read address ack");
            icmd(3'd4, 0); check(i_rdata[7:0] == a, "i2c read byte 1");
            icmd(3'd4, 0); check(i_rdata[7:0] == b, "i2c read byte 2");
            icmd(3'd5, 0); check(i_rdata[7:0] == c, "i2c read byte 3 (nack)");
            icmd(3'd2, 0);
            check(i_rdata[9] == 0, "i2c no timeout");
        end
    endtask

    // ------------------------------------------------------------------ the tests
    integer i, m, mode, w, lsbf;
    reg [15:0] v, expect;

    initial begin
        for (i = 0; i < 256; i = i + 1) mem[i] = i ^ 8'hA5;
        repeat (4) @(posedge clk);
        rst = 0;

        // UART: reset divider 417 (115200 Bd); the high register; 1 MBd (48 clocks)
        u_sel = 3'd2; @(negedge clk);
        check(u0_rdata == 16'd417, "uart divider after reset");
        uwrite(0, 3'd3, 16'h0012); u_sel = 3'd3; @(negedge clk);
        check(u0_rdata == 16'h0012, "uart divider high register");
        uwrite(0, 3'd3, 0);
        uwrite(0, 3'd2, 48); uwrite(1, 3'd2, 48);
        uwrite(0, 3'd4, 16'h8000); uwrite(1, 3'd4, 16'h8000);
        n = 0;
        for (i = 0; i < 256; i = i + 1) begin
            u_sel = 3'd1; @(negedge clk);
            while (!u0_rdata[1]) @(negedge clk);          // room in TX
            uwrite(0, 3'd0, i);
            udrain;
        end
        uflush;
        check(n == 256, "uart 256 bytes received at 1 MBd");
        u_sel = 3'd1; @(negedge clk);
        check(u1_rdata[3] == 0 && u1_rdata[4] == 0, "uart no overrun, no framing error");
        check(u0_rdata[2] == 1, "uart transmitter idle");

        // divider below 8 works as 8 (6 MBd): every byte value again
        uwrite(0, 3'd2, 3); uwrite(1, 3'd2, 3);
        n = 0;
        for (i = 0; i < 256; i = i + 1) begin
            u_sel = 3'd1; @(negedge clk);
            while (!u0_rdata[1]) @(negedge clk);
            uwrite(0, 3'd0, i);
            udrain;
        end
        uflush;
        check(n == 256, "uart 256 bytes at the smallest divider");
        u_sel = 3'd2; @(negedge clk);
        check(u0_rdata == 16'd3, "uart divider reads back as written");

        // overrun: 1030 bytes into the 1 KB FIFO without reading
        for (i = 0; i < 1030; i = i + 1) begin
            u_sel = 3'd1; @(negedge clk);
            while (!u0_rdata[1]) @(negedge clk);
            uwrite(0, 3'd0, i);
        end
        u_sel = 3'd1; @(negedge clk);
        while (!u0_rdata[2]) @(negedge clk);              // all sent
        repeat (200) @(negedge clk);
        check(u1_rdata[3] == 1 && u1_rdata[0] == 1, "uart overrun flagged");
        uwrite(1, 3'd1, 0);
        u_sel = 3'd1; @(negedge clk);
        check(u1_rdata[3] == 0, "uart overrun cleared");
        n = 0; udrain;
        check(n == 1024, "uart FIFO held 1024 bytes");

        // framing error: the receiver runs twice as fast as the sender, a 0x00 byte
        uwrite(0, 3'd2, 48); uwrite(1, 3'd2, 24);
        uwrite(0, 3'd0, 0);
        repeat (1000) @(negedge clk);
        u_sel = 3'd1; @(negedge clk);
        check(u1_rdata[4] == 1, "uart framing error flagged");
        uwrite(1, 3'd4, 0);                               // disabling empties and clears
        uwrite(1, 3'd4, 16'h8000);
        u_sel = 3'd0; @(negedge clk);
        check(u1_rdata[8] == 0, "uart disable empties the FIFO");
        $display("[tb_serial] uart: 1 MBd and 6 MBd, overrun after 1024 bytes, framing error");

        // SPI: every mode, width and bit order at half = 2 (12 MHz), loopback and slave
        swrite(3'd3, 1);                                  // works as 2
        swrite(3'd4, 16'h8000);
        for (mode = 0; mode < 4; mode = mode + 1)
            for (w = 0; w < 2; w = w + 1)
                for (lsbf = 0; lsbf < 2; lsbf = lsbf + 1) begin
                    swrite(3'd2, (w << 4) | (lsbf << 2) | mode);
                    sl_cpol = mode[1]; sl_cpha = mode[0]; sl_lsb = lsbf; sl_w = w ? 16 : 8;
                    repeat (3) @(negedge clk);           // SCK takes its idle level
                    for (m = 0; m < 8; m = m + 1) begin
                        use_slave = m[0];
                        v = $random;
                        expect = w ? v : {8'd0, v[7:0]};
                        sl_load(m[0] ? ~v : 16'd0);
                        if (m[0]) expect = w ? ~v : {8'd0, ~v[7:0]};
                        sck_edges = 0;
                        swrite(3'd0, v);
                        s_sel = 3'd1; @(negedge clk);
                        while (s_rdata[0]) @(negedge clk);
                        s_sel = 3'd0; @(negedge clk);
                        check(s_rdata == expect, m[0] ? "spi value from the slave" : "spi loopback value");
                        if (m[0]) check((w ? sl_got : lsbf ? sl_got[15:8] : sl_got[7:0])
                                        == (w ? v : v[7:0]), "spi slave got MOSI");
                        check(sck_edges == 2 * (w ? 16 : 8), "spi sck edges");
                        check(sck == mode[1], "spi sck idle level");
                    end
                end
        swrite(3'd3, 24); s_sel = 3'd3; @(negedge clk);
        check(s_rdata == 16'd24, "spi half register");
        $display("[tb_serial] spi: modes 0-3, 8/16 bits, MSB/LSB first at 12 MHz, loopback and slave");

        // I2C at 400 kHz (quarter 30) and 100 kHz (quarter 120)
        iwrite(2'd2, 16'h8000 | (43 << 8) | 42);
        iwrite(2'd1, 30);
        i2c_round(8'h10, 8'h11, 8'h22, 8'h33);
        iwrite(2'd1, 120);
        i2c_round(8'hFE, 8'h5A, 8'hC3, 8'h00);           // the pointer wraps
        icmd(3'd1, 0); icmd(3'd3, 8'hB0);                 // nobody at 0x58
        check(i_rdata[8] == 0, "i2c no ack from an absent address");
        icmd(3'd2, 0);
        check(scl && sda, "i2c bus released after stop");
        iwrite(2'd1, 1); i_sel = 2'd1; @(negedge clk);
        check(i_rdata == 16'd1, "i2c quarter reads back as written");
        $display("[tb_serial] i2c: write, repeated start, read with ACK/NACK, stretching, 400/100 kHz");

        $display("[tb_serial] %0d checks, %0d failed", checks, errors);
        $finish;
    end

endmodule
