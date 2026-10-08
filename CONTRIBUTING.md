# Contributing

Thank you for helping. Bug reports, fixes and translations are all welcome.

## Reporting a problem

Open an issue with:

- the WinsAlt version (dashboard footer, or `/api/info`) and the operating system
- what you expected and what happened
- relevant lines from the dashboard's Events tab, `journalctl -u winsalt` on Linux, or the Windows Event Log
  (source `WinsAlt`)

Remove host names, addresses and anything else private from what you paste. Security problems go through
[SECURITY.md](SECURITY.md), not public issues.

## Code

- .NET 10, Native AOT. Reflection-based JSON is off: every type that is serialized must be registered in a
  `JsonSerializerContext` in `Web/JsonContexts.cs`. The AOT and trim analyzers are on and their warnings are errors.
- The query path (a name query that hits or misses) must not allocate.
- Anything that touches the operating system needs both a Windows and a Linux branch (`OperatingSystem.IsWindows()`).
- Run the three suites in `tools/` before sending a change (see the README); add a check for what you changed.
- Text people read (UI, help, log and error messages, docs, comments) uses plain punctuation: no em dashes,
  middle dots, ellipsis characters or curly quotes.
- Keep the version in `WinsAlt.csproj` and `WinsAlt_Setup.iss` in step.

## Translations

Every text of the dashboard lives in a language file: `wwwroot/lang/<code>.json`. English (`en.json`) is the
reference and the fallback for any key a translation does not have.

### Format

- One flat JSON object, key to text. Keep the keys exactly; translate the values.
- `_code` is the language code (also the file name), `_name` is the language's own name as shown in the selector
  (for example `"Deutsch"`).
- `{0}`, `{1}` ... are filled in by the page (numbers, names, addresses). Keep them, in whatever order the
  sentence needs.
- Help texts (`setting.<Key>.help`) are arrays of lines:
  - a line starting with `- ` is a list item; `1. ` a numbered step
  - a line starting with two spaces belongs to the item above it
  - an empty string `""` is a paragraph break
- `server.<English message>` keys translate error messages the server sends in English. The key is the exact
  English text; only messages without numbers or names in them can be matched this way.
- Product and protocol terms (WinsAlt, NetBIOS, WINS, UDP, DHCP, replication key, static mapping) may stay in
  English where that is what administrators in your language use.

### Trying a translation without building

Put the file in the `lang` folder of the data folder (Windows: `C:\Program Files\WinsAlt\lang`,
Linux: `/var/lib/winsalt/lang`) and reload the dashboard. The file is read on every request, so edits show
after a reload, with no restart. A file that is not valid JSON is ignored and logged in the Events tab.

### Adding it to the project

Put the file in `wwwroot/lang/`, rebuild, and run `pwsh tools/security-smoke.ps1`. Add the language code to the
two checks that list `en` and `th` ("every setting has a label and an explanation in ...") so that a setting added
later without a translation fails the suite.
