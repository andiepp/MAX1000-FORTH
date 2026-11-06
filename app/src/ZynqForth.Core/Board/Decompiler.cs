using System.Globalization;
using System.Text;

namespace ZynqForth.Board;

/// <summary>A decompiled word: Forth source and a listing (address, instruction, meaning).</summary>
public sealed record Decompiled(string Name, int CodeAddress, int End, string Source, string Listing);

/// <summary>
/// Turns the HF16 code of a word back into Forth (MAX1000-FORTH's "see"). The instruction set
/// maps almost one-to-one onto Forth: a literal is a number, a call names a word, an ALU
/// instruction is a primitive, and the jumps that IF, ELSE, THEN, BEGIN, UNTIL, AGAIN, WHILE,
/// REPEAT, DO and LOOP compile are recognised by their pattern. Comments and stack comments
/// are not in the code, so they cannot come back.
/// </summary>
public static class Decompiler
{
    // --- the encoding (tools/hf16.py, docs/design.md chapter 3) ---------------------------
    private static readonly Dictionary<string, int> Ops = new()
    {
        ["T"] = 0, ["N"] = 1, ["N+T"] = 2, ["N&T"] = 3, ["N|T"] = 4, ["N^T"] = 5, ["~T"] = 6, ["N=T"] = 7,
        ["N<T"] = 8, ["Nu<T"] = 9, ["N>>T"] = 10, ["N<<T"] = 11, ["R"] = 12, ["[T]"] = 13, ["c[T]"] = 14,
        ["depth"] = 15, ["N-T"] = 16, ["T+1"] = 17, ["T-1"] = 18, ["T=0"] = 19, ["T<0"] = 20, ["N*T"] = 21,
        ["T/2"] = 22, ["-T"] = 23, ["N*Thi"] = 24,
    };

    private static int Delta(int d) => d switch { 1 => 1, -1 => 3, -2 => 2, _ => 0 };

    private static ushort Alu(string op, bool r2p = false, bool t2n = false, bool n2m = false, bool b = false,
                              int rd = 0, int dd = 0) =>
        (ushort)(0x6000 | (r2p ? 0x1000 : 0) | (Ops[op] << 7) | (t2n ? 0x40 : 0) | (n2m ? 0x20 : 0)
                 | (b ? 0x10 : 0) | (Delta(rd) << 2) | Delta(dd));

    private static readonly ushort Exit = Alu("T", r2p: true, rd: -1);
    private static readonly ushort Invert = Alu("~T");
    private static readonly ushort Drop = Alu("N", dd: -1);
    private static readonly ushort Store = Alu("N", n2m: true, dd: -1);
    private static readonly ushort CStore = Alu("N", n2m: true, b: true, dd: -1);
    private static readonly ushort Swap = Alu("N", t2n: true);
    private static readonly ushort ToR = Alu("N", rd: 1, dd: -1);
    private static readonly ushort RFrom = Alu("R", rd: -1, dd: 1);
    private static readonly ushort RFetch = Alu("R", dd: 1);

    private static readonly Dictionary<ushort, string> Prims = new()
    {
        [Alu("T", dd: 1)] = "dup", [Drop] = "drop", [Swap] = "swap", [Alu("N", dd: 1)] = "over",
        [Alu("T", dd: -1)] = "nip", [ToR] = ">r", [RFrom] = "r>", [RFetch] = "r@",
        [Alu("N+T", dd: -1)] = "+", [Alu("N-T", dd: -1)] = "-", [Alu("N&T", dd: -1)] = "and",
        [Alu("N|T", dd: -1)] = "or", [Alu("N^T", dd: -1)] = "xor", [Alu("N=T", dd: -1)] = "=",
        [Alu("N<T", dd: -1)] = "<", [Alu("Nu<T", dd: -1)] = "u<", [Alu("N>>T", dd: -1)] = "rshift",
        [Alu("N<<T", dd: -1)] = "lshift", [Alu("N*T", dd: -1)] = "*", [Alu("N*Thi", dd: -1)] = "(um*)",
        [Invert] = "invert", [Alu("T+1")] = "1+", [Alu("T-1")] = "1-", [Alu("T=0")] = "0=",
        [Alu("T<0")] = "0<", [Alu("T/2")] = "2/", [Alu("-T")] = "negate", [Alu("[T]")] = "@",
        [Alu("c[T]")] = "c@", [Alu("depth", dd: 1)] = "depth", [Alu("T")] = "noop",
    };

