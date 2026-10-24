// SPDX-License-Identifier: MPL-2.0

using System.Runtime.CompilerServices;

// Explicit because GenerateAssemblyInfo is off (CommonAssemblyInfo.cs is linked), which also drops <InternalsVisibleTo> items.
[assembly: InternalsVisibleTo("NINA.Mcp.Test")]
