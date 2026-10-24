# Claude Status

A small Windows 10/11 utility that shows your **Claude plan usage** on the taskbar and in the
system tray: the 5-hour session limit and the 7-day weekly limit, with time to reset.
It is built for people who use **Claude Desktop / claude.ai only**; it needs neither Claude Code
nor an API key.

> Unofficial and not affiliated with Anthropic. It reads the same numbers as
> claude.ai → Settings → Usage, using undocumented endpoints that may change.

## What it looks like

Real screenshots from the CI smoke test, captured on hosted Windows Server 2025 (Windows 11
shell) and Windows Server 2022 (Windows 10 shell) at 1920×1080 with demo data. The "Test Mode"
watermark comes from the CI host.

### Windows 11

Two rows on the taskbar (default):

![Windows 11 – bars](docs/images/win11-bars.png)

Compact, one line:

![Windows 11 – compact](docs/images/win11-compact.png)

Not signed in yet:

![Windows 11 – signed out](docs/images/win11-signed-out.png)

Details flyout (click the widget or a tray icon):

![Windows 11 – details](docs/images/win11-flyout.png)

### Windows 10

![Windows 10 – bars](docs/images/win10-bars.png)

![Windows 10 – compact](docs/images/win10-compact.png)

![Windows 10 – signed out](docs/images/win10-signed-out.png)

![Windows 10 – details](docs/images/win10-flyout.png)

### Tray icons and notifications (rendered previews)

Hosted CI machines keep new tray icons in the overflow area and suppress notifications, so these
two are previews of the design rather than screenshots. The filled tile is the 5h session and the
outlined tile is the 7d limit:

![Tray icons](docs/images/win11-icons.png)

![Limit notification](docs/images/win11-notification.png)

## Features

- **Taskbar widget** next to the notification area, in one of three styles: two rows with bars,
  one compact line, or tray icons only. It **never covers taskbar icons**: it appears only when it
  fits in the free space between the last taskbar button and the tray, and otherwise stays hidden
  (the tray icons remain). It is also hidden on vertical taskbars. You can drag it sideways within
  that free space. It hides when the taskbar
  auto-hides or a full-screen app is in front, and follows the light/dark Windows mode.
- **Choose what is shown** under **Display** in the menu: the taskbar widget, the 5-hour tray icon
  and the 7-day tray icon can each be turned on or off, but at least one always stays on (the last
  one is greyed out). If only the widget is on and the taskbar has no room for it, the 5-hour tray
  icon stands in until it fits again. Limit notifications appear either way.
- **Two tray icons**, one for 5h and one for 7d. Each shows the percentage and is coloured by
  level: normal, amber from 70%, red from 90%.
- **Details flyout** with every limit the account has (session, weekly, Opus/Sonnet weekly,
  extra usage) and the local reset times.
- **Windows notifications**, each sent once per limit window:

  | Limit        | Notified at        |
  |--------------|--------------------|
  | 5-hour       | 90%, 100%          |
  | 7-day        | 70%, 90%, 100%     |

- **Several Claude accounts**: the **Account** menu lists the saved accounts (by e-mail) and switches
  between them, adds another, signs in again or signs out. If an account belongs to several Claude
  organisations (for example personal and Team/Enterprise), **Organization** picks which one is
  tracked. Accounts are kept strictly apart: results that arrive after a switch are discarded, and
  notification state is per account and organisation.
- **Always reachable**: right-click the widget or a tray icon for the menu, or start Claude Status
  again from the Start menu to open the details flyout of the running copy. The flyout has
  **Account ▾** and **Quit** buttons; `ClaudeStatus.exe --quit` closes it from a script.
- **Sign in once** in a built-in Microsoft Edge (WebView2) window, the same way as on claude.ai.
  Windows single sign-on is on: if your organisation signs in to Claude with Microsoft (Entra ID)
  SSO, the Microsoft account picker offers the account you are signed in to Windows with, plus
  *Use another account*. **Sign in with a different account…** in the menu starts from a clean
  browser profile.
  The session is stored for the current Windows user only, encrypted with DPAPI.
