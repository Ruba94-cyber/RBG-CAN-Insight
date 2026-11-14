# Third-party dependencies and notices

This document identifies dependencies referenced by the source and the runtime found in the examined 5.1.0 Store candidate. Third-party components remain under their own licenses; the RBG CAN Insight license does not replace them. This inventory is based on NuGet package metadata and `dotnet list package --include-transitive` for the project targets; it is not a certification that every redistributed binary's notice requirements have been satisfied.

## Application

The packaging scripts now pin .NET runtime 10.0.15 and include the license and notice files under `third-party/dotnet-10.0.15`, copied from the corresponding `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64` NuGet packages. The WindowsDesktop package did not include a separate third-party notice file. Review the applicable distribution terms and notices before releasing each artifact.

The application project has no direct or transitive NuGet package references for `net10.0-windows` in the examined restore. It uses .NET and Windows Desktop/WPF. The examined self-contained Store candidate contains `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` version 10.0.10, per its runtime configuration.

Microsoft's [.NET license information](https://github.com/dotnet/core/blob/main/license-information.md) distinguishes source and package licensing from Windows product distributions; see the [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) and the third-party notices for the exact runtime and desktop distribution being shipped. Self-contained executables include runtime components. For each binary release, identify its actual runtime versions and include all applicable license texts and third-party notices with that distribution; this repository inventory alone does not establish binary compliance. Runtime notice example: [.NET Runtime third-party notices](https://github.com/dotnet/runtime/blob/main/src/installer/pkg/THIRD-PARTY-NOTICES.TXT).

The [`third-party/dotnet-10.0.10`](third-party/dotnet-10.0.10/) directory contains copies from the restored `Microsoft.NETCore.App.Runtime.win-x64` 10.0.10 NuGet package (its LICENSE.TXT and THIRD-PARTY-NOTICES.TXT) and the `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.10 package (its LICENSE file). Both package license files declare MIT. The WindowsDesktop runtime package did not contain a separate file named THIRD-PARTY-NOTICES; check the full applicable materials for each release and any dependencies before distribution. These files are references in this source tree, not evidence that an existing MSI, ZIP or Store package includes them.

## Test dependencies

The following packages are restored for unit or GUI tests. They are not direct application project dependencies. License labels below are the NuGet package's declared license metadata; links point to upstream project or license information. `LICENSE.txt` means the exact text is included in that NuGet package and should be consulted if that package is redistributed.

| Package | Version | Declared license |
|---|---:|---|
| [FlaUI.Core](https://github.com/FlaUI/FlaUI), [FlaUI.UIA3](https://github.com/FlaUI/FlaUI) | 4.0.0 | MIT, package LICENSE.txt |
| [Interop.UIAutomationClient](https://github.com/Roemer/UIAutomation-Interop) | 10.19041.0 | MIT, package LICENSE.txt |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest), Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost | 17.11.1 | MIT |
| [Microsoft.NETCore.Platforms](https://github.com/dotnet/runtime) | 5.0.1 | MIT |
| [Microsoft.Win32.SystemEvents](https://dot.net/), [System.Drawing.Common](https://github.com/dotnet/winforms) | 10.0.15 | MIT |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) | 13.0.1 | MIT |
| [System.CodeDom, System.Configuration.ConfigurationManager, System.Management, System.Security.Cryptography.ProtectedData, System.Security.Permissions, System.Windows.Extensions](https://github.com/dotnet/runtime) | 5.0.0 | MIT |
| [System.Diagnostics.PerformanceCounter](https://github.com/dotnet/runtime) | 5.0.1 | MIT |
| [xunit.abstractions](https://github.com/xunit/xunit) | 2.0.3 | [Upstream license URL declared by package](https://raw.githubusercontent.com/xunit/xunit/master/license.txt); verify version-specific text before redistribution |
| [xunit, xunit.assert, xunit.core, xunit.extensibility.core, xunit.extensibility.execution](https://github.com/xunit/xunit) | 2.9.2 | Apache-2.0 |
| [xunit.analyzers](https://github.com/xunit/xunit) | 1.16.0 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 2.8.2 | Apache-2.0 |

## Optional external tools

Some independent validation scripts optionally use [python-can](https://github.com/hardbyte/python-can) and [cantools](https://github.com/cantools/cantools), installed separately by the user; consult the licenses of the versions actually installed. The MSI packaging script uses the separately installed [WiX Toolset](https://wixtoolset.org/); consult its version-specific terms. These tools are not NuGet application dependencies. This inventory does not imply that their code or data is bundled in the application.

Regenerate and review this document when dependencies, target frameworks, packaging or runtime versions change.
