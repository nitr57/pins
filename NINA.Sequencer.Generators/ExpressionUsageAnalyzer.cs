using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace NINA.Sequencer.Generators {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ExpressionUsageAnalyzer : DiagnosticAnalyzer {
        private static readonly DiagnosticDescriptor DiscardedIssues = new("EXP0101", "Validation hook changes discarded issues",
            "Generated validation replaces Issues after this hook; {0}", "Usage", DiagnosticSeverity.Warning, isEnabledByDefault: true);
        private static readonly DiagnosticDescriptor MissingValidation = new("EXP0102", "Expression entity does not participate in validation",
            "'{0}' does not implement IValidatable. Set GenerateValidation = true or implement IValidatable and compose ValidateOwnExpressions in Validate().",
            "Usage", DiagnosticSeverity.Warning, isEnabledByDefault: true);
        private static readonly DiagnosticDescriptor MissingHelper = new("EXP0103", "Consider composing generated expression validation",
            "Validation for '{0}' does not directly call its ValidateOwnExpressions helper. Consider calling it to include new expression properties automatically.",
            "Usage", DiagnosticSeverity.Info, isEnabledByDefault: true);
        private static readonly DiagnosticDescriptor RecursiveProxy = new("EXP0104", "Expression validator reenters its scalar property",
            "Validator for '{0}' accesses its evaluating scalar property. Use the supplied Expression or the proxy cache to avoid recursive evaluation.",
            "Usage", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(DiscardedIssues, MissingValidation, MissingHelper, RecursiveProxy);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolStartAction(AnalyzeValidation, SymbolKind.NamedType);
            context.RegisterOperationAction(AnalyzeProperty, OperationKind.PropertyReference);
        }

        private static void AnalyzeValidation(SymbolStartAnalysisContext context) {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.IsAbstract || type.TypeKind != TypeKind.Class || ExpressionGenerator.GeneratesValidation(type)
                || !type.AllInterfaces.Any(i => i.ToDisplayString() == "NINA.Sequencer.ISequenceEntity")) return;
            // A failed generator declaration already has an actionable error. Avoid cascading hints.
            var helper = type.GetMembers("ValidateOwnExpressions").OfType<IMethodSymbol>().FirstOrDefault();
            if (helper == null || !type.GetMembers().OfType<IPropertySymbol>().Any(p => ExpressionAnalysis.Attribute(p) != null)) return;
            var validation = type.AllInterfaces.FirstOrDefault(i => i.ToDisplayString() == "NINA.Sequencer.Validations.IValidatable");
            if (validation == null) {
                context.RegisterSymbolEndAction(end => end.ReportDiagnostic(Diagnostic.Create(MissingValidation, type.Locations.FirstOrDefault(), type.Name)));
                return;
            }
            var contract = validation.GetMembers("Validate").OfType<IMethodSymbol>().Single();
            var implementation = type.FindImplementationForInterfaceMember(contract);
            var callsHelper = 0;
            context.RegisterOperationAction(operation => {
                var call = (IInvocationOperation)operation.Operation;
                if (SymbolEqualityComparer.Default.Equals(operation.ContainingSymbol, implementation)
                    && SymbolEqualityComparer.Default.Equals(call.TargetMethod, helper)
                    && ExpressionAnalysis.IsDirectBody(call) && ExpressionAnalysis.IsThis(call.Instance)) {
                    Interlocked.Exchange(ref callsHelper, 1);
                }
            }, OperationKind.Invocation);
            context.RegisterSymbolEndAction(end => {
                if (Volatile.Read(ref callsHelper) == 0) {
                    end.ReportDiagnostic(Diagnostic.Create(MissingHelper, type.Locations.FirstOrDefault(), type.Name));
                }
            });
        }

        private static void AnalyzeProperty(OperationAnalysisContext context) {
            var property = (IPropertyReferenceOperation)context.Operation;
            if (!ExpressionAnalysis.IsThis(property.Instance) || ExpressionAnalysis.InNameOf(property)
                || !ExpressionAnalysis.IsDirectBody(property)) return;
            var owner = context.ContainingSymbol;
            if (!ExpressionGenerator.GeneratesValidation(owner.ContainingType)) return;
            if (ExpressionAnalysis.ValidatedProperty(owner, context.Compilation) is { } validated
                && ExpressionAnalysis.Proxy(validated) != null && ExpressionAnalysis.SameProperty(property.Property, validated)) {
                context.ReportDiagnostic(Diagnostic.Create(RecursiveProxy, property.Syntax.GetLocation(), validated.Name));
            }
            if (owner is not IMethodSymbol { PartialDefinitionPart: not null } hook
                || hook.Name is not ("ValidateAdditional" or "PrepareExpressionValidation")
                || hook.IsStatic || !hook.ReturnsVoid || hook.Arity != 0
                || (hook.Name == "ValidateAdditional" ? hook.Parameters.Length != 1
                    || hook.Parameters[0].Type.ToDisplayString() != "System.Collections.Generic.IList<string>" : hook.Parameters.Length != 0)
                || !SymbolEqualityComparer.Default.Equals(property.Property, ExpressionGenerator.FindMember(owner.ContainingType, "Issues"))) return;

            IOperation target = property;
            while (target.Parent is IConversionOperation or IParenthesizedOperation) target = target.Parent;
            // Null-conditional calls still mutate this Issues list when it is present.
            var conditionalCall = (target.Parent as IConditionalAccessOperation)?.WhenNotNull as IInvocationOperation;
            if (target.Parent is IPropertyReferenceOperation { Property.IsIndexer: true } indexer && indexer.Instance == target) target = indexer;
            var mutation = target.Parent is IAssignmentOperation assignment && assignment.Target == target
                || target.Parent is IInvocationOperation invocation && invocation.Instance == target && IsListMutation(invocation)
                || conditionalCall?.Instance is IConditionalAccessInstanceOperation && IsListMutation(conditionalCall);
            if (!mutation) return;
            var correction = hook.Name == "ValidateAdditional" ? $"add errors to the '{hook.Parameters[0].Name}' parameter instead"
                : "move error reporting to ValidateAdditional and add errors to its issues parameter";
            context.ReportDiagnostic(Diagnostic.Create(DiscardedIssues, target.Syntax.GetLocation(), correction));
        }

        private static bool IsListMutation(IInvocationOperation call) => call.TargetMethod.Name is
            "Add" or "Clear" or "Insert" or "Remove" or "RemoveAt" or "AddRange" or "InsertRange" or "RemoveRange" or "RemoveAll" or "Reverse" or "Sort";
    }
}