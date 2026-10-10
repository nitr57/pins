# AGENTS.md

Guidance for work in `NINA.sln`. Keep this entry point compact and put subsystem details in the owning architecture document.

## Read For The Task

Use the relevant route when more context is needed. Guidance already loaded for the task does not need to be reread.

| Task | Reference |
| --- | --- |
| Find ownership, trace lifecycle or change a cross-project boundary | [Solution architecture and project index](ARCHITECTURE.md), then the owning project's linked document |
| Select tests, build on Windows or diagnose verification failures | [NINA repository skill](.agents/skills/nina-repository/SKILL.md) and its task-specific references |
| Prepare an upstream contribution, release note or documentation update | [Contribution guide](CONTRIBUTING.md) |
| Fix prose or links | The affected document and its incoming links; application builds are unnecessary |

## Runtime Model (pins fork)

PI 'N' Stars (pins) is a Linux fork of N.I.N.A. (see [`README.md`](README.md)) and runs **headless**. This changes how the upstream-named projects below should be read:

- **Target platform is Linux** (down to Raspberry Pi). Code, scripts, and runtime assumptions should not assume Windows. `NINA.INDI`, for example, shells out to `indiserver`/`mkfifo`/`pkill`.
- **There is no WPF window/shell as the user surface.** The app is not launched as an interactive WPF desktop application the way upstream NINA is, so XAML views and the main-window shell are largely irrelevant to the running product.
- **The WPF view models still execute.** Projects like `NINA.WPF.Base` (and the view models under `NINA`) remain the live runtime/business layer — they are *not* dead code. Do not delete or gut view models on the assumption that "headless means no WPF"; only the XAML/view shell is unused.
- **The user-facing UI is the Touch-N-Stars Vue app**, which drives the backend over HTTP/WebSocket through the `ninaAPI` plugin (under `NINA.Plugins/`). New user-facing behavior is generally surfaced through an API endpoint consumed by that frontend, not through a WPF view.

When a task touches "UI", confirm whether it means the WPF layer (usually not the surface here) or the Vue frontend + `ninaAPI` (usually what users actually see).

