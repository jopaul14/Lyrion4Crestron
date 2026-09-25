# Contributing

Thanks for your interest. This is a small, single-maintainer project that
produces device drivers for Crestron Home control systems. That shapes what a
useful contribution looks like — please read this before opening a pull
request.

## Branching

| Branch | Role |
|---|---|
| `dev` | The working branch. **All pull requests target `dev`.** |
| `main` | The release branch. It moves only when a version ships. |

A PR opened against `main` will be asked to retarget. If you are picking up an
issue, branch from `dev`, not from `main` — `main` can be several releases
behind, and files move between releases.

## Claim the issue first

Comment on the issue before you start. Some issues are already in progress, and
some are deliberately parked — see the "not scheduled" ones. This avoids work
that cannot be merged.

## Build it before you submit

**A pull request that has not been compiled is not ready for review.**

Building requires the .NET Framework 4.7.2 targeting pack and the Crestron
Certified Drivers SDK. Full instructions are in [BUILD.md](BUILD.md).

```
msbuild Lyrion4Crestron.sln /p:Configuration=Release /restore
```

If you cannot install those, this repository is probably not one you can
usefully contribute *code* to. Documentation and README corrections are still
welcome and do not need the toolchain.

## Testing

There is no automated test suite. Verification is manual, against a Crestron
Home processor and a live Lyrion Music Server — hardware the maintainer has and
you most likely do not.

So: **state plainly in the PR what you verified and what you could not.** An
honest "compiled, not run on hardware" is useful. An unqualified "tested" that
turns out to mean "read the diff again" is not.

## Issues are proposals, not specifications

Several issues here come out of repo-wide audits and are written as though the
fix were already settled. They are the maintainer's best guess. If you work one
and find the premise does not hold, **say so** — in the PR or on the issue.
That is a more valuable contribution than the change itself.

Worked example: [#72](https://github.com/jopaul14/Lyrion4Crestron/issues/72)
asked for the hand-rolled percent-encoder in `LmsTokenCodec.Encode` to be
replaced with `Uri.EscapeDataString`, asserting the two were
character-for-character identical. On .NET Framework they are not, and the
difference is selected by a host process the driver does not control.
[#77](https://github.com/jopaul14/Lyrion4Crestron/pull/77) implemented the
issue faithfully and was closed as a result. Checking the premise against the
actual target framework would have caught it; checking it against seven sample
strings did not.

## Platform specifics that catch people out

These drivers target **.NET Framework 4.7.2** and run inside Crestron's driver
host on a control processor.

- **Behaviour you verified on .NET 8 may not hold on net472.** `Uri`, string
  comparison, and the encoding APIs all changed between them. If a change turns
  on what a BCL call returns, verify it on the target framework.
- **The drivers are libraries loaded by a host process you do not control.**
  Anything whose behaviour keys off the entry assembly's target framework or
  off `app.config` is not deterministic here.
- **Fewer lines is not automatically better.** Replacing local code with a
  framework call is only an improvement if the framework call is equivalent,
  deterministic, and fails no worse. On the connection and credential paths,
  determinism beats brevity.

## Scope and house rules

- **[docs/PRD.md](docs/PRD.md) is authoritative** for architecture and
  behaviour. Where any other document disagrees, the PRD wins. Read it before
  proposing a behavioural change.
- **The invariants in [CLAUDE.md](CLAUDE.md) are load-bearing.** In particular:
  only the Lyrion Server opens LMS connections, and the Source/Helper/Receiver
  drivers are thin adapters with no state of their own.
- **Do not bump `DriverVersion` in `Driver.json`.** All four driver versions are
  bumped together at release time.
- **Line endings are LF**, pinned by `.gitattributes`. Do not reformat files you
  are not otherwise changing.
- Match the surrounding code: its naming, its comment density, its idiom.

## AI-assisted contributions

AI assistance is fine. Unreviewed AI output is not. If you used a coding agent:

- **You are the author.** You are accountable for the change and should be able
  to answer questions about it without going back to the model.
- **Build it, and describe what you actually tested.** "The model said the two
  were equivalent" is not verification — see #72 above for how that goes.
- **One pull request at a time, and engage with the review.** Bulk or drive-by
  pull requests — opened across many repositories with no engagement with this
  project — will be closed and the account blocked, whether or not the
  individual change happens to be correct.

## Reporting bugs

Include the driver versions, the Crestron driver runtime version, your Lyrion
Music Server version, and what the room/player configuration looks like. Driver
logging goes to `Trace`, tagged `[Lyrion.Server|Source|Helper|Receiver]`; it
does **not** appear in `errlog`, so an empty errlog tells us nothing. Capture it
from the Toolbox Text Console with logging to file.
