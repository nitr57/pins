// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Logic;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Serialization;

namespace NINA.Mcp.Adapter.Sequence;

public sealed partial class NinaSequenceService {
    private const int MaxSequenceNameLength = 100;

    public async Task<OperationResult> LoadSequenceAsync(string name, CancellationToken cancellationToken) {
        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Loading was cancelled; the current sequence is unchanged.");
        }

        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            if (access.ViewModel.IsRunning) {
                return OperationResult.Fail(RunningMessage);
            }

            string? file = ResolveSavedSequenceFile(name, out error);
            if (file == null) {
                return OperationResult.Fail(error);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string title = Path.GetFileNameWithoutExtension(file);
            string message = await Task.Run(() => RunOnDispatcher(() => LoadFromFile(access, file, title)), CancellationToken.None)
                .ConfigureAwait(false);

            Logger.Info($"MCP: loaded sequence {file}");
            return OperationResult.Ok(message);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Loading was cancelled; the current sequence is unchanged.");
        } catch (Exception ex) {
            Logger.Error($"MCP: loading sequence '{name}' failed", ex);
            return OperationResult.Fail($"Loading the sequence '{name}' failed: {ex.Message}");
        } finally {
            editLock.Release();
        }
    }

    public async Task<OperationResult> AddTargetAsync(TargetRequest request, CancellationToken cancellationToken) {
        if (request == null) {
            return OperationResult.Fail("No target given.");
        }

        string? requestError = ValidateTargetRequest(request);
        if (requestError != null) {
            return OperationResult.Fail(requestError);
        }

        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Adding the target was cancelled; the sequence is unchanged.");
        }

        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(request.TemplateName)) {
                return await Task.Run(() => RunOnDispatcher(() => AddTargetFromTemplate(request)), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return await Task.Run(() => RunOnDispatcher(() => AddTargetFromPlan(access, request)), CancellationToken.None)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Adding the target was cancelled; the sequence is unchanged.");
        } catch (Exception ex) {
            Logger.Error($"MCP: adding target '{request.Name}' failed", ex);
            return OperationResult.Fail($"Adding the target '{request.Name}' failed: {ex.Message}");
        } finally {
            editLock.Release();
        }
    }

    public async Task<OperationResult> ClearTargetsAsync(CancellationToken cancellationToken) {
        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Clearing was cancelled; the sequence is unchanged.");
        }

        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            if (access.ViewModel.IsRunning) {
                return OperationResult.Fail(RunningMessage);
            }

            ISequenceContainer? targetArea = FindTargetArea(access.Root);
            if (targetArea == null) {
                return OperationResult.Fail("The loaded sequence has no target area.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            int removed = await Task.Run(() => RunOnDispatcher(() => ClearContainerItems(targetArea)), CancellationToken.None)
                .ConfigureAwait(false);

            Logger.Info($"MCP: removed {removed} entries from the target area");
            if (removed == 0) {
                return OperationResult.Ok("The target area was already empty.");
            }

            return OperationResult.Ok(
                $"Removed {removed} entr{(removed == 1 ? "y" : "ies")} from the target area. Its triggers and "
                + "conditions as well as the start and end areas are unchanged.");
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Clearing was cancelled; the sequence is unchanged.");
        } catch (Exception ex) {
            Logger.Error("MCP: clearing the targets failed", ex);
            return OperationResult.Fail($"Clearing the targets failed: {ex.Message}");
        } finally {
            editLock.Release();
        }
    }

    public async Task<OperationResult> SaveSequenceAsync(string name, bool overwrite, CancellationToken cancellationToken) {
        string? baseName = NormalizeSaveName(name, out string nameError);
        if (baseName == null) {
            return OperationResult.Fail(nameError);
        }

        try {
            await editLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Saving was cancelled; nothing was written.");
        }

        string? tempFile = null;
        try {
            SequencerAccess? access = TryGetAccess(out string error);
            if (access == null) {
                return OperationResult.Fail(error);
            }

            string? folder = SequenceFolder();
            if (folder == null) {
                return OperationResult.Fail("The active profile has no sequence folder configured.");
            }

            string file = Path.GetFullPath(Path.Combine(folder, baseName + ".json"));
            if (!IsInsideFolder(folder, file)) {
                return OperationResult.Fail("Invalid sequence name.");
            }

            bool exists = File.Exists(file);
            if (exists && !overwrite) {
                return OperationResult.Fail(
                    $"A sequence named '{baseName}' already exists. Choose another name or pass overwrite=true.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(folder);

            string json = await Task.Run(() => RunOnDispatcher(() => SerializeForSave(access, baseName)), CancellationToken.None)
                .ConfigureAwait(false);

            tempFile = Path.Combine(folder, $".{baseName}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, json, CancellationToken.None).ConfigureAwait(false);
            File.Move(tempFile, file, overwrite: true);
            tempFile = null;

            RunOnDispatcher(() => access.ViewModel.ClearHasChanged());

            Logger.Info($"MCP: saved the advanced sequence to {file}");
            return OperationResult.Ok(
                $"{(exists ? "Overwrote" : "Saved")} the sequence as '{baseName}' ({file}). Load it later with "
                + $"load_sequence name='{baseName}'.");
        } catch (OperationCanceledException) {
            return OperationResult.Fail("Saving was cancelled; nothing was written.");
        } catch (Exception ex) {
            Logger.Error($"MCP: saving the sequence as '{name}' failed", ex);
            return OperationResult.Fail($"Saving the sequence failed: {ex.Message}");
        } finally {
            DeleteQuietly(tempFile);
            editLock.Release();
        }
    }

    /// <summary>Runs on the dispatcher. Mirrors <c>Sequence2VM.LoadSequenceFromFile</c> without its notifications.</summary>
    private string LoadFromFile(SequencerAccess access, string file, string title) {
        // Like NINA: global user symbols belong to the old sequence and must go before the new one registers its own.
        UserSymbol.ClearUserSymbols();

        var converter = new SequenceJsonConverter(access.Factory);
        ISequenceContainer? container = converter.DeserializeFromFile(file);
        if (container == null) {
            throw new InvalidOperationException("The file does not contain a sequence.");
        }

        ISequenceRootContainer root;
        string kind;
        if (container is ISequenceRootContainer loadedRoot) {
            root = loadedRoot;
            root.SequenceTitle = title;
            kind = "sequence";
        } else {
            // Like Sequence2VM.LoadSequenceFromFile: a saved target or template goes into a new sequence's target area.
            root = WrapInNewRoot(access.Factory, container);
            kind = $"single {container.GetType().Name} (wrapped into a new sequence)";
        }

        sequenceMediator.SetAdvancedSequence(root);
        root.Validate();
        access.ViewModel.ClearHasChanged();

        int targets = CountTargets(root);
        return $"Loaded the {kind} '{title}' with {targets} target{(targets == 1 ? string.Empty : "s")}. "
            + "Inspect it with get_sequence.";
    }

    private static SequenceRootContainer WrapInNewRoot(ISequencerFactory factory, ISequenceContainer content) {
        SequenceRootContainer? root = factory.GetContainer<SequenceRootContainer>();
        StartAreaContainer? startArea = factory.GetContainer<StartAreaContainer>();
        TargetAreaContainer? targetArea = factory.GetContainer<TargetAreaContainer>();
        EndAreaContainer? endArea = factory.GetContainer<EndAreaContainer>();
        if (root == null || startArea == null || targetArea == null || endArea == null) {
            throw new InvalidOperationException("The sequencer factory does not provide the root and area containers.");
        }

        root.Name = Loc.Instance["LblAdvancedSequenceTitle"];
        root.Add(startArea);
        targetArea.Add(content);
        root.Add(targetArea);
        root.Add(endArea);
        return root;
    }

    private static int CountTargets(ISequenceRootContainer root) {
        try {
            return NINA.Sequencer.Utility.ItemUtility.LookForTargetsDownwards(root)?.Count ?? 0;
        } catch (Exception ex) {
            Logger.Debug($"MCP: counting targets failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Runs on the dispatcher. The same steps as SkyAtlasVM/FramingAssistantVM "add target from template".</summary>
    private OperationResult AddTargetFromTemplate(TargetRequest request) {
        IReadOnlyList<IDeepSkyObjectContainer> templates = GetTemplates();
        string wanted = request.TemplateName!.Trim();
        IDeepSkyObjectContainer? template = templates.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.Ordinal))
            ?? templates.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (template == null) {
            string available = templates.Count == 0
                ? "There are no target templates."
                : "Available templates: " + string.Join(", ", templates.Select(t => $"'{t.Name}'").Distinct()) + ".";
            return OperationResult.Fail($"No target template named '{request.TemplateName}'. {available}");
        }

        if (template.Clone() is not IDeepSkyObjectContainer container) {
            return OperationResult.Fail($"The template '{template.Name}' could not be copied.");
        }

        container.ResetAll();
        if (container is DeepSkyObjectContainer dso) {
            dso.ExposureInfoList.Clear();
        }

        ApplyTarget(container, request);
        sequenceMediator.AddAdvancedTarget(container);
        container.Validate();

        Logger.Info($"MCP: added target '{request.Name}' from template '{template.Name}'");
        return OperationResult.Ok(
            $"Added target '{request.Name}' from the template '{template.Name}' at the end of the target area "
            + $"(RA {request.RaHours:0.#####} h, Dec {request.DecDegrees:0.#####}°). The template's own instructions "
            + "are used; slew/guiding/autofocus/dither options of the request do not apply to templates."
            + FormatIssueHint(container));
    }

    private static void ApplyTarget(IDeepSkyObjectContainer container, TargetRequest request) {
        string name = request.Name.Trim();

        container.Name = name;
        container.Target.TargetName = name;
        container.Target.InputCoordinates.Coordinates = new NINA.Astrometry.Coordinates(
            NINA.Astrometry.Angle.ByHours(request.RaHours),
            NINA.Astrometry.Angle.ByDegree(request.DecDegrees),
            NINA.Astrometry.Epoch.J2000);
        if (request.RotationDegrees.HasValue) {
            container.Target.PositionAngle = request.RotationDegrees.Value;
        }

        // The target's CoordinatesChanged event normally propagates the new coordinates to the children
        // (Center inherits them). ninaAPI calls this explicitly as well; it is idempotent.
        container.AfterParentChanged();
    }

    private static ISequenceContainer? FindTargetArea(ISequenceRootContainer root) {
        ICollection<ISequenceItem> areas = root.GetItemsSnapshot();
        ISequenceContainer? targetArea = areas.OfType<TargetAreaContainer>().FirstOrDefault();
        if (targetArea != null) {
            return targetArea;
        }

        // Sequence2VM itself addresses the target area as Items[1].
        if (areas.Count == 3) {
            return areas.ElementAt(1) as ISequenceContainer;
        }

        return null;
    }

    private static int ClearContainerItems(ISequenceContainer container) {
        int removed = 0;
        foreach (ISequenceItem item in container.GetItemsSnapshot()) {
            item.Detach();
            removed++;
        }

        return removed;
    }

    /// <summary>Runs on the dispatcher. Like Save As in NINA, the sequence title becomes the file name.</summary>
    private static string SerializeForSave(SequencerAccess access, string baseName) {
        access.Root.SequenceTitle = baseName;

        var converter = new SequenceJsonConverter(access.Factory);
        return converter.Serialize(access.Root);
    }

    private string? ResolveSavedSequenceFile(string name, out string error) {
        error = string.Empty;

        string? folder = SequenceFolder();
        if (folder == null || !Directory.Exists(folder)) {
            error = "The sequence folder of the active profile does not exist.";
            return null;
        }

        string requested = (name ?? string.Empty).Trim().Replace('\\', '/');
        if (requested.Length == 0) {
            error = "No sequence name given.";
            return null;
        }

        bool unsafePath = Path.IsPathRooted(requested)
            || requested.Split('/').Any(segment => segment == ".." || segment == ".")
            || requested.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
        if (unsafePath) {
            error = "Invalid sequence name. Use a name from list_saved_sequences.";
            return null;
        }

        if (requested.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            requested = requested.Substring(0, requested.Length - ".json".Length);
        }

        string candidate = Path.GetFullPath(Path.Combine(folder, requested + ".json"));
        if (IsInsideFolder(folder, candidate) && File.Exists(candidate)) {
            return candidate;
        }

        SavedSequenceInfo? match = ListSavedSequences()
            .FirstOrDefault(info => string.Equals(info.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (match != null) {
            string matched = Path.GetFullPath(Path.Combine(folder, match.FileName));
            if (IsInsideFolder(folder, matched) && File.Exists(matched)) {
                return matched;
            }
        }

        error = $"No saved sequence named '{name}'. Use list_saved_sequences to see the available names.";
        return null;
    }

    private static string? NormalizeSaveName(string name, out string error) {
        error = string.Empty;

        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed.Substring(0, trimmed.Length - ".json".Length).TrimEnd();
        }

        if (trimmed.Length == 0) {
            error = "No sequence name given.";
            return null;
        }

        if (trimmed.Length > MaxSequenceNameLength) {
            error = $"The sequence name is longer than {MaxSequenceNameLength} characters.";
            return null;
        }

        bool validCharacters = trimmed.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.');
        if (!validCharacters) {
            error = "The sequence name may contain only letters, digits, spaces, '-', '_' and '.'.";
            return null;
        }

        if (trimmed.StartsWith('.') || trimmed.EndsWith('.')) {
            error = "The sequence name must not start or end with '.'.";
            return null;
        }

        if (trimmed.EndsWith(".template", StringComparison.OrdinalIgnoreCase)) {
            error = "Names ending in '.template' are reserved for templates.";
            return null;
        }

        return trimmed;
    }

    private static bool IsInsideFolder(string folder, string file) {
        string root = WithTrailingSeparator(Path.GetFullPath(folder));
        return Path.GetFullPath(file).StartsWith(root, StringComparison.Ordinal);
    }

    private static void DeleteQuietly(string? file) {
        if (file == null) {
            return;
        }

        try {
            File.Delete(file);
        } catch (Exception ex) {
            Logger.Warning($"MCP: could not delete temporary file {file}: {ex.Message}");
        }
    }

    private static string FormatIssueHint(ISequenceContainer container) {
        IReadOnlyList<string> issues = CollectValidationIssues(container);
        if (issues.Count == 0) {
            return string.Empty;
        }

        const int MaxIssues = 8;
        string list = string.Join("; ", issues.Take(MaxIssues));
        if (issues.Count > MaxIssues) {
            list += $"; and {issues.Count - MaxIssues} more";
        }

        return $" Current validation issues (fix before starting, e.g. connect equipment): {list}.";
    }
}
