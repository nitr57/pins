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

    private const string PreparingMessage =
        "A start of the advanced sequence is being prepared. Try again in a moment, or stop it with stop_sequence.";

    // How long a start job waits for the sequencer to prepare (linked templates after a restart) before it reports the
    // start as still pending. The job, not the request, waits: a client that gives up must not stop a start.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan SyncLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ISequenceMediator sequenceMediator;
    private readonly ISequenceNavigationVM sequenceNavigation;
    private readonly IProfileService profileService;
    private readonly ICameraMediator cameraMediator;
    private readonly IRotatorMediator rotatorMediator;

    // Serializes the editing operations of MCP clients. Stop never waits for it.
    private readonly SemaphoreSlim editLock = new(1, 1);

    // The run of the last start requested over MCP until the start saw it running (it completes when the run ends).
    // Meanwhile the sequencer prepares (IsRunning is set only after the linked templates loaded), and the start counts as
    // running: no second start, no edits, the sequence guard applies.
    private Task? pendingRun;

    // Counts stop requests, so that a start learns of a stop that came while it was asked for, validated, or before the
    // run's own cancellation existed, which the stop could not reach.
    private int stopGeneration;

    // Starts past their first check; only for the answer of a stop.
    private int startsInProgress;

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

    /// <summary>True while the advanced sequence runs or a start over MCP still prepares it. Never throws.</summary>
    public bool IsRunningOrStarting() {
        return IsStartPending() || IsRunning();
    }

    // Clears the pending start once the view model reports the run: from then on it reports it itself, and a hung end of
    // the run (SequenceFinished handlers) must not keep MCP reporting a running sequence, whichever way the start ended.
    private bool IsStartPending() {
        var run = Volatile.Read(ref pendingRun);

        if (run is not { IsCompleted: false }) {
            return false;
        }

        if (IsRunning()) {
            _ = Interlocked.CompareExchange(ref pendingRun, null, run);

            return false;
        }

        return true;
    }

    /// <summary>The number of stop requests so far; a start compares it with the count when it was asked for.</summary>
    public int StopCount => Volatile.Read(ref stopGeneration);

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

    public async Task<OperationResult> StartSequenceAsync(bool skipValidation, int stopCountAtRequest, CancellationToken cancellationToken) {
        Interlocked.Increment(ref startsInProgress);

        try {
            return await StartAsync(skipValidation, stopCountAtRequest, cancellationToken).ConfigureAwait(false);
        } finally {
            Interlocked.Decrement(ref startsInProgress);
        }
    }

    private async Task<OperationResult> StartAsync(bool skipValidation, int stopsAtEntry, CancellationToken cancellationToken) {
        if (Volatile.Read(ref stopGeneration) != stopsAtEntry) {
            return OperationResult.Fail("A stop was requested while the start was being asked for; the sequence was not started.");
        }

        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Start cancelled before it was requested.");
        }

        ISequence2VM viewModel;
        Task runTask;

        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            if (access.ViewModel.IsRunning) {
                return OperationResult.Fail("The advanced sequence is already running.");
            }

            if (IsStartPending()) {
                return OperationResult.Fail("A start of the advanced sequence is still being prepared. Check get_status in a moment.");
            }

            if (!cameraMediator.IsFreeToCapture(access.ViewModel)) {
                return OperationResult.Fail(
                    "The camera is busy (another operation holds it, e.g. a capture, the flat wizard or framing). "
                    + "Wait for it to finish or cancel it, then start again.");
            }

            // Sequence2VM.StartSequence materializes linked templates before it runs; do it before validating, so that
            // issues inside them are found here and not skipped with NINA's validation prompt.
            RunOnDispatcher(() => ResolveLinkedTemplates(access.Root, materializeAll: true));

            if (!skipValidation) {
                IReadOnlyList<string> issues = await Task.Run(() => CollectValidationIssues(access.Root), cancellationToken)
                    .ConfigureAwait(false);
                if (issues.Count > 0) {
                    return OperationResult.Fail(FormatIssues(issues));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref stopGeneration) != stopsAtEntry) {
                return OperationResult.Fail("A stop was requested while the start was being prepared; the sequence was not started.");
            }

            // The issue prompt of NINA (a message box) is always skipped: the check above replaces it. Task.Run keeps
            // the synchronous part of Sequence2VM.StartSequence (SequenceStarting handlers) off the request thread.
            viewModel = access.ViewModel;
            runTask = Task.Run(() => RunOnDispatcher(() => sequenceMediator.StartAdvancedSequence(true)));
            Volatile.Write(ref pendingRun, runTask);
            ObserveRun(runTask);
            Logger.Info("MCP: advanced sequence start requested");
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Start cancelled before it was requested.");
        } catch (Exception ex) {
            Logger.Error("MCP: starting the advanced sequence failed", ex);
            return OperationResult.Fail($"Starting the sequence failed: {ex.Message}");
        } finally {
            editLock.Release();
        }

        // Outside the edit lock: the start counts as running from here (pendingRun), which refuses edits and a second
        // start without making them wait for the sequencer to prepare.
        return await WaitUntilRunning(viewModel, runTask, stopsAtEntry, cancellationToken).ConfigureAwait(false);
    }

    public OperationResult StopSequence() {
        // First: a start that is still asked for or validated, or whose run has no cancellation of its own yet, sees it.
        Interlocked.Increment(ref stopGeneration);

        try {
            if (!sequenceNavigation.Initialized) {
                return OperationResult.Ok("The advanced sequencer is not initialized yet, so nothing is running.");
            }

            bool running = IsRunningOrStarting() || Volatile.Read(ref startsInProgress) > 0;

            // Also cancels a start that is still preparing (IsRunning not yet set). When idle this only cancels the
            // token of the last, finished run, which Sequence2VM.CancelSequence tolerates.
            RunOnDispatcher(() => sequenceMediator.CancelAdvancedSequence());

            if (!running) {
                return OperationResult.Ok("The advanced sequence is not running.");
            }

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

            if (access.ViewModel.IsRunning || IsStartPending()) {
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

    /// <summary>
    /// Waits for the requested run to report running. A stop requested meanwhile, or the cancellation of
    /// <paramref name="cancellationToken"/> (the start job's), cancels the run, repeatedly until it ended, because the
    /// first cancel can come before the run has a cancellation of its own.
    /// </summary>
    /// <remarks>
    /// A start from another client (Touch-N-Stars, the Advanced API) while this one still prepares is a double start NINA
    /// itself does not guard against: it replaces this run's cancellation, and its running state can clear
    /// <see cref="pendingRun"/>, so this answer can then report this run as ended or stopped. Accepted; telling the two
    /// runs apart would need an owner for each start.
    /// </remarks>
    private async Task<OperationResult> WaitUntilRunning(
            ISequence2VM viewModel,
            Task runTask,
            int stopsAtEntry,
            CancellationToken cancellationToken) {
        DateTime deadline = DateTime.UtcNow + StartTimeout;
        bool cancelled = false;
        bool sawRunning = false;

        while (true) {
            cancelled |= cancellationToken.IsCancellationRequested;
            bool stopping = cancelled || Volatile.Read(ref stopGeneration) != stopsAtEntry;

            // Read before IsRunning: a pending start cleared to null then means that IsStartPending saw the sequencer running
            // before this poll's read, so a run not running now has ended.
            var current = Volatile.Read(ref pendingRun);
            bool running = viewModel.IsRunning;

            sawRunning |= running;

            if (stopping) {
                // Another start owns the sequencer: this run ended (IsStartPending cleared it), cancelling would hit the new one.
                if (current is not null && !ReferenceEquals(current, runTask)) {
                    return OperationResult.Fail("The start was stopped; another start of the sequence followed it.");
                }

                // A run that ran and no longer does has ended, also while its end handlers still run.
                if (runTask.IsCompleted || ((sawRunning || current is null) && !running)) {
                    _ = Interlocked.CompareExchange(ref pendingRun, null, runTask);
                    Logger.Info("MCP: advanced sequence start stopped");

                    return OperationResult.Fail(cancelled
                        ? "Start cancelled; the sequence was stopped."
                        : "A stop was requested while the sequence was starting; it was stopped.");
                }

                CancelRun();

                if (DateTime.UtcNow >= deadline) {
                    // Cancelled over and over, it can no longer begin running; from here the view model reports it.
                    _ = Interlocked.CompareExchange(ref pendingRun, null, runTask);

                    return OperationResult.Fail(
                        "The start was stopped, but the sequence has not ended yet. Check get_status and use stop_sequence.");
                }
            } else {
                if (running) {
                    // From here the view model reports the run (see IsStartPending).
                    _ = Interlocked.CompareExchange(ref pendingRun, null, runTask);

                    return OperationResult.Ok(
                        "The advanced sequence is running. Monitor it with get_status, get_sequence and get_recent_images.");
                }

                if (runTask.IsFaulted) {
                    Exception? cause = runTask.Exception?.GetBaseException();
                    return OperationResult.Fail($"The sequence failed to start: {cause?.Message ?? "unknown error"}");
                }

                // Ended, or seen running by someone between two polls and ended since (its end handlers may still run).
                // Both get one answer: which of them a short run lands in depends only on the timing of status polls.
                // NINA's async command catches and logs start failures, so a crashed start also lands here.
                if (runTask.IsCompleted || current is null || !ReferenceEquals(current, runTask)) {
                    return OperationResult.Fail(
                        "The advanced sequence has already ended (it may have run only briefly). Either nothing was left "
                        + "to run (all instructions finished or skipped; use reset_sequence to run it again) or it failed "
                        + "(see get_sequence for failed items, get_recent_images and the pins log).");
                }

                if (DateTime.UtcNow >= deadline) {
                    return OperationResult.Ok(
                        "Start requested; the sequencer is still preparing (e.g. loading linked templates). "
                        + "Check get_status in a moment.");
                }
            }

            try {
                await Task.Delay(StartPollInterval, cancelled ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                cancelled = true;
            }
        }
    }

    private void CancelRun() {
        try {
            RunOnDispatcher(() => sequenceMediator.CancelAdvancedSequence());
        } catch (Exception ex) {
            Logger.Error("MCP: cancelling the pending sequence start failed", ex);
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

    /// <summary>
    /// Mirrors the private <c>Sequence2VM.ResolveLinkedTemplates</c>: refreshes the link state of linked templates from the
    /// template library and, with <paramref name="materializeAll"/>, loads their content. Templates with open edits are
    /// left alone. Runs on the dispatcher.
    /// </summary>
    internal static void ResolveLinkedTemplates(ISequenceContainer container, bool materializeAll, int depth = 0) {
        if (depth > 64) {
            Logger.Warning("MCP: linked template refresh stopped because the linked template nesting is too deep.");
            return;
        }

        if (container is LinkedTemplateContainer linkedTemplate) {
            if (linkedTemplate.HasOpenEdits) {
                return;
            }

            if (materializeAll || linkedTemplate.IsMaterialized) {
                linkedTemplate.TryResolveTemplate();
            } else {
                linkedTemplate.RefreshLinkState();
            }
        }

        foreach (ISequenceContainer child in container.GetItemsSnapshot().OfType<ISequenceContainer>()) {
            ResolveLinkedTemplates(child, materializeAll, depth + 1);
        }
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
