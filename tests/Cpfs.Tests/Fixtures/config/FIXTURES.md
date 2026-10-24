# Config fixtures

Samples of `C:\ProgramData\CPFS\config.json` for the reader and writer tests. All values are placeholders
(`HOST-01`, `ShareUser`, `D:\Shared`, dates in 2000). Files are in the layout of Windows PowerShell 5.1
`ConvertTo-Json -Depth 6`, with CRLF line endings.

| File | Represents | Source |
|---|---|---|
| `v0.1.json` | 0.1: no `Tracked`, no `Backup` | Synthetic, from the script code |
| `v0.2.0-first-run-bom.json` | 0.2.0 first run: full `Backup` with eight `HKLM:\` entries, one item per `Tracked` array, BOM, PowerShell's `\u003c` escapes | Synthetic |
| `v0.2.1-upgraded-from-0.1.json` | `Backup: null`, `Version` 0.2.1 | Synthetic |
| `v0.2.1-partial.json` | Interim save of an interrupted first run: `Partial: true`, `Backup` only | Synthetic |
| `v0.2.x-scalar-arrays.json` | `Tracked.Users` a single object, `Tracked.Networks` a single string, `FirewallRules` null | Synthetic |
| `v0.2.x-count-shape.json` | Arrays as `{"value": [...], "Count": n}`, lower-case keys | Synthetic |
| `v0.2.x-no-bom-persian.json` | No BOM, a Persian network name, an unknown field | Synthetic |
| `v0.2.3-anonymised.json` | A real 0.2.3 install, every identifying value replaced; layout, key order and types as found | Real, anonymised |
| `v0.3.0-written.json` | What the app writes from `v0.2.0-first-run-bom.json`; golden file of the round-trip test | Written by `ConfigWriter` |

No fixture may contain a value from a real machine. `FixturePrivacyTests` checks this against a private
config when one is available on the build machine.
