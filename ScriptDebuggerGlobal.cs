using System;

public static class ScriptDebuggerGlobal {
    // Host must be set by the demo before running the instrumented assembly
    public static Demo.ScriptDebugger? Host { get; set; }

    public static void PushFrame(string methodName, Func<(string Name, object? Value)[]>? localsProvider) =>
        Host?.PushFrame(methodName, localsProvider);

    public static void PopFrame() =>
        Host?.PopFrame();

    public static void Checkpoint(int checkpointId, string methodName, Func<(string Name, object? Value)[]>? localsProvider = null) =>
        Host?.Checkpoint(checkpointId, methodName, localsProvider);

    // Helper used by generated code. Produces an array of tuples for locals
    public static (string Name, object? Value)[] MakeLocals(params (string Name, object? Value)[] pairs) => pairs;
}