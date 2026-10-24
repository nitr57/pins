#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace NINA.Sequencer.Generators {
    [Generator]
    public partial class ExpressionGenerator : IIncrementalGenerator {
        private static readonly DiagnosticDescriptor MissingUsesExpressions = new(
            "EXP0001", "IsExpression usage error",
            "Property '{0}' is marked with [IsExpression], but the containing class '{1}' is missing [UsesExpressions]",
            "Usage", DiagnosticSeverity.Error, isEnabledByDefault: true);
        public void Initialize(IncrementalGeneratorInitializationContext context) {

            //Uncomment to attach a debugger for source generation
            //#if DEBUG
            //            if (!Debugger.IsAttached) {//
            //                Debugger.Launch();
            //            }
            //#endif 

            // Discover invalid declarations too, so an annotation never silently disappears.
            var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                ExpressionAnalysis.ExpressionAttributeName,
                predicate: static (node, ct) => true,
                transform: static (ctx, ct) => new ExpressionDeclaration(ctx.TargetSymbol, ctx.Attributes[0]));
            context.RegisterSourceOutput(declarations.Collect().Combine(context.CompilationProvider),
                (output, input) => Execute(output, input.Left, input.Right));
        }

        private void Execute(SourceProductionContext context, ImmutableArray<ExpressionDeclaration> declarations, Compilation compilation) {
            foreach (var declaration in declarations.Where(d => d.Symbol is not IPropertySymbol and not IFieldSymbol)) {
                context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(declaration.Attribute), declaration.Symbol.Name));
            }
            // Group properties by the full metadata name of their containing type
            var groupedByContainingType = declarations.Where(d => d.Symbol is IPropertySymbol or IFieldSymbol)
                .GroupBy(p => p.Symbol.ContainingType.ToDisplayString());

            foreach (var group in groupedByContainingType) {
                var classSymbol = group.First().Symbol.ContainingType;
                var className = classSymbol.Name;
                var ns = classSymbol.ContainingNamespace.IsGlobalNamespace ? "" : classSymbol.ContainingNamespace.ToDisplayString();
                string? broker = null;

                var valid = true;
                foreach (var declaration in group) {
                    valid &= ValidateExpressionDeclaration(context, compilation, declaration);
                }
                if (!valid) continue;

                bool hasUsesExpressions = classSymbol
                        .GetAttributes()
                        .Any(a => a.AttributeClass?.ToDisplayString() == "NINA.Sequencer.Generators.UsesExpressionsAttribute");

                foreach (var attribute in classSymbol.GetAttributes()) {
                    if (attribute.AttributeClass?.ToDisplayString() == "NINA.Sequencer.Generators.UsesExpressionsAttribute") {
                        if (attribute.ConstructorArguments.Length > 0) {
                            broker = attribute.ConstructorArguments[0].Value as string;
                        }
                    }
                }

                // If the class is missing [UsesExpressions ("symbolBroker")], emit a diagnostic and skip generating code
                if (!hasUsesExpressions) {
                    // Create a diagnostic
                    var diag = Diagnostic.Create(
                        MissingUsesExpressions,
                        group.First().Attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation(),
                        group.First().Symbol.Name,
                        classSymbol.Name);

                    context.ReportDiagnostic(diag);
                    // Do NOT generate code for this property
                    continue;
                }

                var generateValidation = GeneratesValidation(classSymbol);
                var properties = group.Select(p => new PropertyInfo(classSymbol, (IPropertySymbol)p.Symbol, p.Attribute.NamedArguments, "")).ToArray();
                if (!ValidateDeclaration(context, compilation, classSymbol, properties, generateValidation)) {
                    continue;
                }

                // Legacy property behavior stays unchanged until the entity explicitly opts in.
                var generatedSource = GeneratePartialClass(ns, className, properties, broker, generateValidation);

                // Add the source using a stable hint name:
                var hintName = $"{classSymbol.ToDisplayString()}_ExpressionAttribute.g.cs";
                context.AddSource(hintName, generatedSource);
            }
        }

        private static string GeneratePartialClass(string namespaceName, string className, IEnumerable<PropertyInfo> properties, string? broker, bool generateValidation) {
            // Build the partial class with one method per property
            var cloneSource = string.Empty;
            var expressionClones = string.Empty;
            var expressionReleases = string.Empty;
            var propertiesSource = string.Empty;
            var methodsSource = string.Empty;

            if (broker != null) {
                cloneSource += $@"
                {broker} = {broker},";
            }

            foreach (var prop in properties) {
                if (prop is null) continue;
                var propSym = prop.PropertySymbol;
                string propName = propSym.Name;
                string fieldName = propName.Substring(0, 1).ToLower() + propName.Substring(1);
                string fieldNameExpression = fieldName + "Expression";
                string propNameExpression = propName + "Expression";
                bool hasValidator = false;
                string? proxy = null;
                bool jsonIgnore = false;

                IPropertySymbol fieldSymbol = (IPropertySymbol)prop.PropertySymbol;
                string fieldType = fieldSymbol.Type.Name;
                if (fieldType == "Int32") fieldType = "int";

                propertiesSource += $@"

        private Expression {fieldNameExpression};
        [JsonProperty (Order = -1)]
        public Expression {propNameExpression} {{
            get {{
                if ({fieldNameExpression} == null) {{
                    {fieldNameExpression} = new Expression(null, null);
                    {fieldNameExpression}.Context = this;
                    {fieldNameExpression}.Type = ""{fieldType}"";";
                foreach (KeyValuePair<string, TypedConstant> kvp in prop.Args) {

                    if (kvp.Key == "HasValidator") {
                        hasValidator = !generateValidation || (bool)kvp.Value.Value!;
                    } else if (kvp.Key == "Proxy") {
                        proxy = kvp.Value.Value as string;
                        jsonIgnore = true;
                    } else if (kvp.Key == "Range" && !kvp.Value.IsNull) {
                        var values = kvp.Value.Values;
                        double min = Convert.ToDouble(values[0].Value, CultureInfo.InvariantCulture);
                        double max = Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture);
                        double r = 0;
                        if (values.Length > 2) {
                            r = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture);
                        }
                        propertiesSource += $@"
                    {fieldNameExpression}.{kvp.Key} = new double[] {{{DoubleLiteral(min)}, {DoubleLiteral(max)}, {DoubleLiteral(r)}}};";
                    } else if (kvp.Key == "Default" || kvp.Key == "AutoValue") {
                        propertiesSource += $@"
                    {fieldNameExpression}.{kvp.Key} = {DoubleLiteral((double)kvp.Value.Value!)};";
                    } else if (kvp.Key == "DefaultString") {
                        propertiesSource += $@"
                    {fieldNameExpression}.{kvp.Key} = {(kvp.Value.IsNull ? "null" : Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral((string)kvp.Value.Value!, true))};";
                    }
                }

                var synchronizeProxy = generateValidation && proxy != null;
                var validator = synchronizeProxy ? $"ValidateAndSynchronize{propName}" : (hasValidator ? $"{propNameExpression}Validator" : null);
                if (validator != null) {
                    propertiesSource += $@"
                    {fieldNameExpression}.Validator = {validator};";
                }

                expressionClones += $@"
            clone.{propNameExpression} = new Expression (this.{propNameExpression}, clone, {(validator != null ? $"clone.{validator}" : "null")});";

                expressionReleases += $@"
            {fieldNameExpression}?.ReleaseConsumers();";

                propertiesSource += $@"
                }}
                return {fieldNameExpression};
            }}
            set {{
                if (ReferenceEquals({fieldNameExpression}, value)) return;
                {fieldNameExpression}?.ReleaseConsumers();
                {fieldNameExpression} = value;
                if (value == null) return;";
                if (generateValidation) {
                    propertiesSource += $@"
                value.Context = this;
                value.Validator = {validator ?? "null"};";
                }
                propertiesSource += $@"
                RaisePropertyChanged();
            }}
        }}";
                if (hasValidator) {
                    propertiesSource += $@"
        
        partial void {propNameExpression}Validator(Expression expr);
";
                }


                if (proxy != null) {
                    var getter = $"get => {proxy};";
                    if (synchronizeProxy) {
                        getter = $@"get {{
                var expression = {propNameExpression};
                expression.Evaluate(true);
                ValidateAndSynchronize{propName}(expression);
                return {proxy};
            }}";
                        methodsSource += $@"
        private void ValidateAndSynchronize{propName}(Expression expression) {{
            {(hasValidator ? $"if (expression.Error == null) {propNameExpression}Validator(expression);" : "")}
            Synchronize{propName}(expression);
        }}

        private void Synchronize{propName}(Expression expression) {{
            if (expression.Error == null) {{
                {proxy} = ({fieldType})expression.Value;
            }}
        }}