- **No admin rights** needed to run, and **no .NET install** needed: it ships as a
  self-contained executable.

## Install (single PC)

Download `ClaudeStatus-<version>-x64-setup.msi` (or `-ARM64-setup.msi`) from the Releases page and run it,
or use the portable `ClaudeStatus-<version>-win-x64-portable.exe` (the app itself, no installation). Starting a
new version while an older one runs replaces the running copy.

The installer wizard shows the licence, lets you choose the install folder and whether Claude
Status starts automatically for all users, shows progress, and can start the app when it
finishes.

On first start, click the tray icon and sign in. Windows 11 places new tray icons in the
overflow area; to keep both icons visible, go to **Settings → Personalization → Taskbar →
Other system tray icons** and turn on both *Claude Status* entries.

## Deploying with Group Policy (Active Directory)

### 1. Software installation

1. Copy `ClaudeStatus-<version>-x64-setup.msi` to a share that the computer accounts can read, for example
   `\\fileserver\deploy$\ClaudeStatus\`.
2. In **Group Policy Management**, edit a GPO linked to the target computers' OU:
   **Computer Configuration → Policies → Software Settings → Software installation →
   New → Package**, pick the UNC path to the MSI, and choose **Assigned**.
3. The package installs at the next restart. Installing, upgrading or removing it closes a
   running Claude Status first. It is per-machine (Program Files), adds a Start
   menu shortcut, and starts automatically for every user at sign-in (an HKLM `Run` entry).

To skip the autostart entry, deploy with `AUTOSTART=0`. Use a transform (`.mst`) for GPO, or
`msiexec /i ClaudeStatus-<version>-x64-setup.msi AUTOSTART=0 /qn` for Intune, SCCM or scripts.

Upgrades: add the newer MSI to the same GPO as an upgrade of the existing package. All builds
share one UpgradeCode, so the new version replaces the old one.

### 2. Administrative template (optional)

Copy `policy/ClaudeStatus.admx` to `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\`
and `policy/en-US/ClaudeStatus.adml` to its `en-US` subfolder (the central store). Settings then
appear under **Computer/User Configuration → Administrative Templates → Claude Status**:

| Policy                        | Registry value (`SOFTWARE\Policies\ClaudeStatus`)  | Effect                                   |
|-------------------------------|------------------------------------------------------|------------------------------------------|
| Refresh interval              | `RefreshMinutes` (DWORD, 1–60)                       | How often usage is checked               |
| Taskbar display               | `WidgetStyle` (DWORD: 0 bars, 1 compact, 2 icons)    | Display style                            |
| Turn off limit notifications  | `DisableNotifications` (DWORD 0/1)                   | Forces notifications off (1) or on (0)   |
| Allowed account e-mail domains| `AllowedEmailDomains` (REG_SZ, `a.com; b.org`)        | Only accounts in these domains           |
| Allowed Claude organizations  | `AllowedOrganizations` (REG_SZ, UUIDs separated by `;`) | Only these organisations                |

Accounts refused by the two "Allowed" policies are removed together with their stored session,
and the user is told why. If the e-mail address cannot be read, the account is refused (fail
closed).

A configured policy overrides the user's choice and greys out the matching menu item.
HKLM takes precedence over HKCU.

### What each user still does

Each user signs in to their own Claude account once, from the tray icon. Sessions are personal
and are never stored at machine level.

### Requirements

- Windows 10 (1809 or later) or Windows 11, x64 or ARM64.
- Microsoft Edge WebView2 Runtime, used only for the sign-in window. It ships with Windows 11
  and current Windows 10 builds. If it is missing, deploy the Evergreen WebView2 Runtime first.
- Outbound HTTPS to `claude.ai`.

## Troubleshooting

- **Nothing seems to happen when I start it**: if Claude Status is already running, starting the
  same copy again only opens its details flyout (look near the clock). A different version
  replaces the running copy.
- **Which file installs it?** The `-setup.msi` file is the installer. The `-portable.exe` file is
  the app itself and does not install anything.
- **Quit it** from the menu (right-click the widget or a tray icon), the **Quit** button in the
  flyout, or `ClaudeStatus.exe --quit`.

## Where things are stored

| What                     | Where                                              |
|--------------------------|----------------------------------------------------|
| Settings, session (DPAPI)| `%LOCALAPPDATA%\ClaudeStatus\settings.json`        |
| Sign-in browser profile  | `%LOCALAPPDATA%\ClaudeStatus\WebView2\`            |

**Sign out** in the menu removes the stored session. Uninstalling leaves per-user data in place;
delete the folder above to remove it.

## Building

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish src/ClaudeStatus -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:Version=2026.10.1 -o publish/win-x64
dotnet build installer -c Release -p:Platform=x64 -p:PublishDir=$PWD/publish/win-x64 -p:Version=26.10.1 -p:DisplayVersion=2026.10.1
```

