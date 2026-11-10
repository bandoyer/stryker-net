using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stryker.Core.UnitTest.Compiling;

// This generator only runs inside these tests, loaded from the test assembly by the compiling process under test,
// so the packaging rules for generators that ship to other projects do not apply to it.
#pragma warning disable RS1036, RS1041, RS2008

/// <summary>
/// A source generator that reads the lambda passed to <c>Configure</c> as syntax and accepts only assignments in it,
/// as generators that take their options from a configuration lambda do. On anything else it reports an error at the
/// statement and emits a fallback, so the assembly still compiles but <c>GeneratedConfiguration.Understood</c> is false.
/// </summary>
[Generator]
public sealed class ConfigurationReadingGenerator : IIncrementalGenerator
{
    public const string ErrorId = "STRYKERTEST001";

    private static readonly DiagnosticDescriptor _unreadableConfiguration = new(
        ErrorId,
        "Unreadable configuration",
        "Expected an assignment in the configuration lambda",
        "StrykerTest",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, Generate);

    private static void Generate(SourceProductionContext context, Compilation compilation)
    {
        var unreadableStatements = compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.Text: "Configure" })
            .SelectMany(invocation => invocation.ArgumentList.Arguments)
            .Select(argument => (argument.Expression as LambdaExpressionSyntax)?.Body)
            .OfType<BlockSyntax>()
            .SelectMany(block => block.Statements)
            .Where(statement => statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax })
            .ToList();

        foreach (var statement in unreadableStatements)
        {
            context.ReportDiagnostic(Diagnostic.Create(_unreadableConfiguration, statement.GetLocation()));
        }

        var understood = unreadableStatements.Count == 0 ? "true" : "false";
        context.AddSource("GeneratedConfiguration.g.cs",
            $"namespace ExampleProject {{ public static class GeneratedConfiguration {{ public static bool Understood => {understood}; }} }}");
    }
}
