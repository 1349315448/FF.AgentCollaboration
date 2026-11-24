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

    static void Main(string[] args)
    {
        if (args.Contains("--version")) { Console.WriteLine("fixture 2.1"); return; }
        if (args.Contains("app-server")) { WorkbenchEngineFixture.AppServer(); return; }
        if (args.Any(a => a == "--acp" || a == "--headless")) return;
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        SetConsoleMode(GetStdHandle(-10), 0x200);
        SetConsoleMode(GetStdHandle(-11), 7);
        bool copilot = args.Any(a => a.Contains("--additional-mcp-config"));
        bool claude = args.Contains("--mcp-config");
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string draft = "";
        bool newline = false;
        bool approval = false;
        bool swallowed = false;
        bool replaceOnKey = false;
        Action draw = () => {
            string cursor = newline ? "4;2" : "1;" + Math.Min(40, 3 + draft.Length);
            string content = "› " + (draft.Length == 0 ? "Ask Codex to do anything" : draft);
            if (newline) content += "\r\n\r\n  第二段\r\n ";
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
            Console.Write("\x1b[?2004h\x1b[H\x1b[2J" + content + (approval ? "\r\nEnter to select" : "") + "\x1b[" + cursor + "H");
        };
        draw();
        int key;
        while ((key = input.Read()) != -1)
        {
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
                continue;
            }
            if (key == 7) { Console.Write("\x1b[6n\x1b[c\x1b]10;?\x07"); continue; }
            if (key == 12)
            {
                // Simulate native status-bar redraws while the command line remains unchanged.
                for (int i = 0; i < 16; i++) { draw(); System.Threading.Thread.Sleep(250); }
                continue;
            }
            if (key == 21 || key == 24) { draft = ""; newline = approval = false; }
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
                swallowed = false;
                if (mode == "running") { draft = ""; Console.Write("\x1b[H\x1b[2JWorking (esc to interrupt)"); continue; }
                if (mode == "afterkey") continue;
                if (mode == "newtext") { replaceOnKey = true; continue; }
                if (mode == "foreign") { draft = "MSG_ID: deadbeef | MSG: 其他正文"; newline = true; }
                else if ((mode == "retry" && !newline) || mode == "hold" || mode == "approval")
                { newline = true; approval = mode == "approval"; }
                else
                {
                    File.AppendAllText("accepted.txt", draft + Environment.NewLine);
                    draft = ""; newline = approval = false;
                }
            }
            else if (key >= 32)
            {
                if (replaceOnKey) { draft = ""; replaceOnKey = false; }
                draft += (char)key;
            }
            draw();
        }
    }
}
