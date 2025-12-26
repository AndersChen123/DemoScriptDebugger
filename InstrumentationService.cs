using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Compiles and instruments a C# script. Emits an assembly + PDB and returns mapping data.
/// Ensures syntax trees contain encoding (UTF8) and uses all loaded assemblies as references
/// so core types (System.Runtime, etc.) are found.
/// </summary>
public static class InstrumentationService {
	public class InstrumentResult {
		public Dictionary<int, (string FilePath, int Line, int Column)> CheckpointMap { get; init; } = new();
		public Dictionary<string, List<int>> MethodCheckpointMap { get; init; } = new();
	}

	public static InstrumentResult InstrumentAndCompile(string scriptPath, string outDllPath, string outPdbPath) {
		var code = File.ReadAllText(scriptPath);
		// Create SourceText with UTF8 encoding so PDB emission can include source encoding
		var sourceText = SourceText.From(code, Encoding.UTF8);
		var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: scriptPath);

		// Collect metadata references from currently loaded assemblies with a file location
		var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
			.Select(a => a.Location)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var refs = new List<MetadataReference>();
		foreach (var path in loadedAssemblies) {
			try {
				refs.Add(MetadataReference.CreateFromFile(path));
			}
			catch {
				// Ignore any that can't be loaded as metadata reference
			}
		}

		// If System.Runtime is not present in refs (rare), try to load it explicitly
		if (!refs.Any(r => Path.GetFileNameWithoutExtension(((PortableExecutableReference)r).FilePath).Equals("System.Runtime", StringComparison.OrdinalIgnoreCase))) {
			try {
				var sysRuntime = Assembly.Load("System.Runtime");
				if (sysRuntime != null && !string.IsNullOrEmpty(sysRuntime.Location))
					refs.Add(MetadataReference.CreateFromFile(sysRuntime.Location));
			}
			catch {
				// ignore
			}
		}

		// Ensure host assembly (this assembly) is referenced so ScriptDebuggerGlobal etc. resolve
		var hostAsmPath = Assembly.GetExecutingAssembly().Location;
		if (!string.IsNullOrEmpty(hostAsmPath) && !refs.Any(r => string.Equals(((PortableExecutableReference)r).FilePath, hostAsmPath, StringComparison.OrdinalIgnoreCase)))
			refs.Add(MetadataReference.CreateFromFile(hostAsmPath));

		// Create initial compilation
		var compilation = CSharpCompilation.Create(
			assemblyName: Path.GetFileNameWithoutExtension(outDllPath),
			syntaxTrees: new[] { syntaxTree },
			references: refs,
			options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

		var semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
		var rewriter = new InstrumentingRewriter(semanticModel);

		var newRoot = rewriter.Visit(syntaxTree.GetRoot());
		// Create a new syntax tree for the rewritten root and include UTF8 encoding
		var newTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot, path: scriptPath, encoding: Encoding.UTF8);

		// Recreate compilation with the rewritten tree (reuse refs)
		var compilation2 = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTree);

		// Emit to disk (DLL + portable PDB)
		using (var dllFs = File.Open(outDllPath, FileMode.Create))
		using (var pdbFs = File.Open(outPdbPath, FileMode.Create)) {
			var emitOptions = new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb);
			var result = compilation2.Emit(peStream: dllFs, pdbStream: pdbFs, options: emitOptions);
			if (!result.Success) {
				var errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
				throw new Exception("Compilation failed: " + errors);
			}
		}

		return new InstrumentResult {
			CheckpointMap = rewriter.CheckpointMap,
			MethodCheckpointMap = rewriter.MethodCheckpointMap
		};
	}
}