#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.CompilerServices;

// Upstream N.I.N.A. declares this in NINA.Core/Properties/AssemblyInfo.cs.
// The pins fork dropped that file; NINA.Astrometry needs the internal
// CustomHorizon search helpers used by TargetCrossingCalculator.
[assembly: InternalsVisibleTo("NINA.Astrometry")]
