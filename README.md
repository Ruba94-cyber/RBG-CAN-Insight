# RBG CAN Insight

RBG CAN Insight is a Windows desktop application for offline analysis of recorded CAN and UDS traffic. It imports supported trace formats, decodes DBC signals, reconstructs ISO-TP/UDS transactions, plots signals, and supports local analysis and reporting. It does not connect to live vehicle hardware or transmit CAN frames.

The application interface is in English. See the [user guide](docs/USER_GUIDE.md) and [privacy policy](docs/PRIVACY_POLICY.md).

## Distribution and support

The application is free to use, including for commercial use. A paid edition is available through the [Microsoft Store](https://apps.microsoft.com/detail/9NTZXFCKR81D). GitHub builds will be provided through this repository's releases when available; the two channels may have different release dates.

If you would like to support development, you can [donate via PayPal](https://www.paypal.com/donate?business=paolo.ruba.1913%40gmail.com&no_recurring=0&item_name=Support+RBG+CAN+Insight&currency_code=EUR).

## Source-code licensing

Commercial use of the source code requires a separate paid agreement or license. Consult the license file for the applicable source-code terms, once provided. This is separate from permission to use the application itself.

## Build and test

The solution targets Windows and .NET 10. Build with `dotnet build RbgCanInsight.sln -c Release`. Run the unit tests with `dotnet test tests/RbgCanInsight.Tests/RbgCanInsight.Tests.csproj -c Release`. GUI tests require a Windows desktop session. Installer generation may require additional tooling and its applicable license terms.

Created by Paolo Rubagotti.
