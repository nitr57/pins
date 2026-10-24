using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NINA.Sequencer.Generators;
using NINA.Sequencer.Validations;
using NUnit.Framework;
using System.Reflection;
using System.Collections.Immutable;

namespace NINA.Test.Sequencer {
    [TestFixture]
    public partial class ExpressionGeneratorTest {
        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
        private static readonly Lazy<MetadataReference[]> References = new(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(new[] { typeof(ExpressionGenerator).Assembly.Location, typeof(IValidatable).Assembly.Location,
                    typeof(NINA.Core.Model.ApplicationStatus).Assembly.Location, typeof(Newtonsoft.Json.JsonConvert).Assembly.Location })
                .Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray());

        private static string Source(string members = "", string attribute = "GenerateValidation = true", string propertyArguments = "Default = 10", string baseType = "SequenceItem") => $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using NINA.Core.Model;
            using NINA.Sequencer.SequenceItem;
            using NINA.Sequencer.Generators;
            using NINA.Sequencer.Logic;
            namespace GeneratorExample {
                [UsesExpressions({{attribute}})]
                public partial class Example : {{baseType}} {
                    public Example() { }
                    private Example(Example other) : base(other) { }
                    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
                    [IsExpression({{propertyArguments}})]
                    public partial double Amount { get; set; }
                    {{members}}
                }
            }
            """;

        private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source) {
            var compilation = CSharpCompilation.Create("ExpressionExample_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) }, References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new ExpressionGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return (output, driver.GetRunResult());
        }

        private static string IssuesBase(string members) => $$"""
            namespace GeneratorExample {
                public abstract class Parent : NINA.Sequencer.SequenceItem.SequenceItem {
                    protected Parent() { }
                    protected Parent(Parent other) : base(other) { }
                    {{members}}
                }
            }
            """;

        private static dynamic Compile(string source) {
            var (compilation, result) = Generate(source);
            Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            using var stream = new MemoryStream();
            var emit = compilation.Emit(stream);
            Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics));
            return Activator.CreateInstance(Assembly.Load(stream.ToArray()).GetType("GeneratorExample.Example")!)!;
        }

        [Test]
        public void OptIn_ValidatesEveryPropertyAndRecoversWithoutManualEvaluation() {
            dynamic entity = Compile(Source("[IsExpression(Default = 20)] public partial double AddedLater { get; set; }"));
            var validation = (IValidatable)entity;
            foreach (var name in new[] { "AmountExpression", "AddedLaterExpression" }) {
                var expression = (NINA.Sequencer.Logic.Expression)entity.GetType().GetProperty(name).GetValue(entity);
                expression.Definition = "1 +";
                Assert.That(validation.Validate(), Is.False);
                Assert.That(validation.Issues, Has.Count.EqualTo(1));
                Assert.That(validation.Validate(), Is.False);
                Assert.That(validation.Issues, Has.Count.EqualTo(1));
                expression.Definition = "25";
                Assert.That(validation.Validate(), Is.True);
                Assert.That(validation.Issues, Is.Empty);
            }
        }

        [Test]
        public void HandwrittenValidation_ComposesHelperWithInheritedAndDomainChecks() {
            dynamic entity = Compile(Source("""
                [IsExpression(Default = 20)] public partial double AddedLater { get; set; }
                public bool DeviceConnected { get; set; }
                public override bool Validate() {
                    var valid = base.Validate();
                    var issues = new List<string>(Issues);
                    ValidateOwnExpressions(issues);
                    if (!DeviceConnected) issues.Add("device unavailable");
                    Issues = issues;
                    return valid && issues.Count == 0;
                }
                """, attribute: "", baseType: "Parent, NINA.Sequencer.Validations.IValidatable") + IssuesBase("""
                    public bool ChildValid { get; set; }
                    public int ParentValidations { get; private set; }
                    public IList<string> Issues { get; set; } = new List<string>();
                    public virtual bool Validate() {
                        ParentValidations++;
                        Issues = new List<string>();
                        if (!ChildValid) Issues.Add("child invalid");
                        return ChildValid;
                    }
                    """));
            var validation = (IValidatable)entity;
            entity.AmountExpression.Definition = "1 +";
            entity.AddedLaterExpression.Definition = "2 +";
            Assert.That(validation.Validate(), Is.False);
            Assert.That(validation.Issues, Is.EqualTo(new[] {
                "child invalid", NINA.Core.Locale.Loc.Instance["LblSyntaxError"],
                NINA.Core.Locale.Loc.Instance["LblSyntaxError"], "device unavailable"
            }));
            Assert.That((int)entity.ParentValidations, Is.EqualTo(1));
            entity.ChildValid = true;
            entity.DeviceConnected = true;
            entity.AmountExpression.Definition = "12";
            Assert.That(validation.Validate(), Is.False);
            Assert.That(validation.Issues, Has.Count.EqualTo(1));
            entity.AddedLaterExpression.Definition = "24";
            Assert.That(validation.Validate(), Is.True);
            Assert.That(validation.Issues, Is.Empty);
            Assert.That((int)entity.ParentValidations, Is.EqualTo(3));
            Assert.That(((Type)entity.GetType()).GetMethod("ValidateOwnExpressions", BindingFlags.Public | BindingFlags.Instance), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HandwrittenValidation_HelperHonorsConditionsAndRejectsInvalidConditions(bool invalidCondition) {
            var members = """
                public bool Active { get; set; }
                public IList<string> Issues { get; set; } = new List<string>();
                public bool Validate() {
                    var issues = new List<string>();
                    ValidateOwnExpressions(issues);
                    Issues = issues;
                    return issues.Count == 0;
                }
                """;
            var source = Source(members, attribute: "", propertyArguments: invalidCondition
                ? "ValidateWhen = \"Missing\"" : "Default = 10, ValidateWhen = nameof(Active)");
            if (invalidCondition) {
                Assert.That(Generate(source).Result.Diagnostics.Select(d => d.Id), Does.Contain("EXP0003"));
                return;
            }
            dynamic entity = Compile(source);
            entity.AmountExpression.Definition = "1 +";
            Assert.That((bool)entity.Validate(), Is.True);
            entity.Active = true;
            Assert.That((bool)entity.Validate(), Is.False);
            entity.Active = false;
            Assert.That((bool)entity.Validate(), Is.True);
            entity.Active = true;
            entity.AmountExpression.Definition = "12";
            Assert.That((bool)entity.Validate(), Is.True);
        }

        [TestCase("")]
        [TestCase("GenerateValidation = true")]
        public void HandwrittenExpressionHelper_ProducesConflictDiagnostic(string attribute) {
            var (_, result) = Generate(Source("private void ValidateOwnExpressions(IList<string> issues) { }", attribute: attribute));
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EXP0008"));
        }

        [Test]
        public void ConditionalExpression_RechecksBothModeTransitions() {
            dynamic entity = Compile(Source("public bool Active { get; set; }", propertyArguments: "Default = 10, ValidateWhen = nameof(Active)"));
            entity.AmountExpression.Definition = "1 +";
            var validation = (IValidatable)entity;
            Assert.That(validation.Validate(), Is.True);
            entity.Active = true;
            Assert.That(validation.Validate(), Is.False);
            entity.Active = false;
            Assert.That(validation.Validate(), Is.True);
            entity.Active = true;
            entity.AmountExpression.Definition = "12";
            Assert.That(validation.Validate(), Is.True);
        }

        [Test]
        public void Proxy_SynchronizesOnReadValidationReplacementAndClone() {
            dynamic entity = Compile(Source("public double Cache { get; set; }", propertyArguments: "Default = 10, Proxy = nameof(Cache)"));
            entity.AmountExpression.Definition = "12";
            Assert.That((double)entity.Amount, Is.EqualTo(12));
            entity.Cache = -1;
            Assert.That(((IValidatable)entity).Validate(), Is.True);
            Assert.That((double)entity.Cache, Is.EqualTo(12));
            entity.AmountExpression = new NINA.Sequencer.Logic.Expression(null, null) { Definition = "18" };
            Assert.That((double)entity.Amount, Is.EqualTo(18));
            dynamic clone = entity.Clone();
            Assert.That((double)clone.Amount, Is.EqualTo(18));
            clone.AmountExpression.Definition = "20";
            Assert.That((double)clone.Amount, Is.EqualTo(20));
            Assert.That((double)entity.Amount, Is.EqualTo(18));
            entity.AmountExpression.Definition = "1 +";
            Assert.That(((IValidatable)entity).Validate(), Is.False);
            Assert.That((double)entity.Cache, Is.EqualTo(18));
        }

        [Test]
        public void Hooks_RunBeforeAndAfterExpressionValidationAndReuseIssuesSetter() {
            dynamic entity = Compile(Source("""
                public int IssuesBeforeAdditional { get; private set; }
                partial void PrepareExpressionValidation() {
                    AmountExpression.Range = new double[] { 1, 5, 0 };
                    AmountExpression.Definition = "9";
                }
                partial void ValidateAdditional(IList<string> issues) {
                    IssuesBeforeAdditional = issues.Count;
                    issues.Add("device unavailable");
                }
                """, baseType: "Parent") + IssuesBase("""
                    public int Assignments { get; private set; }
                    private System.Collections.Generic.IList<string> issues = new System.Collections.Generic.List<string>();
                    public System.Collections.Generic.IList<string> Issues { get => issues; protected set { issues = value; Assignments++; } }
                    """));
            entity.AmountExpression.Definition = "9";
            Assert.That(((IValidatable)entity).Validate(), Is.False);
            Assert.That(((IValidatable)entity).Issues, Has.Count.EqualTo(2));
            Assert.That((int)entity.IssuesBeforeAdditional, Is.EqualTo(1));
            Assert.That((int)entity.Assignments, Is.EqualTo(1));
        }

        [TestCase("public bool Validate() => true;", "Default = 10", "EXP0002")]
        [TestCase("", "ValidateWhen = \"Missing\"", "EXP0003")]
        [TestCase("public int Active => 1;", "ValidateWhen = nameof(Active)", "EXP0003")]
        [TestCase("public bool Active { set { } }", "ValidateWhen = nameof(Active)", "EXP0003")]
        [TestCase("public static bool Active => true;", "ValidateWhen = nameof(Active)", "EXP0003")]
        [TestCase("", "Proxy = \"Missing.Value\"", "EXP0004")]
        [TestCase("public string Cache { get; set; }", "Proxy = nameof(Cache)", "EXP0004")]
        [TestCase("public double Cache => 1;", "Proxy = nameof(Cache)", "EXP0004")]
        [TestCase("public double Cache { get; init; }", "Proxy = nameof(Cache)", "EXP0004")]
        [TestCase("", "Proxy = nameof(Amount)", "EXP0004")]
        [TestCase("public class Data { public double Value { get; private set; } } public Data Cache { get; } = new Data();", "Proxy = \"Cache.Value\"", "EXP0004")]
        [TestCase("public IList<string> Issues => new List<string>();", "Default = 10", "EXP0007")]
        [TestCase("public IList<string> Issues { get; set; }", "Default = 10", "EXP0007")]
        [TestCase("public IList<string> Issues;", "Default = 10", "EXP0007")]
        public void InvalidDeclarations_ProduceActionableDiagnostics(string members, string arguments, string id) {
            var (_, result) = Generate(Source(members, propertyArguments: arguments));
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain(id));
        }

        [TestCase("public System.Collections.Generic.IList<string> Issues => new System.Collections.Generic.List<string>();")]
        [TestCase("public System.Collections.Generic.IList<string> Issues { get; private set; }")]
        [TestCase("public System.Collections.Generic.IList<string> Issues { protected get; set; }")]
        [TestCase("public System.Collections.Generic.IList<string> Issues { get; init; }")]
        [TestCase("public static System.Collections.Generic.IList<string> Issues { get; set; }")]
        [TestCase("public abstract System.Collections.Generic.IList<string> Issues { get; set; }")]
        [TestCase("public string Issues { get; set; }")]
        [TestCase("public System.Collections.Generic.IList<string> Issues;")]
        public void IncompatibleInheritedIssues_ProducesDiagnosticInsteadOfHidingIt(string members) {
            var (_, result) = Generate(Source(baseType: "Parent") + IssuesBase(members));
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EXP0005"));
        }

        [Test]
        public void ExplicitLocalIssues_AlsoConflictsWithGeneratedContract() {
            var (_, result) = Generate(Source("IList<string> NINA.Sequencer.Validations.IValidatable.Issues => new List<string>();",
                baseType: "SequenceItem, NINA.Sequencer.Validations.IValidatable"));
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EXP0007"));
        }

        [Test]
        public void GeneratedIssues_InitializesNotifiesAndUsesMutableLists() {
            dynamic entity = Compile(Source());
            var validation = (IValidatable)entity;
            Assert.That(validation.Issues, Is.Not.Null.And.Empty);
            var notifications = new List<string?>();
            ((System.ComponentModel.INotifyPropertyChanged)entity).PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            var assigned = new List<string> { "device unavailable" };
            entity.Issues = assigned;
            Assert.That(notifications, Is.EqualTo(new[] { "Issues" }));
            Assert.That(validation.Issues, Is.SameAs(assigned));
            Assert.That(validation.Issues.IsReadOnly, Is.False);
            assigned.Add("later change");
            Assert.That(validation.Issues, Has.Count.EqualTo(2));
            Assert.That(Newtonsoft.Json.Linq.JObject.FromObject((object)entity).Property("Issues"), Is.Null);

            entity.AmountExpression.Definition = "1 +";
            notifications.Clear();
            Assert.That(validation.Validate(), Is.False);
            var previous = validation.Issues;
            Assert.That(notifications.Count(n => n == "Issues"), Is.EqualTo(1));
            entity.AmountExpression.Definition = "12";
            notifications.Clear();
            Assert.That(validation.Validate(), Is.True);
            Assert.That(validation.Issues, Is.Empty.And.Not.SameAs(previous));
            Assert.That(previous, Has.Count.EqualTo(1));
            Assert.That(notifications.Count(n => n == "Issues"), Is.EqualTo(1));
        }

        [TestCase("public")]
        [TestCase("protected")]
        public void InheritedIssues_IsReusedWithoutRedeclaration(string setterAccess) {
            var access = setterAccess == "public" ? "" : "protected ";
            dynamic entity = Compile(Source(baseType: "Parent") + IssuesBase($$"""
                private System.Collections.Generic.IList<string> issues = new System.Collections.Generic.List<string>();
                public int Assignments { get; private set; }
                public System.Collections.Generic.IList<string> Issues {
                    get => issues;
                    {{access}}set { issues = System.Collections.Immutable.ImmutableList.CreateRange(value); Assignments++; }
                }
                """));
            var type = (Type)entity.GetType();
            Assert.That(type.GetProperty("Issues", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Null);
            entity.AmountExpression.Definition = "1 +";
            Assert.That(((IValidatable)entity).Validate(), Is.False);
            Assert.That(((IValidatable)entity).Issues.IsReadOnly, Is.True);
            Assert.That((int)entity.Assignments, Is.EqualTo(1));
            entity.AmountExpression.Definition = "12";
            Assert.That(((IValidatable)entity).Validate(), Is.True);
            Assert.That((int)entity.Assignments, Is.EqualTo(2));
        }

        [Test]
        public void InheritedIssues_MutableSetterIsReused() {
            dynamic entity = Compile(Source(baseType: "Parent")
                + IssuesBase("public System.Collections.Generic.IList<string> Issues { get; set; } = new System.Collections.Generic.List<string>();"));
            var assigned = new List<string> { "device unavailable" };
            entity.Issues = assigned;
            Assert.That(((IValidatable)entity).Issues, Is.SameAs(assigned));
            Assert.That(((IValidatable)entity).Validate(), Is.True);
            Assert.That(((IValidatable)entity).Issues.IsReadOnly, Is.False);
        }

        [Test]
        public void LegacyMode_KeepsHandwrittenValidationAndProxyGetter() {
            dynamic entity = Compile(Source("public double Cache { get; set; } public bool Validate() => true; public IList<string> Issues { get; set; } = new List<string>();", attribute: "", propertyArguments: "Proxy = nameof(Cache)"));
            entity.Cache = 7;
            entity.AmountExpression.Definition = "21";
            Assert.That((double)entity.Amount, Is.EqualTo(7));
            Assert.That(entity is IValidatable, Is.False);
        }

        [Test]
        public void Proxy_CustomValidatorRunsBeforeSynchronization() {
            dynamic entity = Compile(Source("""
                public double Cache { get; set; }
                partial void AmountExpressionValidator(Expression expression) {
                    if (expression.Value > 20) expression.Error = "domain limit";
                }
                """, propertyArguments: "Default = 10, Proxy = nameof(Cache), HasValidator = true"));
            entity.Amount = 12;
            Assert.That((double)entity.Cache, Is.EqualTo(12));
            entity.Amount = 30;
            Assert.That(((IValidatable)entity).Validate(), Is.False);
            Assert.That((double)entity.Cache, Is.EqualTo(12));
            entity.Amount = 18;
            Assert.That(((IValidatable)entity).Validate(), Is.True);
            Assert.That((double)entity.Cache, Is.EqualTo(18));
        }

        [Test]
        public void InheritedValidation_RequiresExplicitComposition() {
            var source = Source(baseType: "Parent") + """
                namespace GeneratorExample {
                    public abstract class Parent : NINA.Sequencer.SequenceItem.SequenceItem {
                        protected Parent() { }
                        protected Parent(Parent other) : base(other) { }
                        public virtual bool Validate() => false;
                    }
                }
                """;
            Assert.That(Generate(source).Result.Diagnostics.Select(d => d.Id), Does.Contain("EXP0006"));
        }

        [Test]
        public async Task CacheReadAnalyzer_FlagsRuntimeReadsButAllowsCallbacksAndLegacyMode() {
            const string members = """
                public double Cache { get; set; }
                public double RuntimeRead() => AmountExpression.Value + this.Cache;
                public double CachedProperty => AmountExpression.Value;
                partial void AmountExpressionValidator(Expression expression) { Cache = AmountExpression.Value; }
                [System.Runtime.Serialization.OnDeserialized]
                private void Restore(System.Runtime.Serialization.StreamingContext context) { Amount = Cache; }
                """;
            foreach (var mode in new[] { "GenerateValidation = true", "" }) {
                var (compilation, _) = Generate(Source(members, attribute: mode,
                    propertyArguments: "Default = 10, Proxy = nameof(Cache), HasValidator = true"));
                var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ExpressionCacheReadAnalyzer())).GetAnalyzerDiagnosticsAsync();
                Assert.That(diagnostics.Select(d => d.Id), Is.All.EqualTo("EXP0100"));
                Assert.That(diagnostics, Has.Length.EqualTo(mode.Length > 0 ? 3 : 0));
            }
        }
    }
}
