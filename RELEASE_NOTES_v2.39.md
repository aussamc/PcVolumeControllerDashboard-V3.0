# Release Notes — v2.39

## New feature: update checker

The dashboard now automatically checks GitHub Releases for a newer version shortly after startup, and displays a non-intrusive dismissible banner when an update is available.

### Details

- **Startup check** — runs ~5 seconds after launch on a background thread so it doesn't compete with serial connect or audio initialisation
- **Non-intrusive banner** — a blue info strip appears between the header and tab area when a newer version is found; includes the version number and a "View release notes" hyperlink
  - Dismissed for the session when the ✕ button is clicked
  - Re-appears (and re-checks) when the manual **Check now** button is used
- **Setup tab section** — a new **Software Updates** card in the Setup tab shows:
  - Current check status (up to date / update available / error message)
  - Timestamp of the last check
  - **Check now** button for on-demand checks
- Uses the GitHub Releases API (`/releases/latest`) via a static `HttpClient` (reuses socket, sets correct `User-Agent` and `Accept` headers)
- Version comparison is numeric (e.g. v2.9 < v2.10), with ordinal string fallback
- Network errors are surfaced as status text and logged; they never show a dialog or block startup
- Dark theme support — the info banner uses a complementary blue palette in both light and dark modes

## Compatibility

- Dashboard: v2.39
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
