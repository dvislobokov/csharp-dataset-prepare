# Trust and security

Input repositories are untrusted.

| Mode | Executes input-controlled code? | Network | Notes |
|---|---|---|---|
| `discover`, `extract --mode syntax_only`, `validate`, `render` | No | No | Pure file reads + Roslyn parsing. Safe default. |
| `extract --semantic-source adhoc` | No | No | Projects rebuilt from discovered files, `ProjectReference` items read as plain XML, shared-framework reference assemblies. No MSBuild evaluation, no NuGet. Package symbols resolve as error types. |
| `extract --semantic-source msbuild --trusted-project-evaluation` | **Yes** (MSBuild evaluates project files, imports SDK/NuGet `.props/.targets`, runs design-time targets in the BuildHost process) | No by itself; requires a prior `dotnet restore` | Refused without the flag. Use only for trusted repos (the eShop pilot). |
| `dotnet restore` (manual, pilot only) | **Yes** (NuGet + MSBuild targets) | Yes | Writes `obj/` inside the checkout (git-ignored). The pilot ran it per project, excluding MAUI projects. |

Never executed in any mode: the analyzed application, its tests, build outputs, analyzers or source generators' outputs
(source generators are not run; `*.g.cs` are excluded).

## Isolation strategy for trusted semantic mode at scale

Run MSBuild evaluation per repository in a disposable container/VM with: read-only source mount, no credentials, network only
to a NuGet mirror during restore and none during evaluation, CPU/memory/time limits, and a fresh `NUGET_PACKAGES` cache per
trust domain. Repositories that fail or are not allowlisted fall back to the adhoc or syntax-only modes with reason codes.

## Other controls

* `pilot fetch`: https only, host allowlist, `git` invoked with argument lists (no shell), `GIT_TERMINAL_PROMPT=0`, refs
  starting with `-` rejected.
* Manifest reader rejects credentials in URLs, non-allowlisted schemes/hosts, path traversal and shell metacharacters.
* Secret regexes skip whole files (`secret_detected`) and individual lines; patterns are configurable.
* Unknown or non-allowlisted licenses keep files out of the corpus unless `license.allow_unknown` is set explicitly.
* Discovery does not follow symlinks.
