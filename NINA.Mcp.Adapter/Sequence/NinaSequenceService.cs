// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Mcp.Observatory;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.ViewModel.Sequencer;

namespace NINA.Mcp.Adapter.Sequence;

/// <summary>
/// The advanced sequencer part of the MCP observatory. <c>NinaObservatory</c> delegates every sequence member of
/// <see cref="IPinsObservatory"/> to the method of the same signature here.
/// </summary>
/// <remarks>
/// <para>Services: <see cref="ISequenceMediator"/> for everything it offers (templates, add target, set sequence,
/// start, cancel, running state, save) and <see cref="ISequenceNavigationVM"/> (a DI singleton) for the advanced
/// sequencer view model, its root container and its sequencer factory. No reflection is needed for either.</para>
/// <para>Threading: like the Touch-N-Stars and ninaAPI plugins, every change to the sequence tree runs through
/// <c>Application.Current.Dispatcher.Invoke</c>. In headless pins that dispatcher executes inline; going through it
/// keeps the code correct should a real UI dispatcher return. Reads use the containers' lock-protected snapshots.</para>
/// <para>Message boxes: the WPF commands of the sequencer (reset, clear, validation prompt) open message boxes that
/// pins forwards to the Touch-N-Stars clients and waits up to five minutes for. This class never uses those
/// commands; it calls the underlying model methods and validates the sequence itself before starting.</para>
/// </remarks>
public sealed partial class NinaSequenceService {
    private const string NotInitializedMessage =
        "The advanced sequencer is not initialized yet (pins is still starting up). Try again in a few seconds.";

    private const string RunningMessage =
        "The advanced sequence is running. Stop it first with stop_sequence.";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan SyncLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ISequenceMediator sequenceMediator;
    private readonly ISequenceNavigationVM sequenceNavigation;
    private readonly IProfileService profileService;
    private readonly ICameraMediator cameraMediator;
    private readonly IRotatorMediator rotatorMediator;

    // Serializes the editing operations of MCP clients. Stop never waits for it.
    private readonly SemaphoreSlim editLock = new(1, 1);

    public NinaSequenceService(
            ISequenceMediator sequenceMediator,
            ISequenceNavigationVM sequenceNavigation,
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IRotatorMediator rotatorMediator) {
        this.sequenceMediator = sequenceMediator ?? throw new ArgumentNullException(nameof(sequenceMediator));
        this.sequenceNavigation = sequenceNavigation ?? throw new ArgumentNullException(nameof(sequenceNavigation));
        this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        this.cameraMediator = cameraMediator ?? throw new ArgumentNullException(nameof(cameraMediator));
        this.rotatorMediator = rotatorMediator ?? throw new ArgumentNullException(nameof(rotatorMediator));
    }

    /// <summary>True while the advanced sequence runs. Never throws.</summary>
    public bool IsRunning() {
        try {
            if (!sequenceNavigation.Initialized) {
                return false;
            }

            return sequenceNavigation.Sequence2VM?.IsRunning == true;
        } catch (Exception ex) {
            Logger.Error("MCP: reading the sequence run state failed", ex);
            return false;
        }
    }

