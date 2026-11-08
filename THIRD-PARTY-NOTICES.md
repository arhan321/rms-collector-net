# Third party files

`sdk/dotnet/YMConnect_CS.dll` and `sdk/dotnet/YMConnect.dll` are the unmodified
Windows .NET release files published by Yaskawa in YMConnect version 1.1.10.

Official distribution:
https://github.com/Yaskawa-Global/YMConnect/releases/download/v1.1.10/YMConnectDotNet_1_1_10_Windows.zip

Official archive SHA256:
`0297438252d903e3c02c9a469543be70181c69db119794dce12468caefdd8d5e`

The license is copied verbatim from the official YMConnect repository to
`sdk/dotnet/LICENSE`, and included in publish output as `licenses/YMConnect-LICENSE`.
https://github.com/Yaskawa-Global/YMConnect/blob/main/LICENSE

The self-contained package also contains the Microsoft .NET runtime and its
supplied license and third party notices in `licenses/dotnet`. The publish target
copies these notices from the actual .NET runtime pack. Preserve those files when distributing
or updating the published folder. The Visual C++ Redistributable installer is
obtained separately from Microsoft and is not bundled in this repository.