`NINA.Docs` is a separate [documentation repository](https://github.com/isbeorn/nina.docs.git) included as a submodule. It is outside `NINA.sln`; coordinate user-facing documentation there when applicable.

## Working Boundaries

- Carry authorized local work through implementation and appropriate verification without repeated confirmation. Upstream discussion requirements apply to contribution proposals and major changes; they are not a separate approval gate for an agreed local task.
- Preserve existing architecture, public interfaces and serialized compatibility unless the task requires changing them. Plugin consumers and saved profiles/sequences can outlive the current build.
- Keep reusable logic in its owning library, app-shell composition in `NINA` and plugin loading rules in `NINA.Plugin`. Use shared mediator interfaces for communication from libraries to UI handlers.
- Treat versioned repository files as the durable source of project knowledge. Record newly discovered contracts in the nearest doc, test or analyzer when they will help future work.

## Style

- Follow [`.editorconfig`](.editorconfig) for C#. Preserve touched files' line endings; use CRLF for new files unless the location dictates otherwise.
- Keep C# declarations readable: separate constructors, properties, methods and type declarations with one blank line. Keep related fields together and separate field groups from other members. Match the surrounding same-line brace style.
- Within methods, separate guard clauses and distinct logical steps with blank lines. Expand control-flow bodies and long expressions instead of compressing them onto one line; wrap long parameter and argument lists consistently. Review new and touched code for this spacing before handoff, even when an automatic formatter preserves compact code.
- Follow surrounding XAML style; there is no repository-wide XAML formatter configuration.
- Prefer modern C# supported by the project and `CommunityToolkit.Mvvm` for new or refactored MVVM code where it fits.
- Avoid new warnings. Report any intentional deferral with its reason.

## NINA-Specific Constraints

### State And Extension Contracts

- [`NINA/ARCHITECTURE.md`](NINA/ARCHITECTURE.md)
- [`NINA.Astrometry/ARCHITECTURE.md`](NINA.Astrometry/ARCHITECTURE.md)
- [`NINA.Benchmark/ARCHITECTURE.md`](NINA.Benchmark/ARCHITECTURE.md)
- [`NINA.Core/ARCHITECTURE.md`](NINA.Core/ARCHITECTURE.md)
- [`NINA.CustomControlLibrary/ARCHITECTURE.md`](NINA.CustomControlLibrary/ARCHITECTURE.md)
- [`NINA.Equipment/ARCHITECTURE.md`](NINA.Equipment/ARCHITECTURE.md)
- [`NINA.GuideEngine/ARCHITECTURE.md`](NINA.GuideEngine/ARCHITECTURE.md)
- [`NINA.Image/ARCHITECTURE.md`](NINA.Image/ARCHITECTURE.md)
- [`NINA.INDI/ARCHITECTURE.md`](NINA.INDI/ARCHITECTURE.md)
- [`NINA.MGEN/ARCHITECTURE.md`](NINA.MGEN/ARCHITECTURE.md)
- [`NINA.Platesolving/ARCHITECTURE.md`](NINA.Platesolving/ARCHITECTURE.md)
- [`NINA.Plugin/ARCHITECTURE.md`](NINA.Plugin/ARCHITECTURE.md)
- [`NINA.Profile/ARCHITECTURE.md`](NINA.Profile/ARCHITECTURE.md)
- [`NINA.Sequencer/ARCHITECTURE.md`](NINA.Sequencer/ARCHITECTURE.md)
- [`NINA.Sequencer.Generators/ARCHITECTURE.md`](NINA.Sequencer.Generators/ARCHITECTURE.md)
- [`NINA.Setup/ARCHITECTURE.md`](NINA.Setup/ARCHITECTURE.md)
- [`NINA.SetupBundle/ARCHITECTURE.md`](NINA.SetupBundle/ARCHITECTURE.md)
- [`NINA.Test/ARCHITECTURE.md`](NINA.Test/ARCHITECTURE.md)
- [`NINA.WPF.Base/ARCHITECTURE.md`](NINA.WPF.Base/ARCHITECTURE.md)
- Typed settings belong in `NINA.Profile`. Account for `IProfileService.ProfileChanged` when retaining settings references and preserve defaults, notifications and persisted compatibility.
- Database changes span `NINA.Core.Database.NINADbContext` and `NINA/Database`. Follow the [migration rules](CONTRIBUTING.md#database-enhancements); changing initial SQL does not migrate existing installations.
- Sequence entities need consistent MEF metadata, factory creation, cloning, parent attachment and validation. Preserve sequence JSON and plugin contracts; consult the [sequencer architecture](NINA.Sequencer/ARCHITECTURE.md) when changing these paths.
- For expression-backed entities, use the [generator contract and diagnostics](NINA.Sequencer.Generators/ARCHITECTURE.md). Evaluate live values through generated scalar properties; attachment and watchdog ordering remain the entity's responsibility.

### Resources And Distribution

- Localize user-visible strings through `NINA.Core.Locale.Loc`. Edit only `NINA.Core/Locale/Locale.resx`; translated `Locale.<culture>.resx` files are managed by Crowdin.
- When adding, removing or replacing a dependency, synchronize `NINA/View/About/ThirdPartyLicensesView.xaml` and `NINA/3rd-party-licenses.txt`. Remove stale entries and record any deliberate choice among multiple licenses consistently.
- Runtime files must be copied by `NINA/NINA.csproj` and packaged by `NINA.Setup` when needed. Check test output copying for assets used by tests. Common asset roots are `NINA/External`, `NINA/Utility`, `NINA/Database` and `NINA/Sequencer/Examples`.

### Science And WPF

- Base astronomical and other sensitive numerical changes on published papers, standards or official model documentation. Use documented reference values, boundary cases and regression tests; SOFA/NOVAS examples already exist in `NINA.Test/AstrometryTest`.
- WPF tests that construct views or use `Application.Current.Resources` need STA and generally `[NonParallelizable]`. Compile affected XAML and instantiate the relevant view/template when practical; compilation alone misses runtime resource and binding failures.
- Isolate file-writing tests in temporary paths or injected storage unless the real user-storage location is the behavior under test.

## Verification And Completion

- Select checks for the changed behavior using the [testing map](.agents/skills/nina-repository/references/testing-map.md#routing-table). Broaden when shared contracts, persistence, numerical behavior or UI integration are affected.
- For bug fixes, reproduce the failure at the closest meaningful layer when practical. Cover paired operations and relevant boundaries, including clone/reset/attachment paths for sequencer changes.
- Run relevant checks after the final source edit and inspect the final diff. Once those checks pass, repeat or broaden only for new changes, failures or unresolved concerns.
- For prose-only changes, check links, anchors, moved references and formatting. For skill changes, also validate metadata and discovery; for test-command changes, verify filters against discovery.
- Report substantive changes, checks performed and any verification gap. Do not report unverified behavior as complete. Include a proposed PR title with a code handoff.
- Focused local checks do not replace required upstream CI. Submission requirements live in [CONTRIBUTING.md](CONTRIBUTING.md#pull-requests).

### Runtime Domain Libraries

- `NINA.Image`
  Image model, file formats, analysis, star detection, rendering helpers.
- `NINA.MGEN`
  Standalone MGEN2/MGEN3 transport/protocol library.
- `NINA.Equipment`
  Device abstractions and concrete ASCOM/Alpaca/native adapters.
- `NINA.INDI`
  pins-specific INDI integration: indiserver process lifecycle, INDI XML protocol, global property store, and per-device-type adapters consumed by `NINA.Equipment`. (Not present in upstream NINA.)
- `NINA.GuideEngine`
  pins internal guider engine: PHD2-port star finding, calibration, guide algorithms, guiding loop, coach and incident recorder, with no NINA dependencies; tested by `NINA.GuideEngine.Test`. (Not present in upstream NINA.)
- `NINA.Platesolving`
  Plate-solver integrations and orchestration.

### Shared UI Infrastructure

- `NINA.CustomControlLibrary`
  Reusable custom WPF controls and themes.
- `NINA.WPF.Base`
  Shared mediators, dockable/view-model base classes, equipment UI support, sky survey subsystem.

### Extensibility And Sequencing

- `NINA.Plugin`
  Plugin manifests, loading, installation, compatibility checks, MEF composition.
- `NINA.Sequencer`
  Advanced sequencer engine, entity model, serialization, target/template storage, expressions/symbols.
- `NINA.Sequencer.Generators`
  Roslyn source generator used by sequencer expression-backed properties.

### App Shell, Packaging, Verification

- `NINA`
  Executable WPF app, DI composition root, main shell, app-specific views/view models, runtime assets.
- `NINA.Setup`
  WiX MSI project.
- `NINA.SetupBundle`
  WiX Burn bootstrapper.
- `NINA.Test`
  NUnit test suite.
- `NINA.Benchmark`
  BenchmarkDotNet performance verification; it is a developer tool and is not shipped with the application.

### Non-`NINA.*` Solution Projects

These are in `NINA.sln` and matter during dependency tracing, but they do not have `ARCHITECTURE.md` files from the previous documentation pass:

- `Accord.Imaging (NETStandard)`
- `nikoncswrapper`

## Startup And Composition

The executable startup path is code-driven:

1. `NINA/App.xaml.cs`
   Parses command-line options, initializes user settings, loads/selects the active profile, configures logging and notifications, and shows the main window.
2. `NINA/CompositionRoot.cs`
   Builds the application service provider and eagerly resolves major view models/controllers.
3. `NINA/Utility/IoCBindings.cs`
   Registers the DI graph with `Microsoft.Extensions.DependencyInjection`.
4. `NINA/MainWindow.xaml(.cs)`
   Hosts the shell.

If you are unsure where an app-wide service comes from, start with `IoCBindings.cs`.

## Cross-Cutting Runtime Rules

### Profiles And Settings

- `IProfileService` is the central runtime configuration service.
- Typed settings live in `NINA.Profile`, not in scattered ad hoc files.
- A profile change can invalidate cached settings references; many components subscribe to `ProfileChanged` for that reason.

### Database

- The EF6/SQLite context lives in `NINA.Core.Database.NINADbContext`.
- The SQL files it consumes at runtime live under `NINA/Database`.
- A schema or initialization change usually touches both places.

### Localization

- User-visible labels are generally backed by `NINA.Core.Locale.Loc`.
- Sequence/plugin metadata often passes label keys like `Lbl_*` and resolves them at runtime.
- For locale additions or label changes, edit only `NINA.Core/Locale/Locale.resx`, which is the source resource file in the current solution layout.
- Do not manually edit the other `Locale.<culture>.resx` files; those are managed through Crowdin, as noted in [`CONTRIBUTING.md`](CONTRIBUTING.md).

### Third-Party Licenses

- Any third-party package addition, removal, or replacement requires a license metadata check before the change is complete.
- Keep both the in-app list in `NINA/View/About/ThirdPartyLicensesView.xaml` and the bundled text file `NINA/3rd-party-licenses.txt` synchronized with the actual dependency set.
- Remove stale license entries when a package is no longer used.
- If a dependency offers multiple licenses and this project intentionally uses one of them, document the chosen license consistently in both places.

### Native Runtime Assets

Runtime code expects files in the application output layout, especially under:

- `NINA/External`
- `NINA/Utility`
- `NINA/Database`
- `NINA/Sequencer/Examples`

If code starts depending on a new runtime file, the change is not complete until:

- the file is copied by `NINA.csproj`
- the installer packages it when necessary (`NINA.Setup`)

## Plugin And Sequencer Surface

The plugin and sequencer systems are tightly connected.

### Plugin Loading

`NINA.Plugin.PluginLoader`:

- loads built-in sequencer/entity types first
- scans plugin DLLs from versioned folders under `%LOCALAPPDATA%\\NINA`
- composes sequence items, conditions, triggers, containers, dockable view models, pluggable behaviors, and equipment providers through MEF
- merges plugin resource dictionaries into the WPF application resources

### Sequencer Consumption

The main app does not hard-code the final sequencer palette. Instead:

- `NINA.ViewModel.Sequencer.SequenceNavigationVM` waits for plugin loading, then builds `SequencerFactory`
- `SequencerFactory` exposes cloneable entity prototypes to the sequence editor
- `NINA.Sequencer.Serialization.SequenceJsonConverter` deserializes through factory-backed creation converters

If you add a new sequence entity or extension point, check all of:

- the sequencer type itself
- MEF export metadata
- plugin loader composition
- serialization/clone behavior

## Shared UI And Mediators

Lower-level runtime code communicates upward through mediator interfaces rather than directly referencing concrete WPF view models.

Primary mediator implementations live in `NINA.WPF.Base/Mediator`.

The executable wires those mediators to concrete handlers through DI in `NINA/Utility/IoCBindings.cs`.

If a library project needs to trigger UI-side behavior, prefer an existing mediator or add a new interface/mediator pair in `NINA.WPF.Base` instead of reaching into `NINA` directly.

## Scientific And Numerical Correctness

- `NINA.Astrometry` already depends on established astronomy libraries and models through `SOFA` and `NOVAS`, and `NINA.Test` preloads `SOFAlib.dll` plus `NOVAS31lib.dll` during test bootstrap.
- For astronomical calculations and other mathematically sensitive code, prefer algorithms that can be traced to published scientific papers, standards, or official reference documentation for the underlying model instead of ad hoc derivations.
- Validate those changes with extensive automated tests in `NINA.Test`, using reference values, edge cases, and regression coverage rather than only spot-checking results manually.
- Existing tests already follow that pattern in places such as `NINA.Test/AstrometryTest/AstrometryTest.cs`, which includes cases annotated as coming from documented SOFA values and other cited reference data.

## Test Bootstrap Constraints

- WPF-facing tests that instantiate `System.Windows.Application`, depend on `Application.Current.Resources`, or construct XAML-backed views should run STA and generally be `[NonParallelizable]`.
- New file-writing tests should prefer isolated temp paths or injectable path providers over shared user locations such as `%LOCALAPPDATA%`, unless the shared location is the behavior under test.

## Change Discipline

- Keep reusable logic out of `NINA` when a lower library already owns that concern.
- Keep UI shell composition out of `NINA.WPF.Base`; that project provides shared infrastructure, not final app assembly.
- Keep plugin runtime rules centralized in `NINA.Plugin`; do not duplicate plugin scanning or manifest logic in app view models.
- Keep runtime file layout expectations synchronized between code, build output, and installer authoring.
- If a boundary matters enough to document, consider whether a focused unit test, analyzer, or CI check can enforce it.
- For concrete owner and verification routing, use `.codex/skills/nina-repository/references/project-map.md` and `.codex/skills/nina-repository/references/testing-map.md`.
