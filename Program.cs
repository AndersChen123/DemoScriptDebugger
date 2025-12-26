using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Demo;

class Program {
    // Pause info queued by script thread and handled by main thread (console I/O)
    class PauseInfo {
        public int PauseId { get; set; }
        public ScriptFrame Frame { get; set; } = null!;
        public int ThreadId { get; set; }
    }

    static void Main(string[] args) {
        // detect whether Console.KeyAvailable is supported in this environment
        bool supportsKeyAvailable;
        try {
            var _ = Console.KeyAvailable;
            supportsKeyAvailable = true;
        }
        catch (InvalidOperationException) {
            supportsKeyAvailable = false;
            Console.WriteLine("Note: non-interactive console detected; pause UI will be limited (cannot preempt blocking input).");
        }

        var scriptPath = "script.cs";
        if (!File.Exists(scriptPath)) {
            Console.WriteLine("Create a script.cs next to the executable with a public static void Run() method.");
            return;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "ScriptDebugDemo");
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "UserScript.dll");
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

        Console.WriteLine("Instrumenting and compiling...");
        var result = InstrumentationService.InstrumentAndCompile(scriptPath, dllPath, pdbPath);
        Console.WriteLine($"Wrote {dllPath}");
        Console.WriteLine();

        Console.WriteLine("Checkpoint mapping (id -> file:line:col):");
        foreach (var kv in result.CheckpointMap.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key} -> {kv.Value.FilePath}:{kv.Value.Line}:{kv.Value.Column}");
        Console.WriteLine();

        var dbg = new ScriptDebugger(result.MethodCheckpointMap);
        ScriptDebuggerGlobal.Host = dbg;

        var activeBreakpoints = new HashSet<int>();

        var pauseQueue = new BlockingCollection<PauseInfo>();

        dbg.Paused += (pauseId, frame, threadId) => {
            pauseQueue.Add(new PauseInfo { PauseId = pauseId, Frame = frame, ThreadId = threadId });
        };

