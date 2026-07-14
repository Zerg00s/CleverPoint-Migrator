# CleverPoint Migrator

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-SharePoint%20Online-0078D4?logo=microsoftsharepoint&logoColor=white)](https://www.microsoft.com/microsoft-365/sharepoint/collaboration)
[![Windows](https://img.shields.io/badge/Windows-Supported-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Linux](https://img.shields.io/badge/Linux-Not%20tested%20yet-333333?logo=linux&logoColor=white)](#)
[![macOS](https://img.shields.io/badge/macOS-Not%20tested%20yet-999999?logo=apple&logoColor=white)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

CleverPoint Migrator is a tool that can be used by office workers and power users to migrate large amounts of files between SharePoint Online sites. No Azure app registration is required, and the tool can be used by anyone with access to the source and destination sites. The tool is designed to be easy to use, with a simple user interface that allows users to select the source and destination sites, as well as the files to be migrated.

![CleverPoint Migrator](/IMG/SharePoint_Online_Migrator.png)

See the animation below for a quick demo of how to use the tool.

![Copy Demo](/IMG/CopyDemo.gif)

## Download and run

- Navigate to https://github.com/Zerg00s/CleverPoint-Migrator/releases/latest
- download and install `CleverPoint.Migrator-*.msi`
- Run `CleverPoint Migrator`

### Portable version

- download and unzip `CleverPoint.Migrator-win-x64-*.zip`
- Run `CleverPoint.Migrator.Ux.exe`

![Run executable](/IMG/RunExe.png)

## Frequently Asked Questions

**Is it secure? Where does my data go?**

Everything runs locally on your computer. Nothing is sent to any third-party companies.

**Does it work across tenants?**

Yes. Tenant-to-tenant migrations work, including managed metadata as of release 1.0.12.

**How many files can it handle?**

There is no built-in restriction. 100K+ files should work just fine.

**What about throttling?**

Throttling won't interrupt the copy. When Microsoft throttles a request, it sends back the recommended wait time, and the tool waits and retries.

**What if the migration gets interrupted?**

Re-run it. The tool also compares file sizes to detect corruption and automatically re-migrates any file that fails the check. You can download a full migration report listing every file, so everything is accounted for.

**Does it migrate permissions?**

It migrates file and folder permissions, but not site owners or members.

**Does it support site pages?**

Yes, it does.

**Does it use PnP under the hood?**

No, but it uses the same API as PnP.

**How much does it cost?**
It's free. If someone needs additional support, I am happy to provide that.

## What It Is Not

This tool is for moving lists, libraries, folders, and files between SharePoint Online and OneDrive sites. It does not migrate entire sites, and it does not support SharePoint Server, file shares, Google, Exchange, or Teams. For file share migrations, use the free Microsoft SharePoint Migration Tool (SPMT). For full-scale tenant migrations with M365 groups, Teams, and mailboxes, I still use ShareGate.

CleverPoint Migrator is not trying to replace ShareGate, AvePoint, or SPMT. It fills the gap in between: the simple, common case of moving content around SharePoint without buying anything.

## Notes for Contributors

### Local Dev

```powershell
dotnet run --project src\CleverPoint.Migrator.Ux
```

### Sign and publishing the release

We properly sign each release with a certificate. To do this, you need to run the following command in PowerShell:

```powershell
.\Sign-App.ps1 -Version 1.0.12 -Release
```