    /// <summary>Names of the instructions that execute right now, prefixed with their target ("M42: Take Exposure").</summary>
    public IReadOnlyList<string> GetRunningItemNames() {
        try {
            if (!sequenceNavigation.Initialized) {
                return Array.Empty<string>();
            }

            IReadOnlyCollection<ISequenceItem>? items = sequenceMediator.GetAdvancedSequencerCurrentRunningItems();
            if (items == null || items.Count == 0) {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (ISequenceItem item in items) {
                if (item == null) {
                    continue;
                }

                string name = DisplayName(item);
                IDeepSkyObjectContainer? target = ItemUtility.FindDeepSkyObjectContainer(item.Parent);
                if (target != null) {
                    name = $"{DisplayName(target)}: {name}";
                }

                names.Add(name);
            }

            return names;
        } catch (Exception ex) {
            Logger.Error("MCP: reading the running sequence items failed", ex);
            return Array.Empty<string>();
        }
    }

    public async Task<OperationResult> StartSequenceAsync(bool skipValidation, CancellationToken cancellationToken) {
        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Start cancelled before it was requested.");
        }

        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            if (access.ViewModel.IsRunning) {
                return OperationResult.Fail("The advanced sequence is already running.");
            }

            if (!cameraMediator.IsFreeToCapture(access.ViewModel)) {
                return OperationResult.Fail(
                    "The camera is busy (another operation holds it, e.g. a capture, the flat wizard or framing). "
                    + "Wait for it to finish or cancel it, then start again.");
            }

            if (!skipValidation) {
                IReadOnlyList<string> issues = await Task.Run(() => CollectValidationIssues(access.Root), cancellationToken)
                    .ConfigureAwait(false);
                if (issues.Count > 0) {
                    return OperationResult.Fail(FormatIssues(issues));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The issue prompt of NINA (a message box) is always skipped: the check above replaces it. Task.Run keeps
            // the synchronous part of Sequence2VM.StartSequence (SequenceStarting handlers) off the request thread.
            Task runTask = Task.Run(() => RunOnDispatcher(() => sequenceMediator.StartAdvancedSequence(true)));
            ObserveRun(runTask);
            Logger.Info("MCP: advanced sequence start requested");

            return await WaitUntilRunning(access.ViewModel, runTask, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Start cancelled before it was requested.");
        } catch (Exception ex) {
            Logger.Error("MCP: starting the advanced sequence failed", ex);
            return OperationResult.Fail($"Starting the sequence failed: {ex.Message}");
        } finally {
            editLock.Release();
        }
    }

    public OperationResult StopSequence() {
        try {
            if (!sequenceNavigation.Initialized) {
                return OperationResult.Fail(NotInitializedMessage);
            }

            if (!IsRunning()) {
                return OperationResult.Ok("The advanced sequence is not running.");
            }

            RunOnDispatcher(() => sequenceMediator.CancelAdvancedSequence());
            Logger.Info("MCP: advanced sequence stop requested");
            return OperationResult.Ok(
                "Stop requested. The running instruction is cancelled; the sequence ends within a few seconds.");
        } catch (Exception ex) {
            Logger.Error("MCP: stopping the advanced sequence failed", ex);
            return OperationResult.Fail($"Stopping the sequence failed: {ex.Message}");
        }
    }

    public OperationResult ResetSequence() {
        bool locked = false;
        try {
            locked = editLock.Wait(SyncLockTimeout);
            if (!locked) {
                return OperationResult.Fail("Another sequence operation is in progress. Try again shortly.");
            }

            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            if (access.ViewModel.IsRunning) {
                return OperationResult.Fail(RunningMessage);
            }

            RunOnDispatcher(() => access.Root.ResetAll());
            Logger.Info("MCP: advanced sequence progress reset");
            return OperationResult.Ok("The progress of all instructions, conditions and triggers was reset.");
        } catch (Exception ex) {
            Logger.Error("MCP: resetting the advanced sequence failed", ex);
            return OperationResult.Fail($"Resetting the sequence failed: {ex.Message}");
        } finally {
            if (locked) {
                editLock.Release();
            }
        }
    }

    private async Task<OperationResult> WaitUntilRunning(
            ISequence2VM viewModel,
            Task runTask,
            CancellationToken cancellationToken) {
        DateTime deadline = DateTime.UtcNow + StartTimeout;

        while (true) {
            if (viewModel.IsRunning) {
                return OperationResult.Ok(
                    "The advanced sequence is running. Monitor it with get_status, get_sequence and get_recent_images.");
            }

            if (runTask.IsFaulted) {
                Exception? cause = runTask.Exception?.GetBaseException();
                return OperationResult.Fail($"The sequence failed to start: {cause?.Message ?? "unknown error"}");
            }

            if (runTask.IsCompleted) {
                return OperationResult.Ok(
                    "The sequence started and ended immediately. Either nothing was left to run (all instructions "
                    + "finished or skipped; use reset_sequence to run it again) or it failed to start (see the pins log).");
            }

            if (DateTime.UtcNow >= deadline) {
                return OperationResult.Ok(
                    "Start requested; the sequencer is still preparing (e.g. loading linked templates). "
                    + "Check get_status in a moment.");
            }

            try {
                await Task.Delay(StartPollInterval, cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return OperationResult.Fail(
                    "Stopped waiting, but the start had already been requested. Check get_status and use "
                    + "stop_sequence if the sequence should not run.");
            }
        }
    }

    private static void ObserveRun(Task runTask) {
        _ = runTask.ContinueWith(
            t => Logger.Error("MCP: the advanced sequence run ended with an error", t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    /// The checks <c>Sequencer.Start</c> runs before it prompts: every enabled validatable entity, descending into all
    /// enabled containers except immutable ones (those validate their own children).
    /// </summary>
    private static IReadOnlyList<string> CollectValidationIssues(ISequenceContainer root) {
        var issues = new List<string>();
        CollectValidationIssues(root, issues, 0);
        return issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectValidationIssues(ISequenceContainer container, List<string> issues, int depth) {
        if (depth > 64) {
            return;
        }

        if (container is IConditionable conditionable) {
            foreach (ISequenceCondition condition in conditionable.GetConditionsSnapshot()) {
                AddIssues(condition, issues);
            }
        }

        if (container is ITriggerable triggerable) {
            foreach (ISequenceTrigger trigger in triggerable.GetTriggersSnapshot()) {
                AddIssues(trigger, issues);
            }
        }

        foreach (ISequenceItem item in container.GetItemsSnapshot()) {
            AddIssues(item, issues);

            bool descend = item is ISequenceContainer
                && item is not IImmutableContainer
                && item.Status != SequenceEntityStatus.DISABLED;
            if (descend) {
                CollectValidationIssues((ISequenceContainer)item, issues, depth + 1);
            }
        }
    }

    private static void AddIssues(ISequenceEntity entity, List<string> issues) {
        if (entity == null || entity.Status == SequenceEntityStatus.DISABLED) {
            return;
        }

        if (entity is not IValidatable validatable) {
            return;
        }

        try {
            validatable.Validate();
            if (validatable.Issues != null) {
                issues.AddRange(validatable.Issues.ToArray().Select(issue => $"{DisplayName(entity)}: {issue}"));
            }
        } catch (Exception ex) {
            Logger.Error($"MCP: validating {entity.GetType().Name} failed", ex);
            issues.Add($"{DisplayName(entity)}: validation failed ({ex.Message})");
        }
    }

    private static string FormatIssues(IReadOnlyList<string> issues) {
        const int MaxIssues = 20;

        var lines = issues.Take(MaxIssues).Select(issue => "- " + issue).ToList();
        if (issues.Count > MaxIssues) {
            lines.Add($"- ... and {issues.Count - MaxIssues} more");
        }

        return "The sequence was not started because validation found issues:\n"
            + string.Join("\n", lines)
            + "\nFix them (e.g. connect the equipment) or start again with skip_validation=true.";
    }

    private SequencerAccess? TryGetAccess(out string error) {
        error = string.Empty;

        if (!sequenceNavigation.Initialized) {
            error = NotInitializedMessage;
            return null;
        }

        ISequence2VM? viewModel = sequenceNavigation.Sequence2VM;
        ISequenceRootContainer? root = viewModel?.Sequencer?.MainContainer;
        ISequencerFactory? factory = viewModel?.SequencerFactory;
        if (viewModel == null || root == null || factory == null) {
            error = NotInitializedMessage;
            return null;
        }

        return new SequencerAccess(viewModel, root, factory);
    }

    private static string DisplayName(ISequenceEntity entity) {
        string? name = entity.Name;
        if (string.IsNullOrWhiteSpace(name)) {
            return entity.GetType().Name;
        }

        return name;
    }

    private static void RunOnDispatcher(Action action) {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static T RunOnDispatcher<T>(Func<T> function) {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) {
            return function();
        }

        return dispatcher.Invoke(function);
    }

    private sealed record SequencerAccess(ISequence2VM ViewModel, ISequenceRootContainer Root, ISequencerFactory Factory);
}