";
                    }
                    propertiesSource += $@"

        [Json";
                    propertiesSource += jsonIgnore ? "Ignore" : "Property";
                    propertiesSource += $@"]
        public partial {fieldType} {propName} {{
            {getter}
            set {{
                {propNameExpression}.Definition = Convert.ToString(value, CultureInfo.InvariantCulture);
                {(synchronizeProxy ? $"ValidateAndSynchronize{propName}({propNameExpression});" : $"{proxy} = {propNameExpression}.Value;")}
            }}
        }}
";
                } else {
                    propertiesSource += $@"
        [JsonProperty (Order = 0)]
        public partial {fieldType} {propName} {{
            get {{ 
                {propNameExpression}.Evaluate(true); 
                return ({fieldType}) {propNameExpression}.";
                    if (fieldType == "String") {
                        propertiesSource += "Definition";
                    } else {
                        propertiesSource += "Value";
                    }
                    propertiesSource += $@";
            }}
            set {{
                {propNameExpression}.Definition = ";
                    if (fieldType == "String") {
                        propertiesSource += "value;";
                    } else {
                        propertiesSource += $@"((value == {propNameExpression}.Default || value == {propNameExpression}.AutoValue) && {propNameExpression}.DefaultString != null) ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);";
                    }
                    propertiesSource += $@"
            }}
        }}

        [JsonProperty (Order = 1)]
        public string {propName}Definition {{
            get => {propNameExpression}.Definition;
            set {{
                {propNameExpression}.Definition = value;
            }}
        }}";
                }

            }

            return $@"// <auto-generated />
