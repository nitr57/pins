// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Moq;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Mcp.Adapter;
using NINA.Mcp.Observatory;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>
    /// The MCP event bridge on mocked mediators. The bridge polls connections every three seconds, so each test waits one
    /// or two polls.
    /// </summary>
    [TestFixture]
    public class NinaEventBridgeTest {

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

        [Test]
        public async Task SafetyMonitorThatIsUnsafeWhenItConnects_IsPublished() {
            var info = new SafetyMonitorInfo { Connected = false, IsSafe = false };
            var safety = new Mock<ISafetyMonitorMediator>();
            safety.Setup(mediator => mediator.GetInfo()).Returns(() => info);

            var log = new ObservatoryEventLog();
            var bridge = Start(log, services => services.AddSingleton(safety.Object));

            try {
                // Let the bridge take the disconnected state first.
                await Task.Delay(500);

                // SafetyMonitorVM.Connect takes the unsafe state of the device without raising IsSafeChanged.
                info = new SafetyMonitorInfo { Connected = true, IsSafe = false };

                var unsafeEvent = await WaitForAsync(log, entry => entry.Kind == "safety");

                Assert.That(unsafeEvent.Severity, Is.EqualTo(EventSeverity.Critical));
                Assert.That(unsafeEvent.Message, Is.EqualTo("The safety monitor reports UNSAFE."));

                // The same state is not published twice; a real change is.
                safety.Raise(mediator => mediator.IsSafeChanged += null, safety.Object, new IsSafeEventArgs(false));
                info.IsSafe = true;
                safety.Raise(mediator => mediator.IsSafeChanged += null, safety.Object, new IsSafeEventArgs(true));

                var safetyEvents = log.Recent(50, entry => entry.Kind == "safety").Events;

                Assert.That(safetyEvents.Select(entry => entry.Message), Is.EqualTo(new[] {
                    "The safety monitor reports UNSAFE.", "The safety monitor reports safe."
                }));
            } finally {
                await bridge.StopAsync(CancellationToken.None);
            }
        }

        [Test]
        public async Task SafetyMonitorThatIsUnsafeWhenTheBridgeStarts_IsPublished() {
            var safety = new Mock<ISafetyMonitorMediator>();
            safety.Setup(mediator => mediator.GetInfo()).Returns(new SafetyMonitorInfo { Connected = true, IsSafe = false });

            var log = new ObservatoryEventLog();
            var bridge = Start(log, services => services.AddSingleton(safety.Object));

            try {
                var unsafeEvent = await WaitForAsync(log, entry => entry.Kind == "safety");

                Assert.That(unsafeEvent.Severity, Is.EqualTo(EventSeverity.Critical));
            } finally {
                await bridge.StopAsync(CancellationToken.None);
            }
        }

        internal static NinaEventBridge Start(ObservatoryEventLog log, Action<IServiceCollection> register) {
            var services = new ServiceCollection();
            register(services);

            var bridge = new NinaEventBridge(services.BuildServiceProvider(), log);
            bridge.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            return bridge;
        }

        internal static async Task<ObservatoryEvent> WaitForAsync(ObservatoryEventLog log, Func<ObservatoryEvent, bool> match) {
            var page = await log.WaitAsync(0, 50, match, Timeout, CancellationToken.None);

            Assert.That(page.Events, Is.Not.Empty, "no matching event was published");

            return page.Events[0];
        }
    }
}
