using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Globalization;
using System.Linq;

namespace NINA.Sequencer.Generators {
    public partial class ExpressionGenerator {
        private static readonly DiagnosticDescriptor InvalidDeclaration = Error("EXP0009", "Unsupported expression declaration",
            "'{0}' must be a public instance partial property with public get; set; accessors in a top-level partial class that is not generic, abstract or file-local");
        private static readonly DiagnosticDescriptor MissingValidator = Error("EXP0010", "Expression validator implementation is missing",
            "'{0}' requests a validator. Implement partial void {0}ExpressionValidator(NINA.Sequencer.Logic.Expression expression) without an access modifier, or remove HasValidator.");
        private static readonly DiagnosticDescriptor InvalidRange = Error("EXP0011", "Invalid expression range",
            "Range on '{0}' must be null or contain [minimum, maximum] with optional integer boundary flags 0..3, non-NaN bounds and a nonempty ordered interval; maximum 0 means NO_MAXIMUM");

        private sealed class ExpressionDeclaration {
            internal ExpressionDeclaration(ISymbol symbol, AttributeData attribute) {
                Symbol = symbol;
                Attribute = attribute;
            }

            internal ISymbol Symbol { get; }
            internal AttributeData Attribute { get; }
        }

        private static bool ValidateExpressionDeclaration(SourceProductionContext context, Compilation compilation, ExpressionDeclaration declaration) {
            var symbol = declaration.Symbol;
            var type = symbol.ContainingType;
            var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
            var validShape = symbol is IPropertySymbol { IsStatic: false, IsIndexer: false, ReturnsByRef: false, ReturnsByRefReadonly: false } property
                && property.DeclaredAccessibility == Accessibility.Public
                && property.GetMethod?.DeclaredAccessibility == Accessibility.Public
                && property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false }
                && syntax != null && syntax.ExpressionBody == null && syntax.Initializer == null
                && syntax.Modifiers.Any(SyntaxKind.PartialKeyword)
                && syntax.Modifiers.All(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PartialKeyword))
                && syntax.AccessorList?.Accessors.Count == 2
                && syntax.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null && a.SemicolonToken.IsKind(SyntaxKind.SemicolonToken))
                && type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && !type.IsFileLocal && type.Arity == 0 && type.ContainingType == null
                && type.DeclaringSyntaxReferences.All(r => r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!validShape) {
                context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(declaration.Attribute), symbol.Name));
                return false;
            }
            // The compiler reports unresolved or incorrectly typed constants. Do not turn
            // those errors into a generator exception that discards other owners' output.
            if (declaration.Attribute.NamedArguments.Any(a => InvalidConstant(a.Value))) return false;

            var valid = true;
            if (ExpressionAnalysis.HasValidator(declaration.Attribute, GeneratesValidation(type))
                && !type.GetMembers(symbol.Name + "ExpressionValidator").OfType<IMethodSymbol>().Any(m =>
                    ExpressionAnalysis.IsValidatorSignature(m, compilation)
                    && m.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is MethodDeclarationSyntax method
                        && method.Modifiers.Any(SyntaxKind.PartialKeyword) && !method.Modifiers.Any(SyntaxKind.PrivateKeyword)
                        && (method.Body != null || method.ExpressionBody != null)))) {
                context.ReportDiagnostic(Diagnostic.Create(MissingValidator, AttributeLocation(declaration.Attribute, "HasValidator"), symbol.Name));
                valid = false;
            }
            foreach (var argument in declaration.Attribute.NamedArguments.Where(a => a.Key == "Range")) {
                if (!ValidRange(argument.Value)) {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidRange, AttributeLocation(declaration.Attribute, "Range"), symbol.Name));
                    valid = false;
                }
            }
            return valid;
        }

        private static Location? AttributeLocation(AttributeData attribute, string? argument = null) {
            var syntax = attribute.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
            return syntax?.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == argument)?.GetLocation()
                ?? syntax?.GetLocation();
        }

        private static bool InvalidConstant(TypedConstant value) => value.Kind == TypedConstantKind.Error
            || (value.Kind == TypedConstantKind.Array && !value.IsNull && value.Values.Any(InvalidConstant));

        private static bool ValidRange(TypedConstant range) {
            if (range.IsNull) return true;
            if (range.Kind != TypedConstantKind.Array || range.Values.Length is < 2 or > 3) return false;
            if (range.Values.Any(v => v.Value is not double)) return false;
            var min = (double)range.Values[0].Value!;
            var max = (double)range.Values[1].Value!;
            var flags = range.Values.Length == 3 ? (double)range.Values[2].Value! : 0;
            if (double.IsNaN(min) || double.IsNaN(max) || double.IsNaN(flags) || flags < 0 || flags > 3 || flags != Math.Truncate(flags)) return false;
            // Keep the runtime's zero maximum sentinel, including its ignored upper-bound flag.
            var effectiveMax = max == 0 ? double.MaxValue : max;
            var exclusive = ((int)flags & 1) != 0 || (max != 0 && ((int)flags & 2) != 0);
            return min < effectiveMax || (min == effectiveMax && !exclusive);
        }

        private static string DoubleLiteral(double value) => double.IsNaN(value) ? "global::System.Double.NaN"
            : double.IsPositiveInfinity(value) ? "global::System.Double.PositiveInfinity"
            : double.IsNegativeInfinity(value) ? "global::System.Double.NegativeInfinity"
            : value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }
}