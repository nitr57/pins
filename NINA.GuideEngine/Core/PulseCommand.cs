// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>A guide pulse to be issued on an <see cref="IPulseOutput"/>.</summary>
public readonly record struct PulseCommand(GuideDirection Direction, int DurationMs);