    private enum Kind { Literal, Call, Jump, ZBranch, Prim, Exit, Text, Unknown }

    private sealed class Op
    {
        public int Address;            // byte address of the first cell
        public int Cells = 1;
        public Kind Kind;
        public string Text = "";
        public int Target;             // jumps and calls: byte address
        public int Value;              // literals
        public bool Hidden;            // part of a pattern shown elsewhere (do, unloop)
        public bool EndsWord;          // a return merged into the instruction, or a tail call
    }

    /// <summary>Reads a word's code from the board and decompiles it.</summary>
    public static async Task<Decompiled> SeeAsync(BoardClient client, WordList list, ForthWord word)
    {
        int end = CodeEnd(list, word);
        int cells = Math.Max(1, (end - word.CodeAddress) / 2);
        var code = new List<ushort>(cells);
        for (int a = word.CodeAddress; code.Count < cells;)
        {
            int n = Math.Min(256, cells - code.Count);
            code.AddRange(await client.MemoryAsync(a, n));
            a += 2 * n;
        }

        return Decompile(list, word, code);
    }

    /// <summary>
    /// Where the word's code ends: at the header of the next word in memory, or at HERE for
    /// the newest. A header is link (2 bytes), count (1), name, padded to an even address.
    /// </summary>
    public static int CodeEnd(WordList list, ForthWord word)
    {
        int end = list.Here;
        foreach (ForthWord w in list.Words)
        {
            int header = w.CodeAddress - ((3 + Encoding.ASCII.GetByteCount(w.Name) + 1) & ~1);
            if (header > word.CodeAddress && header < end)
            {
                end = header;
            }
        }

        return end;
    }

    public static Decompiled Decompile(WordList list, ForthWord word, IReadOnlyList<ushort> code)
    {
        var names = new Dictionary<int, string>();
        foreach (ForthWord w in list.Words)
        {
            names.TryAdd(w.CodeAddress, w.Name);       // the newest definition of a name wins
        }

        int start = word.CodeAddress;
        int end = start + 2 * code.Count;
        string tail = word.Immediate ? " immediate" : "";

        // data words: CREATE / VARIABLE push the address behind their two instructions,
        // CONSTANT pushes its value; ':' with one number compiles the same, and means the same
        if (code.Count >= 2 && code[0] >= 0x8000 && (code[0] & 0x7FFF) == start + 4 && code[1] == Exit)
        {
            int bytes = end - (start + 4);
            string source = bytes == 2
                ? $"variable {word.Name}   \\ now {Signed(code[2])}"
                : $"create {word.Name}  {bytes} allot   \\ {bytes} bytes of data at {Hex(start + 4)}";
            return new Decompiled(word.Name, start, end, source + tail, Listing(start, code, names));
        }

        if (!word.Immediate && code.Count is 2 or 3 && code[0] >= 0x8000 && code[^1] == Exit && (code.Count == 2 || code[1] == Invert))
        {
            int value = code[0] & 0x7FFF;
            if (code.Count == 3)
            {
                value = ~value & 0xFFFF;
            }

            return new Decompiled(word.Name, start, end, $"{Number(value, names)} constant {word.Name}{tail}",
                                  Listing(start, code, names));
        }

        List<Op> ops = Decode(code, start, end, names);
        Structure(ops, start, end, names);
        return new Decompiled(word.Name, start, end, Render(ops, word, end), Listing(start, code, names));
    }

