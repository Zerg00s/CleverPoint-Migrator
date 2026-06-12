# Third-party notices

CleverPoint Migrator uses the following third-party components:

| Component | License | Use |
|---|---|---|
| Microsoft.SharePointOnline.CSOM | Microsoft Software License Terms | SharePoint client object model |
| Microsoft.Data.Sqlite (incl. SQLitePCLraw / SQLite) | MIT / Public Domain | Migration history storage |
| Microsoft.Web.WebView2 | Microsoft Software License Terms (BSD-style for the SDK) | Browser-based sign-in |
| System.Security.Cryptography.ProtectedData | MIT (.NET Foundation) | DPAPI protection of stored secrets |

Sign-in to Microsoft 365 uses the publicly documented "Microsoft Graph
Command Line Tools" first-party client id for the OAuth authorization-code
flow; no Microsoft code is redistributed for it.

The user interface design is original work. Workflow concepts common to
migration tooling (source/target selection, copy sessions, migration
reports) are generic industry patterns.
