# DemoScriptDebugger

This repository contains a demo script debugger built with Roslyn instrumentation, an in-process debugger host, and an expression evaluator with caching and collectible AssemblyLoadContexts.

What’s included
- Instrumenting rewriter (InstrumentingRewriter.cs) that injects PushFrame/Checkpoint/PopFrame calls.
- InstrumentationService.cs — compiles the instrumented script and emits DLL + PDB.
- ScriptDebugger (ScriptDebugger.cs) — supports per-thread state, breakpoints, StepInto / StepOver / StepOut.
- ScriptDebuggerGlobal.cs — helper used by instrumented code to forward calls to the host.
- EvaluatorService.cs — expression evaluator that compiles expressions, caches them with an LRU policy,
  and loads compiled evaluators into collectible AssemblyLoadContexts (unloaded on eviction).
- EvaluatorRunner/Program.cs — subprocess evaluator that reads code over stdin and runs it with Roslyn scripting.
- Program.cs — console REPL host that instruments a script, allows breakpoint management, and interacts with paused script threads.
- script.cs — sample script used by the demo.
- CI workflow to build the demo on .NET 8.

Build & run
1. Ensure .NET 8 SDK is installed.
2. Restore & build:
   dotnet restore
   dotnet build
3. Run:
   dotnet run

Usage notes
- Use the REPL to add breakpoints by checkpoint id or by source line:
  - bp add <id>
  - bp addline <file>:<line>
  - map shows mapping id -> source location
  - run starts the instrumented script (set breakpoints before running for best results)
- When the script pauses you can:
  - c (continue), i (step into), o (step over), u (step out), e (evaluate expressions), b (add breakpoint)

Security and sandboxing
- EvaluatorService compiles expressions and runs them in collectible AssemblyLoadContexts for easier unloading.
- For a stronger sandbox, run evaluations in the separate evaluator subprocess (EvaluatorRunner) and enforce OS-level controls (timeouts, user privileges, containers). The demo uses stdin/stdout IPC for the evaluator.

Limitations
- The instrumenter is conservative but not exhaustive — some language constructs (async state machines, ref/out complex captures) may require additional handling.
- The evaluator runs user code in-process for compiled evaluators; untrusted code should be executed in a separate process or container.

License
MIT — see LICENSE file.
