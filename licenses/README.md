# Dependencies

| Component | Version | License |
|---|---|---|
| [NAudio.Core and NAudio.Wasapi](https://github.com/naudio/NAudio) | 3.1.0 | [MIT](NAudio-MIT.txt) |
| [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) | 2.1.0 | [MIT](NAudio.Lame-MIT.txt) for the wrapper |
| [LAME](https://lame.sourceforge.io/) | Bundled DLL reports 3.100-CVS | [LGPL-2.0-or-later](LAME-COPYING.txt) |
| System.Numerics.Tensors | 9.0.0 | [MIT](dotnet-MIT.txt) |
| .NET and ASP.NET Core | 10 | [Microsoft license](dotnet-MIT.txt), [ASP.NET Core license](aspnetcore-MIT.txt) and their bundled notices |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | [Windows SDK license](https://aka.ms/WinSDKLicenseURL) |
| [node-raumkernel](https://github.com/ChriD/node-raumkernel) | Protocol adapted from 1.2.22 | [MIT](node-raumkernel-MIT.txt) |
| [PSScriptAnalyzer](https://github.com/PowerShell/PSScriptAnalyzer) | 1.24.0, development only | [MIT](PSScriptAnalyzer-MIT.txt) |

SWYH and swyh-rs were references. No source from either project or node-raumserver
is included.

The native LAME DLL has a separate license from its managed wrapper. Keep it
replaceable and include the license texts when distributing the app. Distribute
the corresponding LAME source and build scripts, or a valid source offer under
the LGPL. The NuGet package does not identify the exact source and build recipe
for its 3.100-CVS binary. Obtain those from the maintainer or replace the DLL with
a reproducible build before publishing a release.
