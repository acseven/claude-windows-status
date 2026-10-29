# Claude Status – project rules

Windows 10/11 taskbar and tray utility (C# .NET 8, WPF) showing Claude plan usage. Deployed by MSI
(`installer/`, WiX v5) and managed through Group Policy (`policy/ClaudeStatus.admx` + `en-US/*.adml`).

## Group Policy check — required for every review, merge and release

Any code review, merge (including PR merges) and release (`[release]` commit, `vYYYY.M.N` tag or
"Publish a release" run) **must** include a GPO check, and its result must be stated in the review,
PR or release notes. Do not merge or release while it fails.

Items 1 and 5 are automated by the `policy-check` CI job (`.github/scripts/check_policy.py`, run
locally with `python3 .github/scripts/check_policy.py`); releases do not run unless it passes.
Items 2–4 and 6 need a human/Claude review.

The check:

1. **Definitions match the code.** Every policy in `policy/ClaudeStatus.admx` has the same value name,
   registry type and value range as `src/ClaudeStatus/Policy.cs` reads (DWORD → `ReadDword`,
   REG_SZ list → `ReadList`), under `SOFTWARE\Policies\ClaudeStatus` (HKLM before HKCU), and every
   value the code reads is defined in the ADMX.
2. **Explanations are true.** Each `*_Help` string in `policy/en-US/ClaudeStatus.adml` describes what
   the code actually does (including "Disabled" / "Not configured"), and the policy table in
   `README.md` agrees.
3. **Policies are enforced wherever the setting is used.** Behaviour goes through the `Effective*`
   properties in `Settings.cs` or `Policy.Current`, never the raw user setting; menu items for
   policy-controlled settings are greyed out.
4. **Tenancy policies fail closed.** `AllowedEmailDomains` / `AllowedOrganizations`: a refused
   account is removed with its stored session and its usage history, and the user is told; history
   of a refused organisation is deleted; an unreadable e-mail is refused when domains are set;
   transient network errors are never treated as a refusal; refusals only come from a configured
   policy. `DisableUsageHistory` deletes existing history and stops recording.
5. **Files are valid.** ADMX/ADML parse as XML, and every `$(string.*)` / `$(presentation.*)`
   reference resolves.
6. **Changes are reflected.** A new, renamed or changed setting that an administrator could want to
   control is either added to the ADMX/ADML (and README) or the review states why not. Value names
   are never reused with a different meaning.

## Account isolation

Accounts never share data: one request at a time through `BrowserFetcher` with only that account's
session cookie (deleted afterwards); results for a previous account/refresh are discarded
(`UsageMonitor` generation check); notification state and history are keyed by account id +
organisation id, never by label or e-mail. Changes touching these paths need a review of this.

## Development

- WPF cannot be built on Linux. Type-check locally against the WindowsDesktop reference assemblies
  if needed, but the real build, MSI and Windows smoke tests run in CI (`.github/workflows/build.yml`).
- New behaviour should be visible in the smoke test where possible (demo data via `--demo`).
- `ci-screenshots` is a CI-owned branch (force pushed); never commit to it by hand.

## Versions and deployment shares

- Versions are `YYYY.M.number` (`2026.10.11`) everywhere (tags, releases, release file names, app
  version) **except** the MSI's ProductVersion, which is `YY.M.number` (`26.10.11`): Windows
  Installer allows at most 255 in the first field. Conversion: `(YYYY - 2000).M.number`.
- Windows and install scripts see only the MSI's ProductVersion (registry `DisplayVersion`,
  Add/Remove Programs, `msiexec` logs), i.e. `26.10.11`. Anything that compares an installed version
  with a **file name** must use the same form, or it sees "different" every time and **reinstalls
  the app at every start-up**.
- Therefore the copy of the installer on a deployment share (GPO, start-up script, Intune, SCCM) is
  named `ClaudeStatus_<YY.M.N>_<arch>.msi`, e.g. `ClaudeStatus_26.10.11_x64.msi`. Release assets keep
  the full form (`ClaudeStatus-2026.10.11-x64-setup.msi`); rename when copying to the share.
- Never compare `2026.10.11` against `26.10.11` as strings; convert one to the other first.
- Any change to version numbering, MSI naming or the installer's version properties must keep these
  statements true and update the README ("Deploying with Group Policy" and "Versions and releases").

## Other conventions

- Never put personal information (names, e-mail addresses, company names, internal hosts) in the repo.
- Releases are only made on purpose (see README, "Versions and releases").
