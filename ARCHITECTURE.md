# NINA Solution Architecture

This is the ownership and boundary map for `NINA.sln`. Use it to choose the relevant project document when tracing lifecycle, designing a change or following dependencies. [AGENTS.md](AGENTS.md) holds repository constraints and [CONTRIBUTING.md](CONTRIBUTING.md) covers contributor setup and submission.

## Project Index

The solution separates foundation libraries, runtime domains, shared UI, extensibility and the application shell. The links below cover all 19 NINA project architecture documents.

| Layer | Project And Responsibility | Starting Points And Neighboring Checks |
| --- | --- | --- |
| Foundation | [NINA.Core](NINA.Core/ARCHITECTURE.md): utilities, logging, localization, models, database context and generated protobuf contracts | `NINA.Core.Database`, `Locale.resx`, `NINA/Database` and protobuf consumers |
| Foundation | [NINA.Profile](NINA.Profile/ARCHITECTURE.md): persisted profiles and typed settings | `Profile`, `ProfileService`, defaults, known types, notifications and serialized compatibility |
| Foundation | [NINA.Astrometry](NINA.Astrometry/ARCHITECTURE.md): coordinates, astronomy math, twilight and catalogs | SOFA/NOVAS, database/catalog callers and `NINA.Test/AstrometryTest` |
| Runtime domain | [NINA.Image](NINA.Image/ARCHITECTURE.md): image models, formats, analysis, star detection and rendering helpers | Native assets, autofocus, plate solving, image history and sequencing |
| Runtime domain | [NINA.MGEN](NINA.MGEN/ARCHITECTURE.md): standalone MGEN2/MGEN3 transport and protocol | Runtime DLL layout and the `NINA.Equipment` guider adapter |
| Runtime domain | [NINA.GuideEngine](NINA.GuideEngine/ARCHITECTURE.md): pins internal guider engine (PHD2 port and extensions), free of NINA dependencies | `NINA.GuideEngine.Test`, PHD2 golden/parity data and the host contracts in `Core/Hardware.cs` |
| Runtime domain | [NINA.Equipment](NINA.Equipment/ARCHITECTURE.md): device abstractions and ASCOM/Alpaca/native adapters | Vendor SDKs, profile settings and device mediators |
| Integration | [NINA.Mcp](NINA.Mcp/ARCHITECTURE.md): pins MCP server for AI clients (protocol, jobs, tools); `NINA.Mcp.Adapter` binds it to the NINA mediators and sequencer | `NINA/Program.cs` wiring, `NINA.Mcp.Test`, `PINS_MCP_*` variables |
| Runtime domain | [NINA.Platesolving](NINA.Platesolving/ARCHITECTURE.md): solver integrations and orchestration | `PlateSolverFactory`, capture, centering and rotation |
| Shared UI | [NINA.CustomControlLibrary](NINA.CustomControlLibrary/ARCHITECTURE.md): reusable WPF controls and themes | Control classes, default theme XAML and `Themes/Generic.xaml` |
| Shared UI | [NINA.WPF.Base](NINA.WPF.Base/ARCHITECTURE.md): mediators, shared view models, equipment UI and sky surveys | Interfaces, app DI wiring and concrete handlers; [offline sky-map pipeline](NINA.WPF.Base/SkySurvey/ARCHITECTURE.md) |
| Extensibility | [NINA.Plugin](NINA.Plugin/ARCHITECTURE.md): manifests, loading, installation and compatibility | `PluginLoader`, public contracts, MEF composition and resource dictionaries |
| Sequencing | [NINA.Sequencer](NINA.Sequencer/ARCHITECTURE.md): engine, entities, serialization, targets, templates and expressions | MEF metadata, factory/prototypes, clone/parent/validation and JSON; [editor history](NINA.Sequencer/Editing/ARCHITECTURE.md) |
| Sequencing | [NINA.Sequencer.Generators](NINA.Sequencer.Generators/ARCHITECTURE.md): Roslyn expression generator and diagnostics | Generated contracts, plugin-author diagnostics and expression-backed entity tests |
| Application | [NINA](NINA/ARCHITECTURE.md): executable, DI composition, shell, app views and runtime assets | `App.xaml.cs`, `CompositionRoot.cs`, `Utility/IoCBindings.cs`, `ViewModel` and `View` |
| Packaging | [NINA.Setup](NINA.Setup/ARCHITECTURE.md): WiX MSI | `Product.wxs`, application output and shipped files/directories |
| Packaging | [NINA.SetupBundle](NINA.SetupBundle/ARCHITECTURE.md): WiX Burn bootstrapper | Bundle UI/theme, release-note conversion and chained MSI |
| Verification | [NINA.Test](NINA.Test/ARCHITECTURE.md): NUnit suite | Production-area fixtures, shared bootstrap/assets and x64/native dependencies |
| Verification | [NINA.Benchmark](NINA.Benchmark/ARCHITECTURE.md): BenchmarkDotNet performance checks | Changed production area and matching regression fixtures; not shipped with the app |

