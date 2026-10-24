using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Sequencer.Generators {
    internal static class ExpressionAnalysis {
        internal const string ExpressionAttributeName = "NINA.Sequencer.Generators.IsExpressionAttribute";

        internal static AttributeData? Attribute(IPropertySymbol property) => (property.PartialDefinitionPart ?? property).GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ExpressionAttributeName);

        internal static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol? type) {
            for (; type != null; type = type.BaseType) {
                foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
                    if (Attribute(property) != null) yield return property;
                }
            }
        }

        internal static bool HasValidator(AttributeData attribute, bool generatedValidation) =>
            attribute.NamedArguments.Any(a => a.Key == "HasValidator" && (!generatedValidation || a.Value.Value is true));

        internal static string? Proxy(IPropertySymbol property) => Attribute(property)?.NamedArguments
            .FirstOrDefault(a => a.Key == "Proxy").Value.Value as string;

        internal static bool IsValidatorSignature(IMethodSymbol method, Compilation compilation) =>
            !method.IsStatic && method.ReturnsVoid && method.Arity == 0 && method.DeclaredAccessibility == Accessibility.Private
            && method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None
            && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, compilation.GetTypeByMetadataName("NINA.Sequencer.Logic.Expression"));

        internal static IPropertySymbol? ValidatedProperty(ISymbol owner, Compilation compilation) {
            if (owner is not IMethodSymbol method || !IsValidatorSignature(method, compilation)) return null;
            return Properties(owner.ContainingType).FirstOrDefault(p => method.Name == p.Name + "ExpressionValidator"
                && SymbolEqualityComparer.Default.Equals(p.ContainingType, method.ContainingType)
                && HasValidator(Attribute(p)!, ExpressionGenerator.GeneratesValidation(p.ContainingType)));
        }

        internal static bool SameProperty(IPropertySymbol a, IPropertySymbol b) =>
            SymbolEqualityComparer.Default.Equals(a.PartialDefinitionPart ?? a, b.PartialDefinitionPart ?? b);

        internal static IOperation? Unwrap(IOperation? operation) {
            while (true) {
                if (operation is IConversionOperation conversion) operation = conversion.Operand;
                else if (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
                else return operation;
            }
        }

        internal static bool IsThis(IOperation? operation) => Unwrap(operation) is IInstanceReferenceOperation {
            ReferenceKind: InstanceReferenceKind.ContainingTypeInstance
        };

        internal static bool InNameOf(IOperation operation) {
            for (var parent = operation.Parent; parent != null; parent = parent.Parent) {
                if (parent is INameOfOperation) return true;
            }
            return false;
        }

        internal static bool IsDirectBody(IOperation operation) {
            for (var parent = operation.Parent; parent != null; parent = parent.Parent) {
                if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation) return false;
            }
            return true;
        }

        internal static ISymbol EnclosingMember(ISymbol owner) {
            while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }) owner = owner.ContainingSymbol;
            return owner;
        }

        internal static ISymbol? ReferencedMember(IOperation? operation) => Unwrap(operation) switch {
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            _ => null
        };

        internal static IOperation? Instance(IOperation operation) => Unwrap(operation) switch {
            IPropertyReferenceOperation property => property.Instance,
            IFieldReferenceOperation field => field.Instance,
            _ => null
        };

        internal static bool MatchesProxy(IOperation operation, IPropertySymbol property, string path) {
            var members = new List<ISymbol>();
            var type = property.ContainingType;
            foreach (var name in path.Split('.')) {
                var member = ExpressionGenerator.FindMember(type, name);
                if (member == null) return false;
                members.Add(member);
                type = (member is IPropertySymbol p ? p.Type : (member as IFieldSymbol)?.Type) as INamedTypeSymbol;
            }
            IOperation? current = operation;
            for (var i = members.Count - 1; i >= 0; i--) {
                if (current == null || !SymbolEqualityComparer.Default.Equals(ReferencedMember(current), members[i])) return false;
                current = Instance(current);
            }
            return IsThis(current);
        }
    }
}