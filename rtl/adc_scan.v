// Continuous scan of MAX 10 ADC1 channels CH8..CH0 and the temperature sensing diode (TSD),
// using the Quartus Modular ADC control core (altera_modular_adc_control, instantiated
// directly, no Platform Designer). Parameters match the IP GUI settings: 1 MSPS, ADC input
// clock 10 MHz from PLL c0, internal reference (= VCCA 3.3 V on single-supply 10M08SA),
// channels 0..8 enabled; the TSD runs at 50 kSPS from the second clock divider.
module adc_scan (
    input  wire            clk,
    input  wire            rst_n,
    input  wire            pll_c0,
    input  wire            pll_locked,
    output reg [12*10-1:0] codes          // codes[k*12 +: 12] = CHk (k = 0..8), slot 9 = TSD
);

    localparam [4:0] TSD = 5'd17;         // the control core's channel number of the TSD

    reg  [4:0]  ch;
    reg  [1:0]  rep;
    wire        ts = (ch == TSD);
    wire        cmd_ready;
    wire        rsp_valid;
    wire [4:0]  rsp_channel;
    wire [11:0] rsp_data;
    wire        rsp_eop;

    // Avalon-ST command source: cmd_valid always high. Each analog channel is converted 4
    // times in a row and only the last conversion (flagged with eop) is kept, so charge left
    // on the sample capacitor by the previous channel does not leak into the result (matters
    // for floating / high-impedance inputs). After CH0 comes one TSD command; the core keeps
    // a running average of the last 64 TSD samples (it switches the ADC to temperature mode
    // and back by itself).
    always @(posedge clk or negedge rst_n)
        if (!rst_n) begin
            ch  <= 5'd8;
            rep <= 2'd0;
        end else if (cmd_ready) begin
            if (ts) begin
                ch  <= 5'd8;
                rep <= 2'd0;
            end else begin
                rep <= rep + 2'd1;
                if (rep == 2'd3)
                    ch <= (ch == 5'd0) ? TSD : ch - 5'd1;   // CH8 .. CH0, then the TSD
            end
        end

    always @(posedge clk or negedge rst_n)
        if (!rst_n)
            codes <= {12*10{1'b0}};
        else if (rsp_valid && rsp_eop) begin
            if (rsp_channel <= 5'd8)
                codes[rsp_channel*12 +: 12] <= rsp_data;
            else if (rsp_channel == TSD)
                codes[9*12 +: 12] <= rsp_data;
        end

    altera_modular_adc_control #(
        .clkdiv                          (2),          // 10 MHz / 10 = 1 MHz ADC clock (1 MSPS)
        .tsclkdiv                        (1),          // TSD: 1 MHz / 20 = 50 kHz
        .tsclksel                        (1),
        .prescalar                       (0),
        .refsel                          (1),          // internal reference
        .device_partname_fivechar_prefix ("10M08"),
        .is_this_first_or_second_adc     (1),
        .analog_input_pin_mask           (65791),      // 0x100FF: CH0 (bit 16) + CH1..CH8 (bits 0..7), must be an integer
        .hard_pwd                        (0),
        .dual_adc_mode                   (0),
        .enable_usr_sim                  (0)
    ) u_ctrl (
        .clk               (clk),
        .rst_n             (rst_n),
        .clk_in_pll_c0     (pll_c0),
        .clk_in_pll_locked (pll_locked),
        .cmd_valid         (1'b1),
        .cmd_channel       (ch),
        .cmd_sop           (ts || rep == 2'd0),
        .cmd_eop           (ts || rep == 2'd3),
        .sync_ready        (1'b0),
        .cmd_ready         (cmd_ready),
        .rsp_valid         (rsp_valid),
        .rsp_channel       (rsp_channel),
        .rsp_data          (rsp_data),
        .rsp_sop           (),
        .rsp_eop           (rsp_eop),
        .sync_valid        ()
    );

endmodule
