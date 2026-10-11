// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Test.Fakes;
using NUnit.Framework;

namespace NINA.Mcp.Test;

[TestFixture]
public class JobManagerTests {

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private ProtocolManualTimeProvider time = null!;
    private JobManager jobs = null!;

    [SetUp]
    public void SetUp() {
        time = new ProtocolManualTimeProvider();
        jobs = new JobManager(time);
    }

    [TearDown]
    public void TearDown() {
        jobs.CancelAll();
    }

    [Test]
    public async Task Start_ReturnsRunningJobWithReadableId() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = jobs.Start("slew", "Slew to M42", "mount", _ => gate.Task);

        Assert.That(job.Id, Does.Match("^job-[0-9a-f]{6}$"));
        Assert.That(job.State, Is.EqualTo(JobState.Running));
        Assert.That(job.Kind, Is.EqualTo("slew"));
        Assert.That(job.Description, Is.EqualTo("Slew to M42"));
        Assert.That(job.Resource, Is.EqualTo("mount"));
        Assert.That(job.StartedAt, Is.EqualTo(time.GetUtcNow()));
        Assert.That(job.FinishedAt, Is.Null);
        Assert.That(job.Outcome, Is.Null);

        gate.SetResult(JobOutcome.Ok("done"));

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Succeeded));
        Assert.That(finished.Outcome!.Message, Is.EqualTo("done"));
        Assert.That(finished.FinishedAt, Is.Not.Null);
    }

    [Test]
    public void Start_IdsAreUnique() {
        var ids = Enumerable.Range(0, 40)
            .Select(_ => jobs.Start("k", "d", null, _ => Task.FromResult(JobOutcome.Ok())).Id)
            .ToList();

        Assert.That(ids, Is.Unique);
    }

    [Test]
    public void Start_SameResourceWhileRunning_ThrowsConflict() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Start("slew", "Slew to M42", "mount", _ => gate.Task);

        var ex = Assert.Throws<JobConflictException>(() => jobs.Start("park", "Park", "Mount", _ => Task.FromResult(JobOutcome.Ok())));

        Assert.That(ex!.Resource, Is.EqualTo("Mount"));
        Assert.That(ex.RunningJob.Id, Is.EqualTo(first.Id));
        Assert.That(ex.Message, Does.Contain(first.Id).And.Contain("cancel_job"));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public void Start_DifferentResourcesOrNoResource_DoNotConflict() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        jobs.Start("slew", "Slew", "mount", _ => gate.Task);

        Assert.DoesNotThrow(() => jobs.Start("capture", "Capture", "camera", _ => gate.Task));
        Assert.DoesNotThrow(() => jobs.Start("a", "No resource", null, _ => gate.Task));
        Assert.DoesNotThrow(() => jobs.Start("b", "No resource", null, _ => gate.Task));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task Start_ResourceFreeAgainAfterJobFinished() {
        var job = jobs.Start("slew", "Slew", "mount", _ => Task.FromResult(JobOutcome.Ok()));

        await WaitFinishedAsync(job.Id);

        Assert.DoesNotThrow(() => jobs.Start("park", "Park", "mount", _ => Task.FromResult(JobOutcome.Ok())));
    }

    [Test]
    public async Task Start_ConcurrentClaimsOfOneResource_OnlyOneWins() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var successes = 0;
        var conflicts = 0;

        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() => {
            try {
                jobs.Start("slew", "Slew", "mount", _ => gate.Task);
                Interlocked.Increment(ref successes);
            } catch (JobConflictException) {
                Interlocked.Increment(ref conflicts);
            }
        }));

        await Task.WhenAll(attempts);

        Assert.That(successes, Is.EqualTo(1));
        Assert.That(conflicts, Is.EqualTo(15));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task Work_Throws_JobFailedWithMessage() {
        var job = jobs.Start("focus", "Autofocus", "camera", _ => throw new InvalidOperationException("Star detection failed"));

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Failed));
        Assert.That(finished.Outcome!.Success, Is.False);
        Assert.That(finished.Outcome.Message, Is.EqualTo("Star detection failed"));
    }

    [Test]
    public async Task Work_ThrowsAsynchronously_JobFailed() {
        var job = jobs.Start("focus", "Autofocus", null, async _ => {
            await Task.Yield();
            throw new InvalidOperationException("late failure");
        });

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Failed));
        Assert.That(finished.Outcome!.Message, Is.EqualTo("late failure"));
    }

    [Test]
    public async Task Work_ReturnsFailOutcome_JobFailedWithOutcome() {
        var job = jobs.Start("connect", "Connect camera", "camera", _ => Task.FromResult(JobOutcome.Fail("No camera selected", new { code = 7 })));

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Failed));
        Assert.That(finished.Outcome!.Message, Is.EqualTo("No camera selected"));
        Assert.That(finished.Outcome.Data, Is.Not.Null);
    }

    [Test]
    public async Task Work_OperationCanceledWithoutCancelRequest_IsFailure() {
        var job = jobs.Start("capture", "Capture", null, _ => throw new OperationCanceledException("driver timeout"));

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Failed));
        Assert.That(finished.Outcome!.Message, Is.EqualTo("driver timeout"));
    }

    [Test]
    public async Task Cancel_RunningJob_CancelsTokenAndEndsCancelled() {
        var job = jobs.Start("slew", "Slew", "mount", async token => {
            await Task.Delay(Timeout.Infinite, token);
            return JobOutcome.Ok();
        });

        Assert.That(jobs.Cancel(job.Id), Is.True);

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Cancelled));
        Assert.That(finished.Outcome!.Success, Is.False);
        Assert.That(finished.Outcome.Message, Does.Contain("cancelled"));
    }

    [Test]
    public async Task Cancel_WorkReturnsFailAfterCancel_IsCancelled() {
        var job = jobs.Start("capture", "Capture", "camera", async token => {
            try {
                await Task.Delay(Timeout.Infinite, token);
            } catch (OperationCanceledException) {
                return JobOutcome.Fail("Exposure aborted");
            }

            return JobOutcome.Ok();
        });

        jobs.Cancel(job.Id);

        var finished = await WaitFinishedAsync(job.Id);

        Assert.That(finished.State, Is.EqualTo(JobState.Cancelled));
        Assert.That(finished.Outcome!.Message, Is.EqualTo("Exposure aborted"));
    }

    [Test]
    public async Task Cancel_UnknownOrFinished_ReturnsFalse() {
        Assert.That(jobs.Cancel("job-000000"), Is.False);

        var job = jobs.Start("k", "d", null, _ => Task.FromResult(JobOutcome.Ok()));

        await WaitFinishedAsync(job.Id);

        Assert.That(jobs.Cancel(job.Id), Is.False);
    }

    [Test]
    public async Task CancelAll_CancelsEveryRunningJob() {
        Func<CancellationToken, Task<JobOutcome>> forever = async token => {
            await Task.Delay(Timeout.Infinite, token);
            return JobOutcome.Ok();
        };

        var done = jobs.Start("k", "done", null, _ => Task.FromResult(JobOutcome.Ok()));
        await WaitFinishedAsync(done.Id);

        var a = jobs.Start("slew", "Slew", "mount", forever);
        var b = jobs.Start("capture", "Capture", "camera", forever);

        Assert.That(jobs.CancelAll(), Is.EqualTo(2));

        Assert.That((await WaitFinishedAsync(a.Id)).State, Is.EqualTo(JobState.Cancelled));
        Assert.That((await WaitFinishedAsync(b.Id)).State, Is.EqualTo(JobState.Cancelled));
        Assert.That(jobs.Get(done.Id)!.State, Is.EqualTo(JobState.Succeeded));
        Assert.That(jobs.CancelAll(), Is.EqualTo(0));
    }

    [Test]
    public async Task StartAndWait_FinishesWithinWait_ReturnsFinalState() {
        var job = await jobs.StartAndWaitAsync(
            "move",
            "Move focuser",
            "focuser",
            _ => Task.FromResult(JobOutcome.Ok("at 1200")),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.That(job.State, Is.EqualTo(JobState.Succeeded));
        Assert.That(job.Outcome!.Message, Is.EqualTo("at 1200"));
    }

    [Test]
    public async Task StartAndWait_ZeroWait_ReturnsImmediatelyWhileRunning() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = await jobs.StartAndWaitAsync("slew", "Slew", "mount", _ => gate.Task, TimeSpan.Zero, CancellationToken.None);

        Assert.That(job.State, Is.EqualTo(JobState.Running));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task StartAndWait_TimeoutElapses_ReturnsRunningAndJobContinues() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var waiting = jobs.StartAndWaitAsync("slew", "Slew", "mount", _ => gate.Task, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.That(waiting.IsCompleted, Is.False);

        time.Advance(TimeSpan.FromSeconds(31));

        var job = await waiting.WaitAsync(TestTimeout);

        Assert.That(job.State, Is.EqualTo(JobState.Running));

        gate.SetResult(JobOutcome.Ok());

        Assert.That((await WaitFinishedAsync(job.Id)).State, Is.EqualTo(JobState.Succeeded));
    }

    [Test]
    public async Task StartAndWait_RequestCancelled_StopsWaitingButNotTheJob() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken jobToken = default;
        using var request = new CancellationTokenSource();

        var waiting = jobs.StartAndWaitAsync(
            "slew",
            "Slew",
            "mount",
            token => {
                jobToken = token;
                return gate.Task;
            },
            TimeSpan.FromMinutes(2),
            request.Token);

        request.Cancel();

        var job = await waiting.WaitAsync(TestTimeout);

        Assert.That(job.State, Is.EqualTo(JobState.Running));
        Assert.That(jobToken.IsCancellationRequested, Is.False);

        gate.SetResult(JobOutcome.Ok());

        Assert.That((await WaitFinishedAsync(job.Id)).State, Is.EqualTo(JobState.Succeeded));
    }

    [Test]
    public void StartAndWait_Conflict_Throws() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Start("slew", "Slew", "mount", _ => gate.Task);

        Assert.ThrowsAsync<JobConflictException>(() => jobs.StartAndWaitAsync(
            "park",
            "Park",
            "mount",
            _ => Task.FromResult(JobOutcome.Ok()),
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task WaitAsync_UnknownId_ReturnsNull() {
        var job = await jobs.WaitAsync("job-ffffff", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.That(job, Is.Null);
    }

    [Test]
    public async Task WaitAsync_JobFinishesDuringWait_ReturnsFinalState() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = jobs.Start("slew", "Slew", "mount", _ => gate.Task);

        var waiting = jobs.WaitAsync(started.Id, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.That(waiting.IsCompleted, Is.False);

        gate.SetResult(JobOutcome.Ok("arrived"));

        var job = await waiting.WaitAsync(TestTimeout);

        Assert.That(job!.State, Is.EqualTo(JobState.Succeeded));
        Assert.That(job.Outcome!.Message, Is.EqualTo("arrived"));
    }

    [Test]
    public async Task WaitAsync_ZeroWait_ReturnsCurrentState() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = jobs.Start("slew", "Slew", "mount", _ => gate.Task);

        var job = await jobs.WaitAsync(started.Id, TimeSpan.Zero, CancellationToken.None);

        Assert.That(job!.State, Is.EqualTo(JobState.Running));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task List_RunningFirstThenFinishedNewestFirst() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var finishedOld = jobs.Start("k", "old", null, _ => Task.FromResult(JobOutcome.Ok()));
        await WaitFinishedAsync(finishedOld.Id);
        time.Advance(TimeSpan.FromMinutes(1));

        var finishedNew = jobs.Start("k", "new", null, _ => Task.FromResult(JobOutcome.Ok()));
        await WaitFinishedAsync(finishedNew.Id);
        time.Advance(TimeSpan.FromMinutes(1));

        var runningOld = jobs.Start("k", "running old", null, _ => gate.Task);
        time.Advance(TimeSpan.FromMinutes(1));
        var runningNew = jobs.Start("k", "running new", null, _ => gate.Task);

        var ids = jobs.List().Select(job => job.Id).ToList();

        Assert.That(ids, Is.EqualTo(new[] { runningNew.Id, runningOld.Id, finishedNew.Id, finishedOld.Id }));

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task FinishedJobs_ExpireAfterOneHour() {
        var job = jobs.Start("k", "d", null, _ => Task.FromResult(JobOutcome.Ok()));
        await WaitFinishedAsync(job.Id);

        time.Advance(TimeSpan.FromMinutes(59));

        Assert.That(jobs.Get(job.Id), Is.Not.Null);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.That(jobs.Get(job.Id), Is.Null);
        Assert.That(jobs.List(), Is.Empty);
    }

    [Test]
    public async Task RunningJobs_NeverExpire() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Start("cool", "Cool camera", "camera", _ => gate.Task);

        time.Advance(TimeSpan.FromHours(5));

        Assert.That(jobs.Get(job.Id)!.State, Is.EqualTo(JobState.Running));

        gate.SetResult(JobOutcome.Ok());

        await WaitFinishedAsync(job.Id);
    }

    [Test]
    public async Task FinishedJobs_CappedAtFiftyNewest() {
        var ids = new List<string>();

        for (var i = 0; i < JobManager.MaxFinishedJobs + 5; i++) {
            var job = jobs.Start("k", $"job {i}", null, _ => Task.FromResult(JobOutcome.Ok()));
            await WaitFinishedAsync(job.Id);
            ids.Add(job.Id);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var listed = jobs.List().Select(job => job.Id).ToList();

        Assert.That(listed, Has.Count.EqualTo(JobManager.MaxFinishedJobs));
        Assert.That(listed, Is.EquivalentTo(ids.Skip(5)));
        Assert.That(jobs.Get(ids[0]), Is.Null);
    }

    private async Task<JobInfo> WaitFinishedAsync(string id) {
        var deadline = DateTime.UtcNow + TestTimeout;

        while (DateTime.UtcNow < deadline) {
            var job = jobs.Get(id);

            if (job is not null && job.State != JobState.Running) {
                return job;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Job {id} did not finish within {TestTimeout}.");

        throw new InvalidOperationException("Unreachable.");
    }
}
