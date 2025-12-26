using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Expression evaluator with an LRU cache. Compiles per-expression evaluators into collectible
/// AssemblyLoadContexts so we can unload generated assemblies when the cache evicts entries.
/// 
/// Usage: EvaluatorService.EvaluateExpression(expr, locals)
/// locals: IReadOnlyList of (Name, object? Value) in the stable order produced by the instrumenter.
/// </summary>
public static class EvaluatorService {
	// LRU cache capacity (tune as needed)
	const int DefaultCapacity = 64;

	// Lock guards _cacheMap and the _lruList
	static readonly object _lock = new object();

	// Map key -> cache entry
	static readonly Dictionary<string, CacheEntry> _cacheMap = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

	// LRU list: most-recent at front
	static readonly LinkedList<string> _lruList = new LinkedList<string>();

	// Current capacity (immutable after first use for simplicity)
	static readonly int _capacity = DefaultCapacity;

	/// <summary>
	/// Evaluate the expression using the provided locals (stable order).
	/// Returns (Success, Result, ErrorMessage).
	/// </summary>
	public static (bool Success, object? Result, string? Error) EvaluateExpression(string expression, IReadOnlyList<(string Name, object? Value)> locals) {
		try {
			// Build cache key from expression and locals names (order-sensitive)
			var localsSignature = string.Join(",", locals.Select(l => l.Name ?? ""));
			var cacheKey = expression + "|" + localsSignature;

			Delegate del;
			CacheEntry entry;

			// Try get from cache or create new entry
			lock (_lock) {
				if (_cacheMap.TryGetValue(cacheKey, out entry)) {
					// Move to front (most recently used)
					_lruList.Remove(entry.LruNode);
					_lruList.AddFirst(entry.LruNode);
					del = entry.Delegate!;
				}
				else {
					// Compile and create new entry while holding lock to avoid duplicated compiles in this demo
					entry = CreateEntry(cacheKey, expression, locals.Select(l => l.Name).ToArray());
					// Add to cache and LRU
					var node = _lruList.AddFirst(cacheKey);
					entry.LruNode = node;
					_cacheMap[cacheKey] = entry;
					del = entry.Delegate!;
					// Evict if over capacity
					if (_cacheMap.Count > _capacity) {
						EvictLeastUsed();
					}
				}
			}

			// Build argument tuple array for invocation and invoke delegate
			var tupleType = typeof(ValueTuple<string, object>);
			var arr = Array.CreateInstance(tupleType, locals.Count);
			for (int i = 0; i < locals.Count; i++) {
				var item = (ValueTuple<string, object>)(object)(locals[i].Name ?? "", locals[i].Value);
				arr.SetValue(item, i);
			}

			// Invoke delegate. Use DynamicInvoke since delegate type is constructed at runtime.
			var result = del.DynamicInvoke(new object[] { arr })!;
			return (true, result, null);
		}
		catch (TargetInvocationException tie) {
			return (false, null, "Runtime exception: " + tie.InnerException?.ToString());
		}
		catch (Exception ex) {
			return (false, null, "Error: " + ex.ToString());
		}
	}

	static CacheEntry CreateEntry(string cacheKey, string expression, string[] localNames) {
		// Compile the evaluator source to PE/PDB byte arrays
		var code = BuildEvaluatorSource(expression, localNames);
		var syntaxTree = CSharpSyntaxTree.ParseText(code);

		var references = AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
			.Select(a => MetadataReference.CreateFromFile(a.Location))
			.ToList();

		// Try to ensure Microsoft.CSharp is referenced for dynamic usage
		try {
			var mcs = Assembly.Load("Microsoft.CSharp");
			if (mcs != null && !references.Any(r => string.Equals((r as PortableExecutableReference)?.FilePath, mcs.Location, StringComparison.OrdinalIgnoreCase)))
				references.Add(MetadataReference.CreateFromFile(mcs.Location));
		}
		catch { /* ignore */ }

		var compilation = CSharpCompilation.Create(
			assemblyName: "ExprEval_" + Guid.NewGuid().ToString("N"),
			syntaxTrees: new[] { syntaxTree },
			references: references,
			options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release)
		);