The MSI is built with [WiX Toolset](https://wixtoolset.org/) v5, which is restored from NuGet
automatically.

## Versions and releases

Versions are **YYYY.M.number**: year, month, and a counter that restarts each month
(`2026.10.1`, `2026.10.2`, `2026.11.1`). Tags (`v2026.10.1`), release names, file names and the
app's own version all use this form.

The only exception is the MSI's internal ProductVersion: Windows Installer allows at most 255 in
the first field, so it is **YY.M.number** (`26.10.1`). Upgrades compare correctly, and
Add/Remove Programs shows this short form.

Releases are never published automatically. When a build is ready, go to **Actions → build →
Run workflow** and tick **Publish a release**. The workflow picks the next free number for the
current month, builds and smoke-tests everything, and publishes a GitHub release with:

- `ClaudeStatus-<version>-x64-setup.msi` and `ClaudeStatus-<version>-ARM64-setup.msi`
- `ClaudeStatus-<version>-win-x64-portable.exe` and `ClaudeStatus-<version>-win-arm64-portable.exe`
- `ClaudeStatus-<version>-PolicyDefinitions.zip` (ADMX/ADML)

A test build can also be published as a **pre-release**, either by pushing a `vYYYY.M.N` tag or
by pushing a commit whose message contains `[release]` (the next free number is used).

Ordinary pushes build version `YYYY.M.0` and only keep the files as workflow artifacts.

## Verification

The `smoke` job in `.github/workflows/build.yml` runs the built app on hosted **Windows Server
2022** (Windows 10 shell) and **Windows Server 2025** (Windows 11 shell) desktops. It uses demo
data (`ClaudeStatus.exe --demo`), checks that the app keeps running, and uploads screenshots of
the taskbar, the flyout and a notification as build artifacts.

Command-line switches, useful for testing:

| Switch                          | Effect                                         |
|---------------------------------|------------------------------------------------|
| `--demo` / `--demo=alert`       | Sample data, no sign-in, nothing saved         |
| `--style=bars\|compact\|icon`   | Display style for this run                     |
| `--open-details`                | Open the details flyout at start               |

## How it works

- `UsageClient` calls `https://claude.ai/api/organizations` and
  `/api/organizations/{id}/usage` with the signed-in session. claude.ai is behind Cloudflare, which
  blocks plain HTTP clients, so `BrowserFetcher` makes these calls with `fetch()` inside a hidden
  embedded Edge (WebView2) page. Requests run one at a time; each sets the account's session as an
  in-memory cookie and removes it afterwards, so accounts never share a request. Every object in the response that
  has a `utilization` field becomes a limit row, so new limit types appear without code changes.
- `TaskbarWidget` is a borderless, always-on-top, non-activating window placed over the taskbar
  next to the notification area. `TaskbarLayout` reads the taskbar buttons' positions through UI
  Automation every few seconds, and the widget shows only when it fits in the free gap. Windows 11 removed deskbands, so this is how taskbar widgets
  work today.
- `TrayController` draws the two tray icons and owns the menu; `Notifier` sends the threshold
  notifications through the notification area, which Windows shows as native toasts.

## Licence

MIT. See [LICENSE](LICENSE).
