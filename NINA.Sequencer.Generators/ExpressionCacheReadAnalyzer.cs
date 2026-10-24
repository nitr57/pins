using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;
using System.Linq;

namespace NINA.Sequencer.Generators {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ExpressionCacheReadAnalyzer : DiagnosticAnalyzer {
        private static readonly DiagnosticDescriptor CacheRead = new("EXP0100", "Expression cache bypasses evaluation",
            "Read '{0}' to evaluate the expression before consuming its value; '{1}' is cached state",
            "Usage", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(CacheRead);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeRead, OperationKind.PropertyReference, OperationKind.FieldReference);
        }

        private static void AnalyzeRead(OperationAnalysisContext context) {
            var access = context.Operation;
            var owner = ExpressionAnalysis.EnclosingMember(context.ContainingSymbol);
            if (owner is not IMethodSymbol && owner is not IPropertySymbol) return;
            if (owner is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }) return;
            // Validator callbacks deliberately inspect cached values to avoid recursive evaluation.
            // Deserialization callbacks inspect the saved proxy before migrating legacy JSON.
            if (ExpressionAnalysis.ValidatedProperty(owner, context.Compilation) != null
                || owner.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Serialization.OnDeserializedAttribute")) return;
            if (access.Parent is ISimpleAssignmentOperation assignment && assignment.Target == access) return;
            if (access.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out }) return;
            if (ExpressionAnalysis.InNameOf(access)) return;

            foreach (var property in ExpressionAnalysis.Properties(owner.ContainingType)) {
                if (!ExpressionGenerator.GeneratesValidation(property.ContainingType)) continue;
                var proxy = ExpressionAnalysis.Proxy(property);
                var cachedValue = access is IPropertyReferenceOperation { Property.Name: "Value" } value
                    && ExpressionAnalysis.Unwrap(value.Instance) is IPropertyReferenceOperation expression
                    && ExpressionAnalysis.IsThis(expression.Instance)
                    && SymbolEqualityComparer.Default.Equals(expression.Property.ContainingType, property.ContainingType)
                    && expression.Property.Name == property.Name + "Expression";
                var ownProxy = proxy != null && ExpressionAnalysis.MatchesProxy(access, property, proxy);
                if (cachedValue || ownProxy) {
                    context.ReportDiagnostic(Diagnostic.Create(CacheRead, access.Syntax.GetLocation(), property.Name, access.Syntax.ToString()));
                    return;
                }
            }
        }
    }
}
