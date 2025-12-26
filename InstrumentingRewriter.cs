using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Rewriter that injects PushFrame/PopFrame and Checkpoint calls, records checkpoint map and per-method checkpoint list.
/// Version 4: avoids recursive Visit/block infinite recursion by visiting child statements individually.
/// Compatible with .NET 8 + Roslyn 5.x.
/// </summary>
public sealed class InstrumentingRewriter : CSharpSyntaxRewriter {
	readonly SemanticModel semanticModel;
	readonly SyntaxTree originalTree;
	int nextCheckpointId;
	public Dictionary<int, (string FilePath, int Line, int Column)> CheckpointMap { get; } = new();
	public Dictionary<string, List<int>> MethodCheckpointMap { get; } = new();

	public InstrumentingRewriter(SemanticModel semanticModel, int startCheckpointId = 1) {
		this.semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
		this.originalTree = semanticModel.SyntaxTree;
		this.nextCheckpointId = startCheckpointId;
	}

	int NewCheckpointId() => nextCheckpointId++;

	public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
		if (node.Body is null)
			return base.VisitMethodDeclaration(node);

		// Get method identity
		var methodSymbol = semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
		var methodFullName = methodSymbol != null ? GetMethodFullName(methodSymbol) : node.Identifier.Text;

		if (!MethodCheckpointMap.ContainsKey(methodFullName))
			MethodCheckpointMap[methodFullName] = new List<int>();

		// Visit each child statement inside the method body (this rewrites nested blocks)
		var originalBody = node.Body;
		var visitedStatements = new List<StatementSyntax>(originalBody.Statements.Count);
		foreach (var stmt in originalBody.Statements) {
			var visited = (StatementSyntax?)Visit(stmt) ?? stmt;
			visitedStatements.Add(visited);
		}

		// Build a new BlockSyntax from visited statements
		var visitedBody = SyntaxFactory.Block(SyntaxFactory.List(visitedStatements));

		// Wrap with PushFrame .. try { body } finally { PopFrame(); }
		var pushStmt = CreatePushInvocationStatement(methodFullName);
		var popStmt = CreatePopInvocationStatement();
		var finallyClause = SyntaxFactory.FinallyClause(SyntaxFactory.Block(popStmt));

		var tryStatement = SyntaxFactory.TryStatement(visitedBody, SyntaxFactory.List<CatchClauseSyntax>(), finallyClause);
		var outerBlock = SyntaxFactory.Block(pushStmt, tryStatement);

