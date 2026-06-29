# CleverPoint Migrator

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-SharePoint%20Online-0078D4?logo=microsoftsharepoint&logoColor=white)](https://www.microsoft.com/microsoft-365/sharepoint/collaboration)
[![Windows](https://img.shields.io/badge/Windows-Supported-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Linux](https://img.shields.io/badge/Linux-Not%20tested%20yet-333333?logo=linux&logoColor=white)](#)
[![macOS](https://img.shields.io/badge/macOS-Not%20tested%20yet-999999?logo=apple&logoColor=white)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

CleverPoint Migrator is a tool that can be used by office workers and power users to migrate large amounts of files between SharePoint Online sites. No Azure app registration is required, and the tool can be used by anyone with access to the source and destination sites. The tool is designed to be easy to use, with a simple user interface that allows users to select the source and destination sites, as well as the files to be migrated.

![CleverPoint Migrator](/IMG/SharePoint_Online_Migrator.png)

![Copy Demo](/IMG/CopyDemo.gif)


## Download and run

- Navigate to https://github.com/Zerg00s/CleverPoint-Migrator/releases/latest
- download `CleverPoint.Migrator-win-x64-*.zip`
- Unzip the archive
- Run `CleverPoint.Migrator.Ux.exe`

![Run executable](/IMG/RunExe.png)

## Notes for Administrators

### Local Dev

```powershell
dotnet run --project src\CleverPoint.Migrator.Ux
```

### Sign and publishing the release

We properly sign each release with a certificate. To do this, you need to run the following command in PowerShell:

```powershell
.\Sign-App.ps1 -Version 1.0.2 -Release
```