using System;
using System.Globalization;
using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.Sequencer.Logic;
using NINA.Sequencer.Generators;

{(namespaceName.Length == 0 ? "" : $"namespace {namespaceName} {{")}
    partial class {className}{(generateValidation ? " : global::NINA.Sequencer.Validations.IValidatable" : "")}
    {{
        public override object Clone() {{
            var clone = new {className}(this) {{{cloneSource}
            }};
            {expressionClones}
            AfterClone(this, clone);
            AfterClone(clone);
            return clone;
        }}

        public override void ReleaseExpressionConsumers() {{
            base.ReleaseExpressionConsumers();{expressionReleases}
        }}

        partial void AfterClone({className} clone);
        partial void AfterClone({className} original, {className} clone);
{propertiesSource}
{methodsSource}
{GenerateOwnExpressionValidation(properties, generateValidation)}
{(generateValidation ? GenerateValidation(properties.First()!.ContainingType) : "")}
    }}
{(namespaceName.Length == 0 ? "" : "}")}";
        }

        private sealed record PropertyInfo {
            public PropertyInfo(INamedTypeSymbol containingType, IPropertySymbol propertySymbol,
                IEnumerable<KeyValuePair<string, TypedConstant>> args, string broker) {
                ContainingType = containingType;
                PropertySymbol = propertySymbol;
                Args = args;
                Broker = broker;
            }

            public INamedTypeSymbol ContainingType { get; }
            public IPropertySymbol PropertySymbol { get; }
            public IEnumerable<KeyValuePair<string, TypedConstant>> Args;
            public string Broker;
        }
    }


    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class IsExpressionAttribute : Attribute {
        public IsExpressionAttribute() {
        }

        public double _def = 0;
        public double Default {
            get { return _def; }
            set { _def = value; }
        }
        public double _AutoValue = Double.NaN;
        public double AutoValue {
            get { return _AutoValue; }
            set { _AutoValue = value; }
        }

        public double[] _range = new double[3];
        public double[] Range {
            get { return _range; }
            set { _range = value; }
        }

        public string _defaultString = "";
        public string DefaultString {
            get { return _defaultString; }
            set { _defaultString = value; }
        }

        public bool _hasValidator = false;
        public bool HasValidator {
            get { return _hasValidator; }
            set { _hasValidator = value; }
        }

        public string _proxy = "";
        public string Proxy {
            get { return _proxy; }
            set { _proxy = value; }
        }

        public string ValidateWhen { get; set; } = "";
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class UsesExpressionsAttribute : Attribute {
        public bool GenerateValidation { get; set; }
        public UsesExpressionsAttribute() {
        }
    }
}
