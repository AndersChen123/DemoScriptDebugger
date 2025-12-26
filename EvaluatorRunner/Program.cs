using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace EvaluatorRunner
{
    public static class Program
    {
        // Reads code between ---BEGIN-CODE--- and ---END-CODE--- from stdin and executes it using Roslyn scripting.
        public static async Task<int> RunAsync()
        {
            try
            {
                var sb = new StringBuilder();
                string? line;
                bool inCode = false;
                while ((line = Console.ReadLine()) != null)
                {
                    if (line.Trim() == "---BEGIN-CODE---")
                    {
                        inCode = true;
                        continue;
                    }
                    if (line.Trim() == "---END-CODE---")
                    {
                        break;
                    }
                    if (inCode)
                    {
                        sb.AppendLine(line);
                    }
                }

                string code = sb.ToString();

                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.Error.WriteLine("No code received by evaluator.");
                    return 1;
                }

                var options = ScriptOptions.Default
                    .WithImports("System", "System.Collections.Generic", "System.Linq")
                    .WithReferences(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)).Select(a => a.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p)));

                await CSharpScript.RunAsync(code, options);
                return 0;
            }
            catch (CompilationErrorException ce)
            {
                Console.Error.WriteLine("Compilation error:");
                Console.Error.WriteLine(string.Join(Environment.NewLine, ce.Diagnostics));
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Runtime error: " + ex);
                return 3;
            }
        }
    }
}