		return node.WithBody(outerBlock);
	}

	public override SyntaxNode? VisitBlock(BlockSyntax node) {
		// We must avoid calling base.Visit(node) which can re-enter this method in ways
		// that lead to very deep recursion. Instead, visit each statement individually.
		// Use original statements for semantic analysis and mapping, and use the visited
		// statements for emitted nodes.

		// Original statements (for semantic analysis)
		var originalStatements = node.Statements;
		// Visit each statement recursively (this will call Visit on nested blocks/statements)
		var visitedStatements = new List<StatementSyntax>(originalStatements.Count);
		foreach (var stmt in originalStatements) {
			var visited = (StatementSyntax?)Visit(stmt) ?? stmt;
			visitedStatements.Add(visited);
		}

		// Build new statement list with injected checkpoints before each visited statement.
		var newStatements = new List<StatementSyntax>(originalStatements.Count * 2);
		for (int i = 0; i < originalStatements.Count; i++) {
			var origStmt = originalStatements[i];
			var visitedStmt = visitedStatements[i];

			// Create checkpoint id and record mapping using original statement location
			var checkpointId = NewCheckpointId();
			var location = origStmt.GetLocation().GetLineSpan();
			var linePos = location.StartLinePosition;
			CheckpointMap[checkpointId] = (originalTree.FilePath ?? "", linePos.Line + 1, linePos.Character + 1);

			// Determine enclosing method full name (for per-method checkpoint lists)
			var methodName = GetEnclosingMethodFullName(origStmt) ?? "<unknown>";

			if (!MethodCheckpointMap.TryGetValue(methodName, out var list))
				list = (MethodCheckpointMap[methodName] = new List<int>());
			list.Add(checkpointId);

			// Build locals provider lambda using original statement (so semanticModel.AnalyzeDataFlow works)
			var localsLambda = CreateLocalsProviderLambda(origStmt);

			// Create checkpoint invocation (before the visited statement)
			var checkpointStmt = CreateCheckpointInvocationStatement(checkpointId, methodName, localsLambda);

			newStatements.Add(checkpointStmt);
			newStatements.Add(visitedStmt);
		}

		// Return a new block with the new statement list
		var newList = SyntaxFactory.List(newStatements);
		return node.WithStatements(newList);
	}

	// Helper to create a unique full method name (namespace.type.method)
	string GetMethodFullName(IMethodSymbol method) {
		var parts = new List<string>();
		var containing = method.ContainingType;
		if (containing != null) {
			var ns = containing.ContainingNamespace;
			if (ns != null && !ns.IsGlobalNamespace)
				parts.Add(ns.ToDisplayString());
			parts.Add(containing.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
		}
		parts.Add(method.Name);
		return string.Join(".", parts);
	}

	string? GetEnclosingMethodFullName(SyntaxNode node) {
		var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
		if (method is null)
			return null;
		var sym = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
		return sym != null ? GetMethodFullName(sym) : method.Identifier.Text;
	}

	/// <summary>
	/// Creates a locals-provider lambda expression that returns ScriptDebuggerGlobal.MakeLocals(("name",(object)name), ...).
	/// Uses semanticModel.AnalyzeDataFlow on the original anchor node.
	/// </summary>
	private ExpressionSyntax? CreateLocalsProviderLambda(StatementSyntax anchor) {
		var methodDecl = anchor.FirstAncestorOrSelf<MethodDeclarationSyntax>();
		if (methodDecl is null)
			return null;

		var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
		var paramSymbols = methodSymbol?.Parameters ?? Enumerable.Empty<IParameterSymbol>();

		DataFlowAnalysis? data = null;
		try {
			data = semanticModel.AnalyzeDataFlow(anchor);
		}
		catch {
			data = null;
		}

		var locals = new List<ISymbol>();

		// Parameters first
		foreach (var p in paramSymbols)
			locals.Add(p);

		if (data != null) {
			var localSymbols = data.VariablesDeclared
				.Concat(data.DataFlowsIn)
				.Concat(data.Captured)
				.Where(s => s != null && (s.Kind == SymbolKind.Local || s.Kind == SymbolKind.Parameter))
				.GroupBy(s => s.Name)
				.Select(g => g.First())
				.Where(s => !(s is IParameterSymbol))
				.ToList();

			localSymbols.Sort((a, b) => {
				var aDecl = a.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? 0;
				var bDecl = b.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? 0;
				return aDecl.CompareTo(bDecl);
			});

			locals.AddRange(localSymbols);
		}

		// Deduplicate preserving order
		var distinct = new List<ISymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var s in locals) {
			if (s == null) continue;
			if (seen.Add(s.Name))
				distinct.Add(s);
		}

		if (distinct.Count == 0)
			return null;

		// Build tuple expressions ("name", (object)name)
		var tupleExprs = new List<ExpressionSyntax>();
		foreach (var sym in distinct) {
			var ident = SyntaxFactory.IdentifierName(sym.Name);
			var castToObject = SyntaxFactory.CastExpression(
				SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
				ident);

			var nameLiteral = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(sym.Name));
			var tuple = SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(new[]
			{
				SyntaxFactory.Argument(nameLiteral),
				SyntaxFactory.Argument(castToObject)
			}));

			tupleExprs.Add(tuple);
		}

		var separated = SyntaxFactory.SeparatedList<ArgumentSyntax>(tupleExprs.Select(te => SyntaxFactory.Argument(te)));
		var makeLocalsInv = SyntaxFactory.InvocationExpression(
			SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
				SyntaxFactory.IdentifierName("ScriptDebuggerGlobal"),
				SyntaxFactory.IdentifierName("MakeLocals")))
			.WithArgumentList(SyntaxFactory.ArgumentList(separated));

		var lambda = SyntaxFactory.ParenthesizedLambdaExpression(makeLocalsInv)
							  .WithParameterList(SyntaxFactory.ParameterList());

		return lambda;
	}

	static ExpressionStatementSyntax CreatePushInvocationStatement(string methodName) {
		var invocation = SyntaxFactory.InvocationExpression(
			SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
				SyntaxFactory.IdentifierName("ScriptDebuggerGlobal"),
				SyntaxFactory.IdentifierName("PushFrame")))
			.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
				SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(methodName))),
				SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
			})));
		return SyntaxFactory.ExpressionStatement(invocation);
	}

	static StatementSyntax CreatePopInvocationStatement() {
		var invocation = SyntaxFactory.InvocationExpression(
			SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
				SyntaxFactory.IdentifierName("ScriptDebuggerGlobal"),
				SyntaxFactory.IdentifierName("PopFrame")))
			.WithArgumentList(SyntaxFactory.ArgumentList());
		return SyntaxFactory.ExpressionStatement(invocation);
	}

	static ExpressionStatementSyntax CreateCheckpointInvocationStatement(int checkpointId, string methodName, ExpressionSyntax? localsLambda) {
		//var args = new List<ArgumentSyntax> {
		//	SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(checkpointId))),
		//	SyntaxFactory.Argument(SyntaxKind.PredefinedType, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression)), // placeholder to avoid formatter issues
		//};
		// Build proper args manually (avoid earlier typing issues)
		var argList = new List<ArgumentSyntax> {
			SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(checkpointId))),
			SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(methodName)))
		};
		if (localsLambda != null)
			argList.Add(SyntaxFactory.Argument(localsLambda));
		var invocation = SyntaxFactory.InvocationExpression(
			SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
				SyntaxFactory.IdentifierName("ScriptDebuggerGlobal"),
				SyntaxFactory.IdentifierName("Checkpoint")))
			.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argList)));
		return SyntaxFactory.ExpressionStatement(invocation);
	}
}