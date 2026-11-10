#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyRotator;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyRotator {

    /// <summary>
    /// The Wanderer rotator's end check: how far the SDK's end position (0..360) is from start + the relative move,
    /// across 0/360 and for moves either way.
    /// </summary>
    [TestFixture]
    public class WandererRotatorEndOffsetTest {

        [TestCase(10, 20, 30, 0)]
        [TestCase(359, 2, 1, 0)]
        [TestCase(1, -2, 359, 0)]
        [TestCase(0, 359.5, 359.5, 0)]
        [TestCase(180, -180, 0, 0)]
        [TestCase(350, 20, 10.5, 0.5)]
        [TestCase(10, 90, 60, 40)]
        [TestCase(0, 180, 0, 180)]
        [TestCase(5, -10, 356, 1)]
        public void EndOffset_IsTheShortestDistanceToTheTarget(double start, double angle, double end, double expected) {
            Assert.That(WandererRotator.EndOffset(start, angle, end), Is.EqualTo(expected).Within(1e-9));
        }
    }
}
