<p align="center">
  <img src="src/RbgCanInsight.App/Assets/RbgCanInsight.png" alt="RBG CAN Insight official application icon" width="144" />
</p>

<h1 align="center">RBG CAN Insight</h1>

<p align="center">
  <strong>Explore recorded CAN and UDS traffic on your Windows PC.</strong><br />
  Decode signals, investigate diagnostics and review trace evidence locally.
</p>

<p align="center">
  <a href="#download-and-quick-start"><strong>Download</strong></a> ·
  <a href="docs/USER_GUIDE.md">User guide</a> ·
  <a href="https://github.com/Ruba94-cyber/RBG-CAN-Insight-Demo">Synthetic demo</a> ·
  <a href="#application-licensing">Licensing</a> ·
  <a href="#support-development">Donate</a>
</p>

<p align="center">
  <img alt="Windows desktop" src="https://img.shields.io/badge/Windows-desktop-263238?logo=windows&amp;logoColor=white" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&amp;logoColor=white" />
  <img alt="Offline analysis" src="https://img.shields.io/badge/Analysis-offline-24786E" />
</p>

> **Free for personal and non-commercial use.** Business and professional use of version 5.2.0 requires a separate paid commercial license. [See licensing](#application-licensing).

## What is RBG CAN Insight?

RBG CAN Insight is a Windows desktop application for investigating recorded CAN and UDS traffic. Open a recording, decode messages and signals using your own databases, inspect plots and diagnostic transactions, and export results. The application processes selected files locally. Its interface is in English.

It is an **offline analysis tool**: it does not connect to vehicle hardware, transmit CAN frames or perform ECU flashing. OEM databases and proprietary DBC files are not included. Compatibility and decoding depend on the supplied files; equivalence with Vector tools is not claimed.

| Workspace | What you can do |
| --- | --- |
| **Trace** | Open supported recordings, search and filter frames, and navigate timestamped traffic. |
| **DBC and signals** | Load databases, inspect decoded signals, plot timelines, and explore Change Matrix and Byte Matrix views. |
| **Diagnostics** | Reconstruct ISO-TP/UDS transactions and inspect recorded DIDs, DTCs, routines and related evidence. |
| **Correlation** | Review time-aligned DID–CAN candidates and signal relationships, with evidence and sampling context. |
| **Data Studio** | Save and reopen analysis recipes with source references and filters; original files remain necessary. |
| **Reports** | Export supported trace formats, tables and diagnostic reports. |

See the [user guide](docs/USER_GUIDE.md) for supported formats, feature details and limitations.

## Download and quick start

Download the MSI installer or portable ZIP from [GitHub Releases](https://github.com/Ruba94-cyber/RBG-CAN-Insight/releases/latest). Each release includes its version-specific terms and notes.

Once you have an authorized copy:

1. Verify the SHA-256 checksum provided with its release. The self-signed publisher certificate may cause a Windows or SmartScreen warning on a computer that does not trust it.
2. Install the MSI or extract the portable ZIP and start the application.
3. Use **Open trace...** to load a recording, then **Load DBC...** to load a matching database. Explore the Trace and Signals & Plot workspaces; open UDS Analysis for recorded diagnostic traffic.

For a non-sensitive example, use the [synthetic demo files](https://github.com/Ruba94-cyber/RBG-CAN-Insight-Demo) and follow the [user guide](docs/USER_GUIDE.md). The application does not require the demo files.

The [Microsoft Store listing](https://apps.microsoft.com/detail/9NTZXFCKR81D) is a separate distribution channel: check its displayed price, availability and applicable terms. Store and GitHub versions can be released on different dates.

## Application licensing

**Version 5.2.0 and later releases that explicitly adopt these terms:** personal, study, hobby and other non-commercial use is free. Business, employee, professional, consulting, laboratory and other work-related use requires a **separate paid commercial license**, even if the application was downloaded for free. Request terms and pricing at [paolo.ruba.1913@gmail.com](mailto:paolo.ruba.1913@gmail.com). Donations are optional and do not grant a commercial license.

**Existing release rights are preserved.** The previously published GitHub release v5.1.0 expressly permitted free business use. Its download has been withdrawn, but the conditions above do not retroactively remove permission already granted to recipients. Other versions and Store installations are governed by the terms applicable when they were acquired and by their own release and distribution terms.

The application binaries and source code have separate terms. Source-code reuse requires separate authorization; consult [LICENSE.md](LICENSE.md). See [APPLICATION-TERMS.md](APPLICATION-TERMS.md) for the application terms and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party components.

## Troubleshooting and support

- **No decoded signals?** Load a matching DBC and check that message IDs, channels and signal definitions correspond to the recording.
- **A saved analysis cannot reopen?** Keep the original source files available; changed or missing files can require confirmation or prevent restoration.
- **An import is incomplete or unsupported?** Check the format and limitations in the [user guide](docs/USER_GUIDE.md). Avoid sharing confidential vehicle or customer data when reporting an issue.
- **Need help?** Open an [issue](https://github.com/Ruba94-cyber/RBG-CAN-Insight/issues) with a reproducible description using synthetic data. See the [privacy policy](docs/PRIVACY_POLICY.md).

## Build and test

The solution targets Windows and .NET 10. Build from source with `dotnet build RbgCanInsight.sln -c Release`, and run unit tests with `dotnet test tests/RbgCanInsight.Tests/RbgCanInsight.Tests.csproj -c Release`. GUI tests require a Windows desktop session. Building, modifying or redistributing source code remains subject to [LICENSE.md](LICENSE.md); installer tooling has its own terms.

## Support development

If RBG CAN Insight helps you, you can [support development with a voluntary PayPal donation](https://www.paypal.com/donate?business=paolo.ruba.1913%40gmail.com&no_recurring=0&item_name=Support+RBG+CAN+Insight&currency_code=EUR). A donation does not purchase or replace a commercial license.

Created by Paolo Rubagotti.
