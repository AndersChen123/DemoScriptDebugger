using System;

public static class Script {
    public static void Run() {
        Console.WriteLine("Script start");
        int a = 10;
        int b = 20;
        Console.WriteLine($"a+b = {a + b}");
        for (int i = 0; i < 3; i++) {
            Console.WriteLine("Loop i=" + i);
        }
        Console.WriteLine("Script end");
    }
}