    // --- decoding --------------------------------------------------------------------------
    private static List<Op> Decode(IReadOnlyList<ushort> code, int start, int end, Dictionary<int, string> names)
    {
        var ops = new List<Op>();
        for (int i = 0; i < code.Count;)
        {
            ushort insn = code[i];
            var op = new Op { Address = start + 2 * i };
            if (insn >= 0x8000)
            {
                int value = insn & 0x7FFF;
                if (i + 1 < code.Count && code[i + 1] == Invert)
                {
                    value = ~value & 0xFFFF;
                    op.Cells = 2;
                }

                op.Kind = Kind.Literal;
                op.Value = value;
                op.Text = Number(value, names);
            }
            else if (insn < 0x6000)
            {
                int target = (insn & 0x1FFF) * 2;
                op.Target = target;
                switch (insn >> 13)
                {
                    case 0:
                        op.Kind = Kind.Jump;
                        break;
                    case 1:
                        op.Kind = Kind.ZBranch;
                        break;
                    default:
                        op.Kind = Kind.Call;
                        string name = names.GetValueOrDefault(target) ?? $"[call {Hex(target)}]";
                        op.Text = name;
                        if (name is "(.\")" or "(s\")")
                        {
                            // a counted string follows the call, padded to an even address
                            int at = op.Address + 2;
                            int len = Byte(code, start, at);
                            var text = new StringBuilder();
                            for (int k = 0; k < len; k++)
                            {
                                text.Append((char)Byte(code, start, at + 1 + k));
                            }

                            int after = (at + 1 + len + 1) & ~1;
                            op.Kind = Kind.Text;
                            op.Text = (name == "(.\")" ? ".\" " : "s\" ") + text + "\"";
                            op.Cells = (after - op.Address) / 2;
                        }

                        break;
                }
            }
            else if (insn == Exit)
            {
                op.Kind = Kind.Exit;
                op.Text = "exit";
            }
            else if ((insn == Store || insn == CStore) && i + 1 < code.Count && (code[i + 1] & ~0x100C) == Drop)
            {
                op.Kind = Kind.Prim;
                op.Text = insn == Store ? "!" : "c!";
                op.Cells = 2;
                op.EndsWord = (code[i + 1] & 0x100C) == 0x100C;
            }
            else
            {
                // a return the cross-compiler merged into the word's last instruction
                bool merged = (insn & 0x1000) != 0 && ((insn >> 2) & 3) == 3;
                ushort plain = merged ? (ushort)(insn & ~0x100C) : insn;
                op.EndsWord = merged;
                op.Kind = Prims.TryGetValue(plain, out string? prim) ? Kind.Prim : Kind.Unknown;
                op.Text = prim ?? $"[alu {Hex(insn)}]";
                if (merged && prim == "noop")
                {
                    op.Kind = Kind.Exit;
                    op.Text = "exit";
                    op.EndsWord = false;
                }
            }

            ops.Add(op);
            i += op.Cells;
        }

        return ops;
    }

    private static int Byte(IReadOnlyList<ushort> code, int start, int address)
    {
        int i = (address - start) / 2;
        if (i < 0 || i >= code.Count)
        {
            return 0;
        }

        return (address & 1) == 0 ? code[i] & 0xFF : code[i] >> 8;
    }

    // --- control structures ----------------------------------------------------------------
    private sealed class Marks
    {
        public readonly Dictionary<int, int> Thens = [];               // address -> count
        public readonly Dictionary<int, string> Begins = [];           // address -> begin / do
    }

    private static Marks marks = new();

