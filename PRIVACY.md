# Privacy Policy

**Effective date:** 21 September 2026
**Applies to:** the `revit-mcp` MCP server and its bundled Revit bridge add-in
(the "Software"), distributed as the npm package `@rui.branco/revit-mcp` and the `revit` plugin.

## Summary

The Software runs entirely on your own computer. It has no backend, no account,
no telemetry and no analytics. It sends nothing to the author, and it does not
transmit your data anywhere over the internet.

## Data collection

**The author collects no data whatsoever.** The Software contains no telemetry,
crash reporting, usage analytics, update check or licence check. No personal
information, model content, file path or identifier is transmitted to the author
or to any third party operated by the author.

## What the Software processes, and where

The Software reads and edits the Revit model open on your machine. That data is
processed locally and in memory:

- The Node MCP server exchanges JSON with the bridge add-in strictly over
  loopback HTTP at `http://127.0.0.1:48884`. The listener binds the `127.0.0.1`
  literal, not a wildcard, so it is not reachable from your network.
- The bridge add-in runs inside `Revit.exe` and calls the Revit API directly.
- Model data leaves your machine only if your MCP client sends it onward. When
  the client is Claude, your prompts and the tool results the model sees are
  handled under **Anthropic's** privacy policy, not this one:
  <https://www.anthropic.com/legal/privacy>

Choosing what to ask Claude, and therefore what model content is included in a
conversation, is yours to control.

## Local storage on your machine

The Software writes only these, all locally, and never transmits them:

| What | Where | Why |
| --- | --- | --- |
| Bridge log | `%LOCALAPPDATA%\RevitMcpBridge\bridge.log` | Diagnosing failures. May contain Revit element ids, view and sheet names, and error text. |
| Bridge add-in files | `%APPDATA%\Autodesk\Revit\Addins\<version>\` | The installed add-in and its `.addin` manifest. |
| Diagnostics buffer | In memory, inside Revit | The last 200 dismissed dialogs and resolved warnings. Lost when Revit closes. |
| Exports you request | The path you specify | Images and PDFs the Software writes only when a tool is called with that path. |

Delete the log file at any time; the Software recreates it only when it next
logs. Uninstalling the bridge (`revit_uninstall_bridge`) removes the add-in
files.

## Third-party sharing

**None.** The author does not share, sell, rent or disclose any data, because the
author never receives any.

The Software makes no outbound internet request of its own. Network activity you
may observe around it comes from other parties under their own policies: your MCP
client contacting its provider, and `npm`/`npx` downloading the package from
<https://registry.npmjs.org> at install time.

## Data retention

The author retains nothing, having received nothing. Local files listed above
stay on your machine until you delete them or uninstall the Software. The
in-memory diagnostics buffer holds at most 200 entries and is discarded when
Revit closes.

## Children

The Software is a professional tool for Autodesk Revit and is not directed at
children under 13.

## Security

The bridge add-in exposes an **unauthenticated** loopback HTTP listener with full
Revit API access while Revit is running: any local process on your machine can
reach it. The add-in is not code-signed. Review the
[Security](README.md#security) section of the README before installing on a
shared or untrusted machine.

## Changes to this policy

Material changes will be published in this file, with the effective date above
updated, and will appear in the repository's commit history:
<https://github.com/rui-branco/revit-mcp/commits/main/PRIVACY.md>

## Contact

Questions, or a privacy or security concern:

- Issues: <https://github.com/rui-branco/revit-mcp/issues>
- Private security reports:
  <https://github.com/rui-branco/revit-mcp/security/advisories/new>
