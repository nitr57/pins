#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media {
    /// <summary>
    /// Delegate for hit test result callbacks.
    /// </summary>
    public delegate HitTestResultBehavior HitTestResultCallback(HitTestResult result);

    /// <summary>
    /// Delegate for hit test filter callbacks.
    /// </summary>
    public delegate HitTestFilterBehavior HitTestFilterCallback(DependencyObject potentialHitTestTarget);

    /// <summary>
    /// Base class for hit test parameters.
    /// </summary>
    public abstract class HitTestParameters { }

    /// <summary>
    /// Parameters for point-based hit testing.
    /// </summary>
    public class PointHitTestParameters : HitTestParameters {
        public Point HitPoint { get; set; }

        public PointHitTestParameters(Point hitPoint) {
            HitPoint = hitPoint;
        }
    }
}