		using var peStream = new MemoryStream();
		using var pdbStream = new MemoryStream();
		var emitResult = compilation.Emit(peStream, pdbStream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
		if (!emitResult.Success) {
			var err = string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
			throw new Exception("Evaluation compilation error: " + err);
		}

		peStream.Seek(0, SeekOrigin.Begin);
		pdbStream.Seek(0, SeekOrigin.Begin);

		// Load into collectible ALC
		var alcName = "EvalALC_" + Guid.NewGuid().ToString("N");
		var alc = new AssemblyLoadContext(alcName, isCollectible: true);
		Assembly assembly;
		try {
			assembly = alc.LoadFromStream(peStream, pdbStream);
		}
		catch {
			peStream.Seek(0, SeekOrigin.Begin);
			pdbStream.Seek(0, SeekOrigin.Begin);
			assembly = alc.LoadFromStream(peStream, pdbStream);
		}

		var type = assembly.GetType("ExprEvaluator.Evaluator");
		if (type is null)
			throw new Exception("Evaluator type not found in generated assembly");

		var method = type.GetMethod("Eval", BindingFlags.Public | BindingFlags.Static);
		if (method is null)
			throw new Exception("Eval method not found in generated assembly");

		// Create delegate type Func<ValueTuple<string,object>[], object>
		var elementType = typeof(ValueTuple<string, object>[]);
		var funcType = typeof(Func<,>).MakeGenericType(elementType, typeof(object));
		var del = method.CreateDelegate(funcType);

		var entry = new CacheEntry {
			Key = cacheKey,
			Delegate = del,
			Alc = alc,
			AlcWeakRef = new WeakReference(alc)
		};
		return entry;
	}

	static void EvictLeastUsed() {
		// remove nodes from the tail until count <= capacity
		while (_cacheMap.Count > _capacity) {
			var last = _lruList.Last;
			if (last == null) break;
			var key = last.Value;
			_lruList.RemoveLast();
			if (_cacheMap.TryGetValue(key, out var entry)) {
				_cacheMap.Remove(key);
				ReleaseEntry(entry);
			}
		}
	}

	static void ReleaseEntry(CacheEntry entry) {
		entry.Delegate = null;

		var alc = entry.Alc;
		if (alc != null) {
			try {
				entry.Alc = null;
				alc.Unload();
			}
			catch { }
		}

		var wr = entry.AlcWeakRef;
		if (wr != null) {
			for (int i = 0; i < 10 && wr.IsAlive; i++) {
				GC.Collect();
				GC.WaitForPendingFinalizers();
				Thread.Sleep(50);
			}
		}
	}

	static string BuildEvaluatorSource(string expression, string[] localNames) {
		string SafeIdent(string name, int idx) {
			if (string.IsNullOrWhiteSpace(name))
				return "v" + idx;
			var s = name;
			if (!char.IsLetter(s[0]) && s[0] != '_')
				s = "_" + s;
			var arr = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
			var id = new string(arr);
			return "@" + id;
		}

		var decls = "";
		for (int i = 0; i < localNames.Length; i++) {
			var name = SafeIdent(localNames[i], i);
			decls += $"            var {name} = (dynamic)locals[{i}].Item2;{Environment.NewLine}";
		}

		var code = $@"
using System;
namespace ExprEvaluator {{
    public static class Evaluator {{
        public static object Eval((string, object)[] locals) {{
{decls}
            return (object)({expression});
        }}
    }}
}}
";
		return code;
	}

	class CacheEntry {
		public string? Key;
		public Delegate? Delegate;
		public AssemblyLoadContext? Alc;
		public WeakReference? AlcWeakRef;
		public LinkedListNode<string>? LruNode;
	}
}