The solution project is named `NINA.PlateSolving`; its folder is `NINA.Platesolving`. `Accord.Imaging (NETStandard)` and `nikoncswrapper` are also solution projects and matter during dependency tracing, but have no project architecture documents here.

`NINA.Docs` is a separate git submodule declared in [`.gitmodules`](.gitmodules), outside `NINA.sln`. User documentation changes follow the [documentation contribution route](CONTRIBUTING.md#contributing-documentation).

## Startup And Composition

1. [NINA/App.xaml.cs](NINA/App.xaml.cs) parses command-line options, initializes user settings, loads/selects the active profile, configures logging/notifications and shows the main window.
2. [NINA/CompositionRoot.cs](NINA/CompositionRoot.cs) builds the application service provider and eagerly resolves major view models/controllers.
3. [NINA/Utility/IoCBindings.cs](NINA/Utility/IoCBindings.cs) registers the DI graph with `Microsoft.Extensions.DependencyInjection`.
4. [NINA/MainWindow.xaml](NINA/MainWindow.xaml) and its code-behind host the shell.

Start with `IoCBindings.cs` to find where an app-wide service comes from. New services, mediators and first-level view models may need registrations there; shell-consumed objects also involve `CompositionRoot.cs` and dock/equipment composition.

## Cross-Project Boundaries

### Profiles And Database

`IProfileService` is the central runtime configuration service. Typed settings live in `NINA.Profile`; profile changes can invalidate cached settings references, so consumers often subscribe to `ProfileChanged`. New settings involve interfaces, concrete settings, defaults, known types, notifications and persistence compatibility.

The EF6/SQLite context is `NINA.Core.Database.NINADbContext`. Its runtime SQL lives under `NINA/Database`; schema or initialization work usually spans both. Follow the [database migration rules](CONTRIBUTING.md#database-enhancements) for existing installations.

### Plugins And Sequencer Entities

`NINA.Plugin.PluginLoader` loads built-in sequencer/entity types first, scans plugin DLLs from versioned folders under `%LOCALAPPDATA%\NINA` and composes items, conditions, triggers, containers, dockable view models, pluggable behaviors and equipment providers through MEF. It also merges plugin resource dictionaries into the WPF application resources.

`NINA.ViewModel.Sequencer.SequenceNavigationVM` waits for plugin loading before building `SequencerFactory`. The factory supplies cloneable prototypes to the editor. `NINA.Sequencer.Serialization.SequenceJsonConverter` deserializes through factory-backed creation converters. A new entity or extension point therefore spans its type, MEF metadata, loader composition, factory creation, clone behavior and JSON compatibility.

Keep runtime plugin scanning and manifest rules in `NINA.Plugin`. Expression generation and editor history have their own contracts linked from the project index.

### Shared UI And Mediators

Lower-level code communicates upward through mediator interfaces. Implementations live in `NINA.WPF.Base/Mediator`; `NINA/Utility/IoCBindings.cs` wires them to concrete handlers. Prefer an existing mediator, or a new interface/mediator pair in `NINA.WPF.Base`, when a library needs UI-side behavior.

Keep reusable logic in its owning library and final shell composition in `NINA`. Shared WPF infrastructure provides reusable components; the executable decides how to assemble them into screens.

### Resources And Distribution

`NINA.Core.Locale.Loc` resolves localized strings. Sequence/plugin metadata often carries `Lbl_*` keys for runtime resolution. [Localization guidance](CONTRIBUTING.md#localization) identifies the source resource and Crowdin workflow.

Runtime code relies on files under `NINA/External`, `NINA/Utility`, `NINA/Database` and `NINA/Sequencer/Examples`. New files need matching `NINA/NINA.csproj` output rules, installer authoring when shipped and test output rules when used by tests. Dependency changes also require synchronizing the two license inventories listed in [AGENTS.md](AGENTS.md#resources-and-distribution).

## Verification And Durable Knowledge

Use the [testing map](.agents/skills/nina-repository/references/testing-map.md#routing-table) to select fixtures for the changed boundary. Numerical astronomy changes need reference-value and boundary coverage; WPF changes may require compiled view/template construction; plugin and persistence changes need compatibility checks.

Record durable architectural changes in the owning document. Where practical, enforce recurring contracts with tests or analyzers. User-facing changes may also need `RELEASE_NOTES.md` and documentation in `NINA.Docs`; see [contribution guidance](CONTRIBUTING.md).
