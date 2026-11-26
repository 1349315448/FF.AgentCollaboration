using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// Controlled ConPTY line editor. The first Enter can leave the draft visible or open a choice.
class TopicTerminalFixture
{
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool ReadConsoleW(IntPtr handle, [Out] char[] buffer, int length, out int read, IntPtr reserved);

    // Match the native Windows editor's UTF-16 console input. The Framework byte stream
    // converts each surrogate to U+FFFD before StreamReader gets to decode it.
    sealed class EditorInput : TextReader
    {
        readonly char[] buffer = new char[256];
        readonly IntPtr console = GetStdHandle(-10);
        int offset, count;
        public override int Read()
        {
            if (offset == count)
            {
                offset = 0;
                if (!ReadConsoleW(console, buffer, buffer.Length, out count, IntPtr.Zero) || count == 0) return -1;
            }
            return buffer[offset++];
        }
    }

    // Run the test reply as the receiving CLI, inheriting only its own workbench grant.
    static void ReplyToPeer(Action draw)
    {
        const string request = "mcp-reply.fixture";
        if (!File.Exists(request)) return;
        // The editor accepts the input before its model can ask the MCP server for a reply.
        draw();
        var lines = File.ReadAllLines(request);
        File.Delete(request);
        var start = new System.Diagnostics.ProcessStartInfo {
            FileName = Environment.GetEnvironmentVariable("FF_TEST_WORKBENCH_EXE"),
            Arguments = "workbench-mcp \"" + Environment.GetEnvironmentVariable("FF_TEST_WORKBENCH_STATE") + "\" --notify-port " + int.Parse(lines[0]),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        using (var child = System.Diagnostics.Process.Start(start))
        {
            child.StandardInput.WriteLine(lines[1]);
            child.StandardInput.Close();
            File.WriteAllText("mcp-reply.tmp", child.StandardOutput.ReadToEnd());
            File.Move("mcp-reply.tmp", "mcp-reply.json");
        }
    }

    static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "login" || args[0] == "auth" || args[0] == "status")) { Environment.ExitCode = WorkbenchEngineFixture.Main(args); return; }
        if (args.Contains("--version")) { Console.WriteLine("fixture 2.1"); return; }
        if (args.Contains("--help") || args.Contains("--stdio")) { WorkbenchEngineFixture.Main(args); return; }
        if (args.Contains("app-server")) { WorkbenchEngineFixture.AppServer(); return; }
        if (args.Contains("--headless")) { WorkbenchEngineFixture.Main(args); return; }
        if (args.Contains("--acp")) { WorkbenchEngineFixture.Main(args); return; }
        File.WriteAllLines("terminal-launch.txt", args);
        File.AppendAllText("terminal-pids.txt", System.Diagnostics.Process.GetCurrentProcess().Id + Environment.NewLine);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        SetConsoleMode(GetStdHandle(-10), 0x200);
        SetConsoleMode(GetStdHandle(-11), 7);
        bool copilot = args.Any(a => a.Contains("--additional-mcp-config"));
        bool claude = args.Contains("--mcp-config");
        var native = File.Exists("native-prompt.fixture") ? File.ReadAllText("native-prompt.fixture").Trim() : "";
        bool busy = false;
        var input = new EditorInput();
        string draft = "";
        bool newline = false;
        bool approval = false;
        bool swallowed = false;
        bool folded = false, collectingFold = false;
        bool truncatedPaste = false;
        bool collectingRepair = false;
        System.Threading.Timer statusTicker = null;
        bool replaceOnKey = false;
        Action draw = () => {
            string cursor = newline ? "4;2" : "1;" + Math.Min(40, 3 + draft.Length);
            string content = "› " + (draft.Length == 0 ? "\x1b[2mAsk Codex to do anything\x1b[22m" : draft);
            if (newline)
            {
                // 原生第一次回车只增加换行。把同一份草稿跨空行重绘，保持正文与初次粘贴逐字一致。
                int split = Math.Max(0, draft.LastIndexOf(" | ", StringComparison.Ordinal));
                content = "› " + draft.Substring(0, split) + "\r\n\r\n " + draft.Substring(split) + "\r\n ";
            }
            if (folded || busy && native == "")
            {
                int chars = 0;
                foreach (char value in draft) if (!char.IsLowSurrogate(value)) chars++;
                string editor = folded && chars > 0 ? "[Pasted Content " + chars + " chars]" : draft;
                string status = "Reading code (12m 39s • esc to interrupt) · 1 background terminal running · /ps to view";
                int statusRows = busy ? (status.Length - 1) / Math.Max(1, Console.WindowWidth) + 1 : 0;
                content = (busy ? status + "\r\n" : "") + "› " + editor +
                    "\r\nGPT-fixture · model: test" + (busy ? "\r\ntab to queue message" : "");
                cursor = (statusRows + 1) + ";" + (3 + editor.Length);
            }
            if (copilot)
            {
                content = "Session: 24.00 AIC used\r\n──────────────────────────\r\n> " + draft +
                    (newline ? "\r\n" : "") + "\r\n──────────────────────────";
                cursor = newline ? "4;2" : "3;" + Math.Min(40, 3 + draft.Length);
            }
            if (claude)
            {
                content = "──────────────────────────\r\n❯ " + draft + "\r\n──────────────────────────\r\n? for shortcuts";
                cursor = "2;" + (3 + draft.Length);
            }
            if (native == "qwen" || native == "codebuddy" || native == "qoder")
            {
                string indicator = native == "qwen" ? "⠋ Thinking (12s · esc to cancel)" :
                    native == "qoder" ? "Generating… (esc to cancel)" : "∴ Thinking...";
                string suggested = draft.Length > 0 ? draft : native == "qwen"
                    ? "\x1b[4m再\x1b[24;38;2;108;112;134m测试一下投递 @码农GPT\x1b[0m"
                    : native == "codebuddy" ? "\x1b[38;5;243mCheck reply delivery status\x1b[39m    ↵ send"
                    : "\x1b[7m查\x1b[0;2m询回执投递状态\x1b[0m";
                content = (busy ? indicator : "✓ Worked for 15s") + "\r\n──────────────────────────\r\n> " + suggested +
                    "\r\n──────────────────────────\r\n" + (native == "qwen" ? "qwen4-max · 1.0m Context 1.9% used" :
                    native == "qoder" ? "? for shortcuts" : "hy4flash-free · ? for shortcuts");
                cursor = "3;" + Math.Min(40,3 + draft.Length);
            }
            if (native == "zcode")
            {
                const string muted = "\x1b[38;2;140;147;161m";
                content = "╭─ Build ────────────────╮\r\n│ " + (draft.Length > 0 ? draft : muted + "Type a prompt\x1b[39m") +
                    " │\r\n│                        │\r\n│ glm " + muted + "zai | \x1b[39mhigh         │\r\n╰────────────────────────╯\r\n" +
                    (busy ? "⠋ esc to interrupt" : "7%");
                cursor = "2;" + Math.Min(40,3 + draft.Length);
            }
            if (native == "traecli")
            {
                content = (busy ? "❖ Working… (0s • esc to interrupt)\r\n" : "\r\n") +
                    "──────────────────────────\r\n❯ " +
                    (draft.Length > 0 ? draft : "\x1b[2mAsk TRAE CLI to do anything\x1b[22m") +
                    "\r\n──────────────────────────\r\n? for shortcuts";
                cursor = "3;" + Math.Min(40, 3 + draft.Length);
            }
            Console.Write("\x1b[?2004h\x1b[H\x1b[2J" + content + (approval ? "\r\nEnter to select" : "") + "\x1b[" + cursor + "H");
        };
        draw();
        int key;
        while ((key = input.Read()) != -1)
        {
            if (key == 9) { File.AppendAllText("approval-key.txt", "Tab\n"); continue; }
            if (key == 27)
            {
                // Consume CSI and OSC as packets; the log distinguishes live replies from stale replay replies.
                int prefix = input.Read();
                var seq = new StringBuilder();
                int c;
                if (prefix == '[')
                {
                    while ((c = input.Read()) >= 0) { seq.Append((char)c); if (c >= 64 && c <= 126) break; }
                }
                else if (prefix == ']')
                {
                    while ((c = input.Read()) >= 0 && c != 7) { if (c == 27) { input.Read(); break; } seq.Append((char)c); }
                }
                if (seq.ToString().EndsWith("R") || seq.ToString().EndsWith("c") || prefix == ']')
                    File.AppendAllText("reports.txt", seq + Environment.NewLine);
                if (prefix == '[' && seq.ToString() == "Z") File.AppendAllText("approval-key.txt", "Shift+Tab\n");
                if (seq.ToString() == "200~" && File.Exists("mode.txt"))
                {
                    var mode = File.ReadAllText("mode.txt").Trim();
                    collectingFold = mode.StartsWith("folded-");
                    collectingRepair = mode == "repair-paste";
                }
                if (seq.ToString() == "201~" && collectingFold)
                {
                    collectingFold = false; folded = true;
                    File.WriteAllText("folded-editor.txt", draft);
                    draw();
                    if (File.ReadAllText("mode.txt").Trim() == "folded-approval")
                    {
                        System.Threading.Thread.Sleep(420);
                        approval = true; draw();
                    }
                }
                if (seq.ToString() == "201~" && collectingRepair)
                {
                    collectingRepair = false;
                    if (!truncatedPaste && draft.Length > 24)
                    {
                        draft = draft.Substring(0, draft.Length - 12);
                        truncatedPaste = true;
                    }
                    draw();
                }
                if (seq.ToString() == "201~" && File.Exists("mode.txt") && File.ReadAllText("mode.txt").Trim() == "ticker-retry")
                    statusTicker = new System.Threading.Timer(_ => Console.Write("\x1b[?25h"), null, 0, 100);
                continue;
            }
            if (key == 7) { Console.Write("\x1b[6n\x1b[c\x1b]10;?\x07"); continue; }
            if (key == 12)
            {
                // Simulate native status-bar redraws while the command line remains unchanged.
                for (int i = 0; i < 16; i++) { draw(); System.Threading.Thread.Sleep(250); }
                continue;
            }
            if (key == 2) busy = true;
            else if (key == 3)
            {
                File.AppendAllText("clears.txt", draft + Environment.NewLine);
                draft = ""; newline = approval = folded = false;
            }
            else if (key == 21 || key == 24) { draft = ""; newline = approval = busy = folded = false; }
            else if (key == 13 || key == 10)
            {
                string mode = File.Exists("mode.txt") ? File.ReadAllText("mode.txt").Trim() : "submit";
                File.AppendAllText("enters.txt", mode + Environment.NewLine);
                if (mode == "delayed")
                {
                    Console.Write("\x1b[H\x1b[2JWorking");
                    System.Threading.Thread.Sleep(4500);
                }
                if (mode == "silent" && !swallowed) { swallowed = true; continue; }
                if (mode == "folded-retry" && !swallowed) { swallowed = true; continue; }
                if (mode == "folded-changed") { draft += "!"; draw(); continue; }
                if (mode == "ticker-retry" && !swallowed) { swallowed = true; draw(); continue; }
                swallowed = false;
                if (statusTicker != null) { statusTicker.Dispose(); statusTicker = null; }
                if (mode == "running") { draft = ""; Console.Write("\x1b[H\x1b[2JWorking (esc to interrupt)"); continue; }
                if (mode == "afterkey") continue;
                if (mode == "newtext") { replaceOnKey = true; continue; }
                if (mode == "foreign") { draft = "MSG_ID: deadbeef | MSG: 其他正文"; newline = true; }
                else if ((mode == "retry" && !newline) || mode == "hold" || mode == "approval")
                { newline = true; approval = mode == "approval"; }
                else
                {
                    File.AppendAllText("accepted.txt", draft + Environment.NewLine);
                    draft = ""; newline = approval = folded = false;
                    if (mode == "folded-retry") busy = false;
                    ReplyToPeer(draw);
                }
            }
            else if (key >= 32)
            {
                if (replaceOnKey) { draft = ""; replaceOnKey = false; }
                draft += (char)key;
            }
            if (!collectingFold && !collectingRepair) draw();
        }
    }
}
