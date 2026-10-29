#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Reflection;
using System.Runtime.CompilerServices;

// 4.1.0.0 > the System.Windows type-forwarder facade in Microsoft.NETCore.App (4.0.0.0),
// so MSBuild conflict resolution and the framework-dependent host (dotnet test) pick this
// assembly instead of the facade, which contains none of the compat types.
[assembly: AssemblyVersion("4.1.0.0")]
[assembly: AssemblyFileVersion("4.1.0.0")]
[assembly: AssemblyTitle("System.Windows")]
[assembly: AssemblyDescription("Headless WPF compatibility layer")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("System.Windows")]
[assembly: AssemblyCopyright("")]

// Note: The public key token b03f5f7f11d50a3a belongs to Microsoft's WPF assemblies.
// We're creating a stub assembly that mimics the same identity for compatibility.
// This is for internal use only in a controlled environment.
