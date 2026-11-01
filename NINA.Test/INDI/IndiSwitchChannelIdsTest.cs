#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MySwitch;
using NINA.INDI;
using NUnit.Framework;
using System.Linq;

namespace NINA.Test.INDI {

    /// <summary>
    /// A Set Switch Value instruction saves an INDI switch hub channel by its id, so the id has to
    /// stay the same from session to session, whatever order the driver's properties arrive in.
    /// </summary>
    [TestFixture]
    public class IndiSwitchChannelIdsTest {

        [Test]
        public void PreferredId_IsFixed() {
            // FNV-1a of "POWER_CHANNELS\0POWER_CHANNEL_1" mapped onto 1000..32767. Changing the
            // function breaks every saved instruction, so this pins it.
            Assert.That(IndiSwitchChannelIds.PreferredId("POWER_CHANNELS", "POWER_CHANNEL_1"), Is.EqualTo(25246));
            Assert.That(IndiSwitchChannelIds.PreferredId("POWER_CHANNELS", "POWER_CHANNEL_2"), Is.EqualTo(21131));
            Assert.That(IndiSwitchChannelIds.PreferredId("DEW_PWM", "DEW_A"), Is.EqualTo(2472));
        }

        [Test]
        public void Assign_GivesEachChannelTheSameId_WhateverTheOrder() {
            var descriptors = Descriptors();

            var ids = IndiSwitchChannelIds.Assign(descriptors);
            var reversed = descriptors.AsEnumerable().Reverse().ToArray();
            var reversedIds = IndiSwitchChannelIds.Assign(reversed);

            for (var i = 0; i < descriptors.Length; i++) {
                var j = reversed.Length - 1 - i;
                Assert.That(reversedIds[j], Is.EqualTo(ids[i]), $"{descriptors[i].PropertyName}.{descriptors[i].ElementName}");
            }
        }

        [Test]
        public void Assign_KeepsAChannelsId_WhenOtherChannelsAreMissing() {
            var descriptors = Descriptors();

            var ids = IndiSwitchChannelIds.Assign(descriptors);
            var lastOnly = IndiSwitchChannelIds.Assign(new[] { descriptors[^1] });

            Assert.That(lastOnly[0], Is.EqualTo(ids[^1]));
        }

        [Test]
        public void Assign_GivesDistinctIds_AboveThePositionalOnes() {
            var descriptors = Enumerable.Range(1, 200)
                .Select(i => Descriptor("POWER_CHANNELS", $"POWER_CHANNEL_{i}"))
                .ToArray();

            var ids = IndiSwitchChannelIds.Assign(descriptors);

            Assert.That(ids, Is.Unique);
            Assert.That(ids, Has.All.GreaterThanOrEqualTo(IndiSwitchChannelIds.MinId));
        }

        private static INDISwitchDescriptor[] Descriptors() => new[] {
            Descriptor("POWER_CHANNELS", "POWER_CHANNEL_1"),
            Descriptor("POWER_CHANNELS", "POWER_CHANNEL_2"),
            Descriptor("USB_PORTS", "PORT_1"),
            Descriptor("DEW_PWM", "DEW_A"),
            Descriptor("DEW_PWM", "DEW_B"),
            Descriptor("POWER_SENSORS", "SENSOR_VOLTAGE"),
        };

        private static INDISwitchDescriptor Descriptor(string property, string element) => new() {
            PropertyName = property,
            ElementName = element,
        };
    }
}
