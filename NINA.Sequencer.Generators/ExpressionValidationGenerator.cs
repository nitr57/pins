using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Sequencer.Generators {
    public partial class ExpressionGenerator {
        internal static bool GeneratesValidation(INamedTypeSymbol type) => type.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "NINA.Sequencer.Generators.UsesExpressionsAttribute"
                && a.NamedArguments.Any(p => p.Key == "GenerateValidation" && p.Value.Value is true));

        private static readonly DiagnosticDescriptor ValidationConflict = Error("EXP0002", "Handwritten validation conflicts with generated validation",
            "'{0}' opts into generated validation but declares Validate(). Move its setup to PrepareExpressionValidation() and its domain rules to ValidateAdditional(IList<string>)");
        private static readonly DiagnosticDescriptor InvalidCondition = Error("EXP0003", "Invalid expression validation condition",
            "ValidateWhen on '{0}' must name an accessible, readable instance bool property; '{1}' does not meet that contract");
        private static readonly DiagnosticDescriptor InvalidProxy = Error("EXP0004", "Invalid expression proxy",
            "Proxy on '{0}' must name an accessible, readable and writable numeric target of the same type; '{1}' does not meet that contract");
        private static readonly DiagnosticDescriptor InvalidIssues = Error("EXP0005", "Incompatible Issues property",
            "'{0}' inherits an incompatible Issues member. Generated validation requires a concrete instance IList<string> property with a public getter and an accessible non-init setter");
        private static readonly DiagnosticDescriptor InheritedValidation = Error("EXP0006", "Inherited validation requires explicit composition",
            "'{0}' inherits Validate(). Keep handwritten validation until its inherited validation has been explicitly composed; automatic generation cannot choose whether to replace or call the base method");
        private static readonly DiagnosticDescriptor IssuesConflict = Error("EXP0007", "Handwritten Issues conflicts with generated validation",
            "'{0}' opts into generated validation but declares Issues. Remove the local declaration to use the generated property or a compatible inherited property");
        private static readonly DiagnosticDescriptor ExpressionHelperConflict = Error("EXP0008", "Handwritten member conflicts with generated expression validation helper",
            "'{0}' declares ValidateOwnExpressions, which is reserved for the generated expression validation helper. Rename the handwritten member");

        private static DiagnosticDescriptor Error(string id, string title, string message) =>
            new(id, title, message, "Usage", DiagnosticSeverity.Error, isEnabledByDefault: true);

        internal static ISymbol? FindMember(INamedTypeSymbol? type, string name) {
            while (type != null) {
                var member = type.GetMembers(name).FirstOrDefault();
                if (member != null) return member;
                type = type.BaseType;
            }
            return null;
        }

        private static bool Readable(Compilation compilation, IPropertySymbol property, INamedTypeSymbol owner) =>
            !property.IsStatic && !property.IsIndexer && property.GetMethod != null && compilation.IsSymbolAccessibleWithin(property.GetMethod, owner);

        private static string? Argument(PropertyInfo property, string name) =>
            property.Args.FirstOrDefault(a => a.Key == name).Value.Value as string;

        private static bool ValidateDeclaration(SourceProductionContext context, Compilation compilation, INamedTypeSymbol type, IEnumerable<PropertyInfo?> properties, bool generateValidation) {
            var valid = true;
            void Report(DiagnosticDescriptor descriptor, ISymbol symbol, params object[] arguments) {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), arguments));
                valid = false;
            }
            if (type.GetMembers("ValidateOwnExpressions").Length != 0) {
                Report(ExpressionHelperConflict, type, type.Name);
            }
            if (generateValidation) {
                if (type.GetMembers("Validate").Length != 0) {
                    Report(ValidationConflict, type, type.Name);
                } else if (FindMember(type.BaseType, "Validate") != null) {
                    Report(InheritedValidation, type, type.Name);
                }
                var localIssues = type.GetMembers().FirstOrDefault(m => m.Name == "Issues"
                    || m is IPropertySymbol property && property.ExplicitInterfaceImplementations.Any(i => i.Name == "Issues"
                        && i.ContainingType.ToDisplayString() == "NINA.Sequencer.Validations.IValidatable"));
                if (localIssues != null) {
                    Report(IssuesConflict, localIssues, type.Name);
                }
                var issues = FindMember(type.BaseType, "Issues");
                if (issues != null && !(issues is IPropertySymbol p && Readable(compilation, p, type) && !p.IsAbstract
                    && p.GetMethod!.DeclaredAccessibility == Accessibility.Public && p.SetMethod != null && compilation.IsSymbolAccessibleWithin(p.SetMethod, type)
                    && !p.SetMethod.IsInitOnly && p.Type.ToDisplayString() == "System.Collections.Generic.IList<string>")) {
                    Report(InvalidIssues, type, type.Name);
                }
            }
            foreach (var property in properties.OfType<PropertyInfo>()) {
                var when = Argument(property, "ValidateWhen");
                if (when != null && !(FindMember(type, when) is IPropertySymbol condition && Readable(compilation, condition, type)
                    && condition.Type.SpecialType == SpecialType.System_Boolean)) {
                    Report(InvalidCondition, property.PropertySymbol, property.PropertySymbol.Name, when);
                }
                var proxy = Argument(property, "Proxy");
                if (generateValidation && proxy != null && !ValidProxy(compilation, type, property.PropertySymbol, proxy)) {
                    Report(InvalidProxy, property.PropertySymbol, property.PropertySymbol.Name, proxy);
                }
            }
            return valid;
        }

        private static bool ValidProxy(Compilation compilation, INamedTypeSymbol owner, IPropertySymbol source, string path) {
            INamedTypeSymbol? type = owner;
            var segments = path.Split('.');
            for (var index = 0; index < segments.Length; index++) {
                var member = FindMember(type, segments[index]);
                var last = index == segments.Length - 1;
                ITypeSymbol? valueType;
                if (member is IPropertySymbol property && Readable(compilation, property, owner)
                    && (!last || (property.SetMethod != null && !property.SetMethod.IsInitOnly && compilation.IsSymbolAccessibleWithin(property.SetMethod, owner)))) {
                    valueType = property.Type;
                } else if (member is IFieldSymbol field && !field.IsStatic && compilation.IsSymbolAccessibleWithin(field, owner) && (!last || !field.IsReadOnly)) {
                    valueType = field.Type;
                } else {
                    return false;
                }
                if (last) return !SymbolEqualityComparer.Default.Equals(member, source)
                    && SymbolEqualityComparer.Default.Equals(source.Type, valueType)
                    && valueType.SpecialType is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Int32;
                // A property returning a struct cannot be assigned through a chained proxy.
                if (valueType.IsValueType) return false;
                type = valueType as INamedTypeSymbol;
            }
            return false;
        }

        private static string GenerateValidation(INamedTypeSymbol type) {
            var source = new StringBuilder();
            if (FindMember(type.BaseType, "Issues") == null) {
                source.AppendLine(@"
        private global::System.Collections.Generic.IList<string> expressionIssues = new global::System.Collections.Generic.List<string>();
        [JsonIgnore]
        public global::System.Collections.Generic.IList<string> Issues {
            get => expressionIssues;
            set { expressionIssues = value; RaisePropertyChanged(); }
        }");
            }
            source.AppendLine(@"
        public bool Validate() {
            var issues = new global::System.Collections.Generic.List<string>();
            PrepareExpressionValidation();
            ValidateOwnExpressions(issues);
            ValidateAdditional(issues);
            Issues = issues;
            return issues.Count == 0;
        }

        partial void PrepareExpressionValidation();
        partial void ValidateAdditional(global::System.Collections.Generic.IList<string> issues);");
            return source.ToString();
        }

        private static string GenerateOwnExpressionValidation(IEnumerable<PropertyInfo?> properties, bool generateValidation) {
            var source = new StringBuilder(@"
        private void ValidateOwnExpressions(global::System.Collections.Generic.IList<string> issues) {
");
            foreach (var property in properties.OfType<PropertyInfo>()) {
                var name = property.PropertySymbol.Name;
                var when = Argument(property, "ValidateWhen");
                if (when != null) source.AppendLine($"            if ({when}) {{");
                source.AppendLine($"            Expression.ValidateExpressions(issues, {name}Expression);");
                if (generateValidation && Argument(property, "Proxy") != null) {
                    source.AppendLine($"            ValidateAndSynchronize{name}({name}Expression);");
                    // Evaluation can clear an earlier custom error without changing Value,
                    // so the engine's value-change callback alone is not sufficient.
                    if (property.Args.Any(a => a.Key == "HasValidator" && a.Value.Value is true)) {
                        source.AppendLine($@"            if ({name}Expression.Error != null && !Expression.JustWarnings({name}Expression.Error) && !issues.Contains({name}Expression.Error)) {{
                issues.Add({name}Expression.Error);
            }}");
                    }
                }
                if (when != null) source.AppendLine("            }");
            }
            source.AppendLine("        }");
            return source.ToString();
        }
    }
}