        var alc = new AssemblyLoadContext("script-alc", isCollectible: true);
        var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
        var runType = asm.GetTypes().FirstOrDefault(t => t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) != null);
        if (runType == null) {
            Console.WriteLine("No public static Run() method found in script.");
            return;
        }
        var runMethod = runType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        var scriptThread = new Thread(() => {
            try {
                runMethod.Invoke(null, null);
            }
            catch (TargetInvocationException tie) {
                Console.WriteLine("Script exception: " + tie.InnerException);
            }
        }) { IsBackground = true };

        Console.WriteLine("REPL commands (before start): help, map, mapline <file>:<line>, bp add <id>, bp addline <file>:<line>, bp rm <id>, bp list, run, quit");

        while (true) {
            string? input = ReadLineWithPauseSupport("repl> ", pauseQueue, dbg, supportsKeyAvailable);
            if (input is null) break;

            input = input.Trim();
            if (input.Length == 0) continue;

            var parts = input.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            if (cmd == "help") {
                Console.WriteLine("Commands (before start):");
                Console.WriteLine("  map                     - show full checkpoint mapping (id -> file:line:col)");
                Console.WriteLine("  mapline <file>:<line>   - show nearest checkpoint for a file and line");
                Console.WriteLine("  bp add <id>             - add breakpoint at checkpoint id");
                Console.WriteLine("  bp addline <file>:<line>- add breakpoint nearest to file:line");
                Console.WriteLine("  bp rm <id>              - remove breakpoint at checkpoint id");
                Console.WriteLine("  bp list                 - list active breakpoints added via REPL");
                Console.WriteLine("  run                     - start the script (breakpoints set before run will take effect)");
                Console.WriteLine("  quit                    - exit host");
                continue;
            }

            if (cmd == "map") {
                Console.WriteLine("Checkpoint mapping:");
                foreach (var kv in result.CheckpointMap.OrderBy(k => k.Key))
                    Console.WriteLine($"  {kv.Key} -> {kv.Value.FilePath}:{kv.Value.Line}:{kv.Value.Column}");
                continue;
            }

            if (cmd == "mapline") {
                if (parts.Length < 2) {
                    Console.WriteLine("Usage: mapline <file>:<line>");
                    continue;
                }
                if (!TryParseFileLine(parts[1], out var fpath, out var lineNo)) {
                    Console.WriteLine("Invalid file:line format. Use e.g. script.cs:12");
                    continue;
                }
                var id = FindNearestCheckpoint(result.CheckpointMap, fpath, lineNo);
                if (id.HasValue) {
                    var loc = result.CheckpointMap[id.Value];
                    Console.WriteLine($"Nearest checkpoint: id={id.Value} -> {loc.FilePath}:{loc.Line}:{loc.Column}");
                }
                else Console.WriteLine("No matching checkpoint found for file.");
                continue;
            }

            if (cmd == "bp") {
                if (parts.Length < 2) {
                    Console.WriteLine("Usage: bp add <id> | bp addline <file>:<line> | bp rm <id> | bp list");
                    continue;
                }
                var sub = parts[1].ToLowerInvariant();
                if (sub == "list") {
                    if (activeBreakpoints.Count == 0) Console.WriteLine("No active breakpoints (via REPL).");
                    else Console.WriteLine("Active breakpoints (via REPL): " + string.Join(", ", activeBreakpoints.OrderBy(x => x)));
                    continue;
                }
                if (sub == "add") {
                    if (parts.Length < 3 || !int.TryParse(parts[2], out var id)) {
                        Console.WriteLine("Usage: bp add <id>");
                        continue;
                    }
                    dbg.AddBreakpoint(id);
                    activeBreakpoints.Add(id);
                    Console.WriteLine($"Breakpoint {id} added.");
                    continue;
                }
                if (sub == "rm" || sub == "remove") {
                    if (parts.Length < 3 || !int.TryParse(parts[2], out var id)) {
                        Console.WriteLine("Usage: bp rm <id>");
                        continue;
                    }
                    dbg.RemoveBreakpoint(id);
                    activeBreakpoints.Remove(id);
                    Console.WriteLine($"Breakpoint {id} removed.");
                    continue;
                }
                if (sub == "addline") {
                    if (parts.Length < 3) {
                        Console.WriteLine("Usage: bp addline <file>:<line>");
                        continue;
                    }
                    if (!TryParseFileLine(parts[2], out var fpathAdd, out var lineAdd)) {
                        Console.WriteLine("Invalid file:line format. Use e.g. script.cs:12");
                        continue;
                    }
                    var id = FindNearestCheckpoint(result.CheckpointMap, fpathAdd, lineAdd);
                    if (id.HasValue) {
                        dbg.AddBreakpoint(id.Value);
                        activeBreakpoints.Add(id.Value);
                        var loc = result.CheckpointMap[id.Value];
                        Console.WriteLine($"Added breakpoint at checkpoint {id.Value} -> {loc.FilePath}:{loc.Line}:{loc.Column}");
                    }
                    else Console.WriteLine("No matching checkpoint found for file.");
                    continue;
                }

                Console.WriteLine("Unknown bp subcommand. Use: add, addline, rm, list");
                continue;
            }

            if (cmd == "run" || cmd == "start") {
                Console.WriteLine("Starting script...");
                scriptThread.Start();
                Console.WriteLine("Script started. Use REPL to add/remove breakpoints while script runs.");
                continue;
            }

            if (cmd == "quit" || cmd == "exit") {
                Console.WriteLine("Exiting host.");
                return;
            }

            Console.WriteLine("Unknown command. Type 'help' for a list of commands.");
        }
    }

    static string? ReadLineWithPauseSupport(string prompt, BlockingCollection<PauseInfo> pauseQueue, ScriptDebugger dbg, bool supportsKeyAvailable) {
        var sb = new StringBuilder();
        Console.Write(prompt);

        if (!supportsKeyAvailable) {
            while (pauseQueue.TryTake(out var pinfo)) {
                HandlePause(pinfo, dbg, pauseQueue);
                Console.Write(prompt);
            }
            return Console.ReadLine();
        }

        while (true) {
            if (pauseQueue.TryTake(out var pinfo)) {
                HandlePause(pinfo, dbg, pauseQueue);
                Console.Write(prompt + sb.ToString());
            }

            try {
                if (Console.KeyAvailable) {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) {
                        Console.WriteLine();
                        return sb.ToString();
                    }
                    else if (key.Key == ConsoleKey.Backspace) {
                        if (sb.Length > 0) {
                            sb.Length--;
                            Console.Write("\b \b");
                        }
                    }
                    else {
                        if (!char.IsControl(key.KeyChar)) {
                            sb.Append(key.KeyChar);
                            Console.Write(key.KeyChar);
                        }
                    }
                }
                else {
                    Thread.Sleep(40);
                }
            }
            catch (InvalidOperationException) {
                Console.WriteLine();
                while (pauseQueue.TryTake(out var pinfo2)) {
                    HandlePause(pinfo2, dbg, pauseQueue);
                }
                return Console.ReadLine();
            }
        }
    }

    static void HandlePause(PauseInfo pinfo, ScriptDebugger dbg, BlockingCollection<PauseInfo> pauseQueue) {
        var pauseId = pinfo.PauseId;
        var frame = pinfo.Frame;
        var threadId = pinfo.ThreadId;

        Console.WriteLine();
        Console.WriteLine($"*** PAUSED (pauseId={pauseId}, thread={threadId}) at {frame.MethodName} checkpoint {frame.CheckpointId} ***");
        Console.WriteLine("Locals:");
        for (int i = 0; i < frame.Locals.Count; i++) {
            var p = frame.Locals[i];
            Console.WriteLine($"  {p.Name} = {p.Value ?? "null"}");
        }
        Console.WriteLine();
        Console.WriteLine("Paused commands: c=continue, i=step into, o=step over, u=step out, e=evaluate expression, b=add breakpoint, q=quit");

        while (true) {
            Console.Write("pause> ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;
            var parts = input.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0].ToLowerInvariant();

            if (key == "c") { dbg.Continue(pauseId); break; }
            if (key == "i") { dbg.StepInto(pauseId); break; }
            if (key == "o") { dbg.StepOver(pauseId); break; }
            if (key == "u") { dbg.StepOut(pauseId); break; }
            if (key == "b") {
                if (parts.Length > 1 && int.TryParse(parts[1], out var id)) {
                    dbg.AddBreakpoint(id);
                    Console.WriteLine($"Breakpoint {id} added.");
                }
                else {
                    Console.Write(" Enter checkpoint id to add breakpoint: ");
                    var s = Console.ReadLine();
                    if (int.TryParse(s, out var id2)) {
                        dbg.AddBreakpoint(id2);
                        Console.WriteLine($"Breakpoint {id2} added.");
                    }
                    else Console.WriteLine("Invalid id.");
                }
                continue;
            }
            if (key == "e") {
                Console.Write(" Expression> ");
                var expr = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(expr)) { Console.WriteLine("Empty expression."); continue; }
                var evalResult = EvaluatorService.EvaluateExpression(expr, frame.Locals);
                if (!evalResult.Success)
                    Console.WriteLine("Eval error: " + evalResult.Error);
                else
                    Console.WriteLine("=> " + (evalResult.Result?.ToString() ?? "null"));
                continue;
            }
            if (key == "q") Environment.Exit(0);

            Console.WriteLine("Unknown paused command. Valid: c,i,o,u,e,b,q");
        }

        Console.WriteLine("*** Resumed ***");
    }

    static bool TryParseFileLine(string s, out string filePart, out int line) {
        filePart = "";
        line = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var idx = s.IndexOf(':');
        if (idx >= 0) {
            filePart = s.Substring(0, idx).Trim();
            var linePart = s.Substring(idx + 1).Trim();
            return int.TryParse(linePart, out line) && !string.IsNullOrEmpty(filePart);
        }
        var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out line)) {
            filePart = parts[0];
            return true;
        }
        return false;
    }

    static int? FindNearestCheckpoint(Dictionary<int, (string FilePath, int Line, int Column)> map, string fileOrName, int line) {
        if (map == null || map.Count == 0) return null;
        string needleFileName = Path.GetFileName(fileOrName);
        var exactMatches = map.Where(kv => string.Equals(kv.Value.FilePath, fileOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        IEnumerable<KeyValuePair<int, (string FilePath, int Line, int Column)>> candidates;
        if (exactMatches.Count > 0) candidates = exactMatches;
        else {
            candidates = map.Where(kv => string.Equals(Path.GetFileName(kv.Value.FilePath), needleFileName, StringComparison.OrdinalIgnoreCase));
            if (!candidates.Any()) {
                candidates = map.Where(kv => kv.Value.FilePath.IndexOf(fileOrName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!candidates.Any()) return null;
            }
        }

        var best = candidates
            .Select(kv => new { Id = kv.Key, Loc = kv.Value, Dist = Math.Abs(kv.Value.Line - line) })
            .OrderBy(x => x.Dist)
            .ThenBy(x => x.Id)
            .FirstOrDefault();

        return best?.Id;
    }
}