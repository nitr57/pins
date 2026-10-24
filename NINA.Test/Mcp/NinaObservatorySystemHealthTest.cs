// SPDX-License-Identifier: MPL-2.0

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>get_system_health in the MCP adapter: external commands share one budget and never block.</summary>
    [TestFixture]
    public class NinaObservatorySystemHealthTest {

        [Test]
        public async Task Health_IsReadWithinTheCommandBudget() {
            var harness = new NinaObservatoryHarness();
            var watch = Stopwatch.StartNew();

            var health = await harness.Observatory.GetSystemHealthAsync(CancellationToken.None);

            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)), "two commands, one two-second budget");
            Assert.That(health.LocalTime, Is.EqualTo(DateTimeOffset.Now).Within(TimeSpan.FromMinutes(1)));
        }

        [Test]
        public async Task CancelledRequest_ReturnsAtOnce() {
            var harness = new NinaObservatoryHarness();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var watch = Stopwatch.StartNew();

            var health = await harness.Observatory.GetSystemHealthAsync(cancelled.Token);

            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(health.Throttling, Is.Null);
        }
    }
}