    private static void Structure(List<Op> ops, int start, int end, Dictionary<int, string> names)
    {
        marks = new Marks();
        Op? EndingAt(int address) => ops.FirstOrDefault(o => o.Address + 2 * o.Cells == address);

        // DO ... LOOP: swap >r >r | body | call (loop) 0branch(back) r> drop r> drop
        for (int k = 0; k < ops.Count; k++)
        {
            Op op = ops[k];
            if (op.Kind != Kind.ZBranch || op.Target > op.Address || k == 0)
            {
                continue;
            }

            Op prev = ops[k - 1];
            if (prev.Kind == Kind.Call && prev.Text is "(loop)" or "(+loop)")
            {
                prev.Hidden = true;
                op.Kind = Kind.Prim;
                op.Text = prev.Text == "(loop)" ? "loop" : "+loop";
                int t = ops.FindIndex(o => o.Address == op.Target);
                if (t >= 3 && Is(ops[t - 3], "swap") && Is(ops[t - 2], ">r") && Is(ops[t - 1], ">r"))
                {
                    ops[t - 3].Hidden = ops[t - 2].Hidden = ops[t - 1].Hidden = true;
                    marks.Begins[ops[t - 3].Address] = "do";
                }
                else
                {
                    marks.Begins[op.Target] = "do";
                }

                if (k + 4 < ops.Count && Is(ops[k + 1], "r>") && Is(ops[k + 2], "drop") && Is(ops[k + 3], "r>")
                    && Is(ops[k + 4], "drop"))
                {
                    for (int u = 1; u <= 4; u++)
                    {
                        ops[k + u].Hidden = true;
                    }

                    ops[k].EndsWord = ops[k + 4].EndsWord;
                }

                // r@ inside the loop is I
                foreach (Op inner in ops.Where(o => o.Address >= op.Target && o.Address < op.Address && Is(o, "r@")))
                {
                    inner.Text = "i";
                }
            }
        }

        foreach (Op op in ops.Where(o => o.Kind is Kind.ZBranch or Kind.Jump && string.IsNullOrEmpty(o.Text)))
        {
            bool inside = op.Target >= start && op.Target <= end;
            if (op.Kind == Kind.ZBranch && op.Target > op.Address && inside)
            {
                Op? before = EndingAt(op.Target);
                if (before is { Kind: Kind.Jump } && before.Target <= op.Address && before.Target >= start)
                {
                    op.Text = "while";
                    before.Text = "repeat";
                    marks.Begins[before.Target] = "begin";
                }
                else if (before is { Kind: Kind.Jump } && before != op && before.Target >= op.Target
                         && before.Target <= end)
                {
                    op.Text = "if";
                    before.Text = "else";
                    Then(before.Target);
                }
                else
                {
                    op.Text = "if";
                    Then(op.Target);
                }
            }
            else if (op.Kind == Kind.ZBranch && op.Target <= op.Address && inside)
            {
                op.Text = "until";
                marks.Begins.TryAdd(op.Target, "begin");
            }
        }

        foreach (Op op in ops.Where(o => o.Kind == Kind.Jump && string.IsNullOrEmpty(o.Text)))
        {
            bool inside = op.Target >= start && op.Target < end;
            if (inside && op.Target <= op.Address)
            {
                op.Text = "again";
                marks.Begins.TryAdd(op.Target, "begin");
            }
            else if (!inside && names.TryGetValue(op.Target, out string? name))
            {
                op.Text = name;                 // a final call the cross-compiler turned into a jump
                op.EndsWord = true;
            }
            else
            {
                op.Text = $"[jump {Hex(op.Target)}]";
            }
        }

        foreach (Op op in ops.Where(o => o.Kind == Kind.ZBranch && string.IsNullOrEmpty(o.Text)))
        {
            op.Text = $"[0branch {Hex(op.Target)}]";
        }
    }

    private static void Then(int address) => marks.Thens[address] = marks.Thens.GetValueOrDefault(address) + 1;

    private static bool Is(Op op, string text) => op.Kind == Kind.Prim && op.Text == text && !op.Hidden;

