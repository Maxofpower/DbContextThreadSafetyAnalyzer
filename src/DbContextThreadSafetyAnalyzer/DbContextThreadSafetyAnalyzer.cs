
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DbContextThreadSafetyAnalyzer
{
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public class DbContextThreadSafetyAnalyzer : DiagnosticAnalyzer
	{
		public const string DiagnosticId = "ER9000";
		private static readonly LocalizableString Title = "Thread Safety Violation";
		private static readonly LocalizableString MessageFormat = "DbContext should not be shared across threads";
		private static readonly LocalizableString Description = "A DbContext instance should not be used across threads as it is not thread-safe.";
		private const string Category = "Design";

		private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
			DiagnosticId,
			Title,
			MessageFormat,
			Category,
			DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description: Description
		);

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

		public override void Initialize(AnalysisContext context)
		{
			context.EnableConcurrentExecution();
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
		}

		private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
		{
			var invocationExpression = (InvocationExpressionSyntax)context.Node;
			var methodSymbol = context.SemanticModel.GetSymbolInfo(invocationExpression).Symbol as IMethodSymbol;

			if (methodSymbol == null)
				return;


			// Detect Parallel.ForEach or Parallel.ForEachAsync
			if (IsParallelForEachMethod(methodSymbol))
			{
				// Look for the lambda inside Parallel.ForEach

				foreach (var argument in invocationExpression.ArgumentList.Arguments)
				{
					AnalyzeArgument(argument.Expression, context);
				}
			}
			// Focus on Task-related methods: Task.Run, Task.WhenAll, Task.StartNew, Task.Factory.StartNew
			if (
				(methodSymbol.ContainingType.Name.Contains("TaskFactory") && methodSymbol.Name == "StartNew")
				||
				methodSymbol.ContainingType.Name == "Task" &&
				 (methodSymbol.Name == "Run" || methodSymbol.Name == "WhenAll" || methodSymbol.Name == "WhenAny" || methodSymbol.Name == "StartNew"))
			{
				// Check the arguments to see if a DbContext is used
				foreach (var argument in invocationExpression.ArgumentList.Arguments)
				{
					AnalyzeArgument(argument.Expression, context);
				}
			}
			if (methodSymbol.Name == "AsParallel")
			{
				// Check if the invocation is on a queryable source derived from DbContext
				var memberAccess = invocationExpression.Expression as MemberAccessExpressionSyntax;
				if (memberAccess != null)
				{
					var sourceExpression = memberAccess.Expression;

					// Analyze the source expression
					var symbolInfo = context.SemanticModel.GetSymbolInfo(sourceExpression).Symbol;
					if (symbolInfo != null)
					{
						// Check if the source symbol's type originates from a DbContext query
						if (symbolInfo is IPropertySymbol propertySymbol &&
							propertySymbol.Type.ToString().Contains("DbSet"))
						{
							// Likely from a DbContext
							var diagnostic = Diagnostic.Create(Rule, invocationExpression.GetLocation(), "DbContext query with AsParallel");
							context.ReportDiagnostic(diagnostic);
						}
						else if (symbolInfo is ILocalSymbol localSymbol &&
								 localSymbol.Type.ToString().Contains("IQueryable"))
						{
							// Further resolve if IQueryable source is DbContext-backed
							var originatingSymbol = localSymbol.ContainingSymbol;
							if (originatingSymbol?.ContainingType.ToString().Contains("DbContext") == true)
							{
								var diagnostic = Diagnostic.Create(Rule, invocationExpression.GetLocation(), "DbContext query with AsParallel");
								context.ReportDiagnostic(diagnostic);
							}
						}
					}
				}
			}
		}
		private bool IsParallelForEachMethod(IMethodSymbol methodSymbol)
		{
			return
				//methodSymbol.ContainingType.Name.Contains("Parallel");
				//&&

				(methodSymbol.Name == "ForEach" || methodSymbol.Name == "ForEachAsync" || methodSymbol.Name == "For");
		}
		private void AnalyzeArgument(ExpressionSyntax expression, SyntaxNodeAnalysisContext context)
		{
			if (expression is SimpleLambdaExpressionSyntax lambda)
			{
				AnalyzeLambda(lambda, context);
			}
			else if (expression is ParenthesizedLambdaExpressionSyntax parenthesizedLambda)
			{
				AnalyzeLambda(parenthesizedLambda, context);
			}
			else if (expression is AnonymousMethodExpressionSyntax anonymousMethod)
			{
				AnalyzeAnonymousMethod(anonymousMethod, context);
			}
			else if (expression is AwaitExpressionSyntax awaitExpression)
			{
				// Check for async lambdas or methods within Task.Run
				AnalyzeAwaitExpression(awaitExpression, context);
			}
		}

		private void AnalyzeLambda(LambdaExpressionSyntax lambda, SyntaxNodeAnalysisContext context)
		{
			var identifierNames = lambda.Body.DescendantNodes().OfType<IdentifierNameSyntax>();
			foreach (var identifier in identifierNames)
			{
				var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
				if (symbol?.Type?.ToString().Contains("DbContext") == true)
				{
					TrackDbContextUsage(identifier, context);
				}
			}
		}

		private void AnalyzeAnonymousMethod(AnonymousMethodExpressionSyntax anonymousMethod, SyntaxNodeAnalysisContext context)
		{
			var identifierNames = anonymousMethod.Body.DescendantNodes().OfType<IdentifierNameSyntax>();
			foreach (var identifier in identifierNames)
			{
				var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
				if (symbol?.Type?.ToString().Contains("DbContext") == true)
				{
					TrackDbContextUsage(identifier, context);
				}
			}
		}

		private void AnalyzeAwaitExpression(AwaitExpressionSyntax awaitExpression, SyntaxNodeAnalysisContext context)
		{
			var identifierNames = awaitExpression.DescendantNodes().OfType<IdentifierNameSyntax>();
			foreach (var identifier in identifierNames)
			{
				var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
				if (symbol?.Type?.ToString().Contains("DbContext") == true)
				{
					TrackDbContextUsage(identifier, context);
				}
			}
		}

		private void TrackDbContextUsage(IdentifierNameSyntax identifier, SyntaxNodeAnalysisContext context)
		{
			// Get the symbol for the identifier (variable or parameter)
			var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
			if (symbol == null)
				return;

			// Ensure the symbol type is DbContext or derived from DbContext
			var type = symbol.Type;
			if (!IsDbContextOrDerived(type))
				return;

			var usingStatement = identifier.Ancestors().OfType<UsingStatementSyntax>().FirstOrDefault();
			if (usingStatement != null)
			{
				// Analyze the using block scope
				var declaredDbContext = usingStatement.Declaration?.Variables.FirstOrDefault()?.Identifier.Text;
				if (declaredDbContext != null && declaredDbContext != identifier.Identifier.Text)
				{
					// DbContext used does not match the one declared in the using block
					ReportDiagnostic(context, identifier, declaredDbContext);
				}

				// Check if the using block is inside Task.Run() or similar invocation
				var enclosingTaskRun = usingStatement.Ancestors()
					.OfType<InvocationExpressionSyntax>()
					.FirstOrDefault(invocation =>
					{
						var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
						if (memberAccess == null) return false;

						// Check for Task.Run()
						return (memberAccess.Name.Identifier.Text == "Run" || memberAccess.Name.Identifier.Text == "StartNew") &&
							   invocation.Expression.ToString().Contains("Task");
					});

				if (enclosingTaskRun == null)
				{
					// The using block is not inside Task.Run(), raise a warning
					ReportDiagnostic(context, identifier, "DbContext usage must be inside Task.Run() to ensure proper scope and disposal.");
				}

				return;
			}

			// Check for 'using var' and ensure the same DbContext is used
			var localDeclarations = context.Node.DescendantNodes()
				.OfType<LocalDeclarationStatementSyntax>();

			foreach (var declaration in localDeclarations)
			{
				// Get the type of the variable being declared
				var variable = declaration.Declaration.Variables.FirstOrDefault();
				if (variable == null) continue;

				var variableType = context.SemanticModel.GetTypeInfo(variable.Initializer.Value).Type;

				// Check if it's a "using var" declaration
				if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
				{
					// Check if the type is DbContext or derived from DbContext
					if (variableType != null && IsDbContextOrDerived(variableType))
					{
						// Ensure the same DbContext is used in Task.Run or other scopes
						var taskRunAncestor = identifier.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(
							invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
										  memberAccess.Name.Identifier.Text == "Run" &&
										  memberAccess.Expression is IdentifierNameSyntax taskIdentifier &&
										  taskIdentifier.Identifier.Text == "Task"
						);

						// If this variable is used in Task.Run and the variable is out of scope, raise a warning
						if (taskRunAncestor != null && identifier.Identifier.Text != variable.Identifier.Text)
						{
							ReportDiagnostic(context, identifier, $"DbContext {variable.Identifier.Text} used outside its scope.");
						}

						return;
					}
				}
				else
				{
					// Unsafe: Not using "using var" for DbContext
					if (variableType != null && IsDbContextOrDerived(variableType))
					{
						ReportDiagnostic(context, identifier, symbol.Name);
					}
				}
			}

			//Parallel
			var parallelForEachAncestor = identifier.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(
				invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
							  (memberAccess.Name.Identifier.Text == "For" || 
							  memberAccess.Name.Identifier.Text == "ForEachAsync" ||
							  memberAccess.Name.Identifier.Text == "ForEach") &&
							  memberAccess.Expression is IdentifierNameSyntax parallelIdentifier &&
							  parallelIdentifier.Identifier.Text == "Parallel"
			);

			if (parallelForEachAncestor != null)
			{
				// DbContext usage inside Parallel.ForEach detected without proper scoping
				ReportDiagnostic(context, identifier, symbol.Name);
			}

			// Detect misuse in Task.Run or similar parallel execution
			 var taskAncestor = identifier.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(
				invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
							  memberAccess.Name.Identifier.Text == "Run" &&
							  memberAccess.Expression is IdentifierNameSyntax taskIdentifier &&
							  taskIdentifier.Identifier.Text == "Task"
			);

			if (taskAncestor != null)
			{
				// DbContext usage inside Task.Run detected without proper scoping
				ReportDiagnostic(context, identifier, symbol.Name);
			}

			// Check for Task.Factory.StartNew
			taskAncestor= identifier.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(
				invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
							  memberAccess.Name.Identifier.Text == "StartNew" &&
							  memberAccess.Expression is MemberAccessExpressionSyntax factoryAccess &&
				factoryAccess.Name.Identifier.Text == "Factory" 
							  );
			if (taskAncestor != null)
			{
				// DbContext usage inside Task.Run detected without proper scoping
				ReportDiagnostic(context, identifier, symbol.Name);
			}

		}
		private bool IsDbContextOrDerived(ITypeSymbol type)
		{
			while (type != null)
			{
				if (type.Name == "DbContext" && type.ContainingNamespace.ToString() == "Microsoft.EntityFrameworkCore")
					return true;
				type = type.BaseType;
			}
			return false;
		}
		private void ReportDiagnostic(SyntaxNodeAnalysisContext context, SyntaxNode node, string dbContextName)
		{
			var diagnostic = Diagnostic.Create(Rule, node.GetLocation(), dbContextName);
			context.ReportDiagnostic(diagnostic);
		}
	}
}