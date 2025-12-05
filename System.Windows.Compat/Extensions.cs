#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;

namespace System.Windows {
    /// <summary>
    /// Extension methods for DependencyObject and FrameworkElement
    /// </summary>
    public static class DependencyObjectExtensions {
        /// <summary>
        /// Gets the element itself and all ancestors in the visual tree.
        /// </summary>
        public static IEnumerable<DependencyObject> GetSelfAndAncestors(this DependencyObject obj) {
            var current = obj;
            while (current != null) {
                yield return current;
                current = Media.VisualTreeHelper.GetParent(current);
            }
        }
    }
}