    // --- output ----------------------------------------------------------------------------
    private static string Render(List<Op> ops, ForthWord word, int end)
    {
        // the words in order; loop openers and closers are marked for the layout
        var tokens = new List<(string Text, int Loop)>();     // Loop: +1 opens, -1 closes
        bool ended = false;
        for (int k = 0; k < ops.Count; k++)
        {
            Op op = ops[k];
            for (int n = marks.Thens.GetValueOrDefault(op.Address); n > 0; n--)
            {
                tokens.Add(("then", 0));
            }

            if (marks.Begins.TryGetValue(op.Address, out string? begin))
            {
                tokens.Add((begin, 1));
            }

            bool last = k == ops.Count - 1 || ops.Skip(k + 1).All(o => o.Hidden);
            if (!op.Hidden)
            {
                if (op.Text == "exit" && last)
                {
                    ended = true;
                }
                else
                {
                    tokens.Add((op.Text, op.Text is "until" or "again" or "repeat" or "loop" or "+loop" ? -1 : 0));
                }
            }

            if (op.EndsWord && last)
            {
                ended = true;
            }
        }

        for (int n = marks.Thens.GetValueOrDefault(end); n > 0; n--)
        {
            tokens.Add(("then", 0));
        }

        string close = (ended ? ";" : "\\ (no return at the end)") + (word.Immediate ? " immediate" : "");
        string flat = string.Join(' ', tokens.Select(t => t.Text));
        if (flat.Length + word.Name.Length + close.Length < 72)
        {
            return $": {word.Name} {flat} {close}".Replace("  ", " ");
        }

        // long words: one line per loop level, BEGIN/DO start a line, the line ends after
        // UNTIL/AGAIN/REPEAT/LOOP; lines longer than 76 characters wrap
        var lines = new List<string> { ": " + word.Name };
        var line = new StringBuilder();
        int depth = 0;

        void Flush()
        {
            if (line.Length > 0)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
        }

        void Add(string text, int indent)
        {
            if (line.Length > 0 && line.Length + 1 + text.Length > 76)
            {
                Flush();
                indent++;
            }

            if (line.Length == 0)
            {
                line.Append(new string(' ', 2 * indent));
            }
            else
            {
                line.Append(' ');
            }

            line.Append(text);
        }

        foreach ((string text, int loop) in tokens)
        {
            if (loop > 0)
            {
                Flush();
                Add(text, depth + 1);
                depth++;
            }
            else if (loop < 0)
            {
                Add(text, depth + 1);
                depth = Math.Max(0, depth - 1);
                Flush();
            }
            else
            {
                Add(text, depth + 1);
            }
        }

        Add(close, depth + 1);
        Flush();
        return string.Join('\n', lines);
    }

    private static string Listing(int start, IReadOnlyList<ushort> code, Dictionary<int, string> names)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < code.Count; i++)
        {
            ushort insn = code[i];
            string meaning = insn >= 0x8000 ? $"literal {insn & 0x7FFF}"
                : (insn >> 13) switch
                {
                    0 => $"jump {Hex((insn & 0x1FFF) * 2)}",
                    1 => $"0branch {Hex((insn & 0x1FFF) * 2)}",
                    2 => $"call {names.GetValueOrDefault((insn & 0x1FFF) * 2) ?? Hex((insn & 0x1FFF) * 2)}",
                    _ => insn == Exit ? "exit"
                        : Prims.TryGetValue((ushort)(insn & ~0x100C), out string? p) && (insn & 0x1000) != 0 ? p + " + exit"
                        : Prims.TryGetValue(insn, out string? q) ? q
                        : insn == Store ? "! (store)" : insn == CStore ? "c! (store)" : "alu",
                };
            sb.Append(Hex(start + 2 * i)).Append("  ").Append(insn.ToString("X4", CultureInfo.InvariantCulture))
              .Append("  ").Append(meaning).Append('\n');
        }

        return sb.ToString();
    }

    private static string Signed(int value) => ((short)value).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A literal as it would be typed: small and negative numbers in decimal, addresses and
    /// bit patterns ($4000 and above) in hex; the address of a known word as <c>['] name</c>.
    /// </summary>
    private static string Number(int value, Dictionary<int, string> names)
    {
        if (value >= 0x100 && value < 0x4000 && (value & 1) == 0 && names.TryGetValue(value, out string? name))
        {
            return "['] " + name;
        }

        short s = (short)value;
        return s is >= -9999 and < 0x4000 ? s.ToString(CultureInfo.InvariantCulture) : "$" + value.ToString("X4", CultureInfo.InvariantCulture);
    }

    private static string Hex(int value) => "$" + value.ToString("X4", CultureInfo.InvariantCulture);
}
