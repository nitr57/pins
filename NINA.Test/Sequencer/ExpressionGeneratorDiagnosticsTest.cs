using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NINA.Sequencer.Generators;
using NUnit.Framework;
using System.Collections.Immutable;

namespace NINA.Test.Sequencer {
    public partial class ExpressionGeneratorTest {
        private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string? suppress = null) {
            var (compilation, result) = Generate(source);
            Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            if (suppress != null) compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                new Dictionary<string, ReportDiagnostic> { [suppress] = ReportDiagnostic.Suppress }));
            var analyzers = typeof(ExpressionGenerator).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
                .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!).ToImmutableArray();
            return await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
        }

        [TestCase("public double Amount;")]
        [TestCase("public double Amount { get; set; }")]
        [TestCase("public partial double Amount { get; init; }")]
        [TestCase("public static partial double Amount { get; set; }")]
        [TestCase("public partial double Amount { get; private set; }")]
        [TestCase("public partial double Amount { get; }")]
        [TestCase("public double Amount => 10;")]
        [TestCase("public partial double this[int index] { get; set; }")]
        [TestCase("public virtual partial double Amount { get; set; }")]
        [TestCase("internal partial double Amount { get; set; }")]
        public void UnsupportedExpressionDeclarations_ReportAttributeError(string declaration) {
            var source = Source().Replace("public partial double Amount { get; set; }", declaration);
            AssertGeneratorError(source, "EXP0009", "IsExpression", "partial");
        }

        [TestCase("public class Example")]
        [TestCase("public abstract partial class Example")]
        [TestCase("public partial class Example<T>")]
        [TestCase("public partial class Outer { public partial class Example")]
        [TestCase("file partial class Example")]
        [TestCase("public partial record Example")]
        [TestCase("public static partial class Example")]
        public void UnsupportedExpressionOwners_ReportAttributeError(string declaration) {
            var source = Source().Replace("public partial class Example", declaration);
            if (declaration.Contains("Outer")) source += "}";
            AssertGeneratorError(source, "EXP0009", "IsExpression", "class");
        }

        [Test]
        public void MissingUsesExpressions_IsAnActionableError() {
            AssertGeneratorError(Source().Replace("[UsesExpressions(GenerateValidation = true)]", ""),
                "EXP0001", "IsExpression", "UsesExpressions");
        }

        [TestCase("", "GenerateValidation = true")]
        [TestCase("Proxy = nameof(Cache), ", "GenerateValidation = true")]
        [TestCase("", "")]
        public void RequestedValidator_RequiresImplementation(string prefix, string mode) {
            AssertGeneratorError(Source("public double Cache { get; set; }", attribute: mode,
                propertyArguments: prefix + "HasValidator = true"), "EXP0010", "HasValidator", "AmountExpressionValidator");
        }

        [TestCase("partial void AmountExpressionValidator() { }")]
        [TestCase("static partial void AmountExpressionValidator(Expression value) { }")]
        [TestCase("partial void AmountExpressionValidator(ref Expression value) { }")]
        [TestCase("void AmountExpressionValidator(Expression value) { }")]
        [TestCase("partial void AmountExpressionValidator<T>(Expression value) { }")]
        [TestCase("private partial void AmountExpressionValidator(Expression value) { }")]
        public void RequestedValidator_RejectsWrongSignature(string members) {
            AssertGeneratorError(Source(members, propertyArguments: "HasValidator = true"),
                "EXP0010", "HasValidator", "Expression");
        }

        [TestCase("[]")]
        [TestCase("[1]")]
        [TestCase("[1, 10, 0, 1]")]
        [TestCase("[1, 10, 4]")]
        [TestCase("[1, 10, -1]")]
        [TestCase("[1, 10, 0.5]")]
        [TestCase("[double.NaN, 10]")]
        [TestCase("[1, double.NaN]")]
        [TestCase("[10, 1]")]
        [TestCase("[1, 1, 1]")]
        [TestCase("[1, 1, 2]")]
        [TestCase("[1, 1, 3]")]
        [TestCase("[1, 10, double.NaN]")]
        [TestCase("[1, 10, double.PositiveInfinity]")]
        public void MalformedRanges_ReportErrorWithoutCrashingGenerator(string range) {
            AssertGeneratorError(Source(propertyArguments: "Range = " + range), "EXP0011", "Range", "Range");
        }

        [TestCase("[1, 10]")]
        [TestCase("[1, 10, 0]")]
        [TestCase("[1, 10, 1]")]
        [TestCase("[1, 10, 2]")]
        [TestCase("[1, 10, 3]")]
        [TestCase("[1, 1]")]
        [TestCase("[100, 0, 1]")]
        [TestCase("[double.NegativeInfinity, double.PositiveInfinity]")]
        [TestCase("[-10, -1, 3]")]
        [TestCase("[double.MaxValue, 0, 2]")]
        [TestCase("null")]
        public void ValidRangesAndSentinelDefaults_StillCompile(string range) {
            Compile(Source(propertyArguments: "Default = -1, AutoValue = -2, DefaultString = \"Automatic\", Range = " + range));
        }

        [Test]
        public void LiteralFormatting_PreservesEscapesAndSpecialNumbers() {
            const string label = "Camera \"A\"\\path\r\nnext\tline";
            var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(label, true);
            dynamic entity = Compile(Source(propertyArguments: $"DefaultString = {literal}, Default = double.NaN, AutoValue = double.PositiveInfinity"));
            var expression = (NINA.Sequencer.Logic.Expression)entity.AmountExpression;
            Assert.That(expression.DefaultString, Is.EqualTo("{" + label + "}"));
            Assert.That(expression.Default, Is.NaN);
            Assert.That(expression.AutoValue, Is.EqualTo(double.PositiveInfinity));
            dynamic negative = Compile(Source(propertyArguments: "Default = double.NegativeInfinity, AutoValue = 1.2345678901234567"));
            Assert.That((double)negative.AmountExpression.Default, Is.EqualTo(double.NegativeInfinity));
            Assert.That((double)negative.AmountExpression.AutoValue, Is.EqualTo(1.2345678901234567));
        }

        [Test]
        public void EqualTypeNamesInDifferentNamespaces_GenerateIndependentSources() {
            var source = Source() + "\nnamespace Other { " + Source() + "\n}";
            var (compilation, result) = Generate(source);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            Assert.That(result.GeneratedTrees.Length, Is.EqualTo(2));
            Assert.That(result.Results.Single().GeneratedSources.Select(s => s.HintName).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void BadDeclaration_DoesNotStopOtherEntitiesGenerating() {
            var source = Source(propertyArguments: "Range = [1]")
                + "\nnamespace Other { " + Source() + "\n}";
            var (_, result) = Generate(source);
            Assert.That(result.Diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0011" }));
            Assert.That(result.GeneratedTrees.Length, Is.EqualTo(1));
        }

        private static void AssertGeneratorError(string source, string id, string locationText, string correction) {
            var (_, result) = Generate(source);
            var diagnostic = result.Diagnostics.Single(d => d.Id == id);
            Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
            Assert.That(diagnostic.Location.IsInSource, Is.True);
            Assert.That(diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan), Does.Contain(locationText));
            Assert.That(diagnostic.GetMessage(), Does.Contain(correction));
            Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785"), Is.False);
        }

        [TestCase("Issues.Add(\"lost\");")]
        [TestCase("this.Issues.Clear();")]
        [TestCase("Issues = new List<string>();")]
        [TestCase("Issues[0] = \"lost\";")]
        [TestCase("Issues[0] += \"lost\";")]
        [TestCase("Issues.Insert(0, \"lost\");")]
        [TestCase("Issues.Remove(\"lost\");")]
        [TestCase("Issues.RemoveAt(0);")]
        [TestCase("Issues ??= new List<string>();")]
        [TestCase("Issues?.Add(\"lost\");")]
        [TestCase("Issues?.Clear();")]
        [TestCase("((List<string>)Issues).AddRange(new[] { \"lost\" });")]
        public async Task ValidationHooks_ReportDiscardedIssueMutations(string statement) {
            var source = Source($"partial void ValidateAdditional(IList<string> issues) {{ {statement} }}");
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0101", DiagnosticSeverity.Warning, "issues");
            Assert.That(await Analyze(source, "EXP0101"), Is.Empty);
        }

        [Test]
        public async Task PreparationHook_ReportsDiscardedIssuesButAllowsReadsAndDeferredFunctions() {
            var source = Source("""
                partial void PrepareExpressionValidation() { Issues.Add("lost"); }
                partial void ValidateAdditional(IList<string> issues) {
                    issues.Add("retained");
                    var previousCount = Issues.Count;
                    Action later = () => Issues.Add("later");
                    void Later() { Issues.Clear(); }
                }
                """);
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0101", DiagnosticSeverity.Warning, "ValidateAdditional");
        }

        [Test]
        public async Task HandwrittenValidation_RequiresInterfaceAndSuggestsHelper() {
            const string members = "public IList<string> Issues { get; set; } = new List<string>(); public bool Validate() => true;";
            var diagnostics = await Analyze(Source(members, attribute: ""));
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0102", DiagnosticSeverity.Warning, "IValidatable");
            var implemented = Source(members, attribute: "", baseType: "SequenceItem, NINA.Sequencer.Validations.IValidatable");
            diagnostics = await Analyze(implemented);
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0103", DiagnosticSeverity.Info, "ValidateOwnExpressions");
            Assert.That(await Analyze(implemented.Replace("=> true;", "{ ValidateOwnExpressions(Issues); return Issues.Count == 0; }")), Is.Empty);
            Assert.That(await Analyze(implemented, "EXP0103"), Is.Empty);
            Assert.That(await Analyze(Source()), Is.Empty);
        }

        [Test]
        public async Task InheritedValidation_CountsForParticipationButNeedsOwnExpressionComposition() {
            var source = Source(attribute: "", baseType: "Parent") + IssuesBase("""
                public IList<string> Issues { get; set; } = new List<string>();
                public virtual bool Validate() => true;
                """).Replace("abstract class Parent : NINA.Sequencer.SequenceItem.SequenceItem", "abstract class Parent : NINA.Sequencer.SequenceItem.SequenceItem, NINA.Sequencer.Validations.IValidatable")
                .Replace("public IList<string>", "public System.Collections.Generic.IList<string>")
                .Replace("new List<string>()", "new System.Collections.Generic.List<string>()");
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0103" }));
        }

        [TestCase("var value = Amount;")]
        [TestCase("Amount = expression.Value;")]
        [TestCase("Amount++;")]
        public async Task ProxyValidator_ReportsDirectScalarReentry(string statement) {
            var source = Source($"public double Cache {{ get; set; }} partial void AmountExpressionValidator(Expression expression) {{ {statement} }}",
                propertyArguments: "HasValidator = true, Proxy = nameof(Cache)");
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0104", DiagnosticSeverity.Warning, "Amount");
            Assert.That(await Analyze(source, "EXP0104"), Is.Empty);
        }

        [Test]
        public async Task ProxyValidator_AllowsCachedStateNameofAndDeferredAccess() {
            Assert.That(await Analyze(Source("""
                public double Cache { get; set; }
                partial void AmountExpressionValidator(Expression expression) {
                    Cache = expression.Value;
                    var name = nameof(Amount);
                    Func<double> later = () => Amount;
                    double Later() => Amount;
                }
                """, propertyArguments: "HasValidator = true, Proxy = nameof(Cache)")), Is.Empty);
        }

        [TestCase("return Cache;", 1)]
        [TestCase("Cache = 2; return Amount;", 0)]
        [TestCase("Cache += 1; return Amount;", 1)]
        [TestCase("this.Cache++; return Amount;", 1)]
        [TestCase("--Cache; return Amount;", 1)]
        [TestCase("Cache -= 1; return Amount;", 1)]
        [TestCase("return AmountExpression.Value;", 1)]
        [TestCase("var name = nameof(Cache); return Amount;", 0)]
        [TestCase("var other = new Example(); return other.Cache;", 0)]
        public async Task CacheReads_AreBasedOnOperationsAndReceivers(string body, int count) {
            var source = Source("public double Cache { get; set; } public double Read() { " + body + " }",
                propertyArguments: "Proxy = nameof(Cache)");
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics.Select(d => d.Id), Is.All.EqualTo("EXP0100"));
            Assert.That(diagnostics, Has.Length.EqualTo(count));
            Assert.That(await Analyze(source, "EXP0100"), Is.Empty);
        }

        [Test]
        public async Task CacheReads_RecognizeInheritedExpressionsAndAllowMigrationCallbacks() {
            var source = Source("public double Cache { get; set; }", propertyArguments: "Proxy = nameof(Cache)") + """
                namespace GeneratorExample {
                    public class Derived : Example {
                        public double Read() => Cache + AmountExpression.Value;
                        [System.Runtime.Serialization.OnDeserialized]
                        private void Restore(System.Runtime.Serialization.StreamingContext context) { Amount = Cache; }
                    }
                }
                """;
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0100", "EXP0100" }));
        }

        private static void AssertUsageDiagnostic(Diagnostic diagnostic, string id, DiagnosticSeverity severity, string correction) {
            Assert.That(diagnostic.Id, Is.EqualTo(id));
            Assert.That(diagnostic.Severity, Is.EqualTo(severity));
            Assert.That(diagnostic.Location.IsInSource, Is.True);
            Assert.That(diagnostic.GetMessage(), Does.Contain(correction));
        }

        [Test]
        public void ExplicitFalseValidator_PreservesLegacyAndOptInSemantics() {
            AssertGeneratorError(Source(attribute: "", propertyArguments: "HasValidator = false"),
                "EXP0010", "HasValidator", "AmountExpressionValidator");
            Compile(Source(propertyArguments: "HasValidator = false"));
            Compile(Source("partial void AmountExpressionValidator(Expression expression) { }", attribute: "", propertyArguments: "HasValidator = false"));
        }

        [Test]
        public void GlobalNamespaceAndAliasedAttributes_StillGenerate() {
            var source = Source().Replace("namespace GeneratorExample {", "").TrimEnd();
            source = source.Substring(0, source.Length - 1).Replace("[IsExpression(", "[global::NINA.Sequencer.Generators.IsExpressionAttribute(");
            var (compilation, result) = Generate(source);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        }

        [TestCase("Default = Unresolved")]
        [TestCase("HasValidator = Unresolved")]
        [TestCase("Range = [Unresolved, 10]")]
        public void InvalidCompilerConstants_DoNotCrashGenerator(string arguments) {
            var (compilation, result) = Generate(Source(propertyArguments: arguments));
            Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785"), Is.False);
            Assert.That(compilation.GetDiagnostics().Any(d => d.Id == "CS0103"), Is.True);
        }

        [Test]
        public void AnnotationOnAnUnsupportedTarget_DoesNotCrashGenerator() {
            AssertGeneratorError(Source().Replace("[UsesExpressions(GenerateValidation = true)]", "[IsExpression]"),
                "EXP0009", "IsExpression", "partial");
        }

        [Test]
        public async Task ExplicitInterfaceValidation_ComposesHelperWithoutHints() {
            Assert.That(await Analyze(Source("""
                public IList<string> Issues { get; set; } = new List<string>();
                bool NINA.Sequencer.Validations.IValidatable.Validate() { ValidateOwnExpressions(Issues); return true; }
                """, attribute: "", baseType: "SequenceItem, NINA.Sequencer.Validations.IValidatable")), Is.Empty);
            const string members = "public IList<string> Issues { get; set; } = new List<string>(); public bool Validate() => true;";
            Assert.That(await Analyze(Source(members, attribute: ""), "EXP0102"), Is.Empty);
        }

        [TestCase("Action later = () => ValidateOwnExpressions(Issues);")]
        [TestCase("void Later() { ValidateOwnExpressions(Issues); }")]
        [TestCase("new Example().ValidateOwnExpressions(Issues);")]
        public async Task HelperHint_RequiresDirectCompositionOnThisInstance(string statement) {
            var diagnostics = await Analyze(Source("public IList<string> Issues { get; set; } = new List<string>(); public bool Validate() { "
                + statement + " return true; }", attribute: "", baseType: "SequenceItem, NINA.Sequencer.Validations.IValidatable"));
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0103" }));
        }

        [Test]
        public async Task Hooks_RespectInheritedIssuesAndOtherReceivers() {
            var diagnostics = await Analyze(Source("""
                partial void ValidateAdditional(IList<string> currentIssues) {
                    new Example().Issues.Add("other");
                    currentIssues.Add("kept");
                    Issues.Add("lost");
                }
                """, baseType: "Parent") + IssuesBase("public System.Collections.Generic.IList<string> Issues { get; set; } = new System.Collections.Generic.List<string>();"));
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            AssertUsageDiagnostic(diagnostics[0], "EXP0101", DiagnosticSeverity.Warning, "currentIssues");
            Assert.That(diagnostics[0].Location.SourceTree!.GetText().ToString(diagnostics[0].Location.SourceSpan), Is.EqualTo("Issues"));
        }

        [Test]
        public async Task CacheReads_UseSymbolPathsAndIgnoreAliasesAndShadowing() {
            var diagnostics = await Analyze(Source("""
                public sealed class State { public double Offset; }
                public State Data { get; } = new State();
                public double Read() {
                    var Data = new State();
                    var alias = this.Data;
                    return this.Data . Offset + Data.Offset + alias.Offset;
                }
                public double PretendExpressionValidator() => Data.Offset;
                """, propertyArguments: "Proxy = \"Data.Offset\""));
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0100", "EXP0100" }));
            Assert.That(diagnostics.Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan)),
                Is.EquivalentTo(new[] { "this.Data . Offset", "Data.Offset" }));
        }

        [Test]
        public async Task CacheReads_IgnoreShadowedInheritedProxyButRecognizeBaseReceiver() {
            var diagnostics = await Analyze(Source("public double Cache { get; set; }", propertyArguments: "Proxy = nameof(Cache)") + """
                namespace GeneratorExample {
                    public class Derived : Example {
                        public new double Cache { get; set; }
                        public double Read() => Cache + base.Cache;
                    }
                }
                """);
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            Assert.That(diagnostics[0].Location.SourceTree!.GetText().ToString(diagnostics[0].Location.SourceSpan), Is.EqualTo("base.Cache"));
        }

        [TestCase("int")]
        [TestCase("float")]
        [TestCase("uint")]
        [TestCase("string")]
        public void ExistingScalarTypes_StillCompileWithoutAWhitelist(string scalar) {
            Compile(Source().Replace("partial double Amount", "partial " + scalar + " Amount"));
        }

        [Test]
        public async Task InheritedGeneratedValidation_RequiresCompositionForNewExpressions() {
            var source = Source().Replace("private Example(Example other)", "protected Example(Example other)") + """
                namespace GeneratorExample {
                    [UsesExpressions]
                    public partial class Child : Example {
                        public Child() { }
                        private Child(Child other) : base(other) { }
                        [IsExpression] public partial double Extra { get; set; }
                    }
                }
                """;
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0103" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("Child"));
        }

        [Test]
        public async Task CacheReads_AllowInitializationAndOutWritesButRecognizeRefReads() {
            var source = Source("""
                public double Cache;
                public void Read() { Assign(out Cache); Modify(ref Cache); }
                private static void Assign(out double value) { value = 10; }
                private static void Modify(ref double value) { value++; }
                """, propertyArguments: "Proxy = nameof(Cache)").Replace("public Example() { }", "public Example() { var oldValue = Cache; Cache = 10; }");
            var diagnostics = await Analyze(source);
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EXP0100" }));
        }

        [Test]
        public async Task ProxyValidator_IgnoresOtherInstancesAndOtherScalarProperties() {
            Assert.That(await Analyze(Source("""
                public double Cache { get; set; }
                [IsExpression] public partial double Another { get; set; }
                partial void AmountExpressionValidator(Expression expression) {
                    var other = new Example();
                    var value = other.Amount + Another;
                }
                """, propertyArguments: "HasValidator = true, Proxy = nameof(Cache)")), Is.Empty);
        }
    }
}