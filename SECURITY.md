# Security Policy

Wisp is a local screen/audio recorder that runs alongside games, including
games with third-party anti-cheat. It also loads third-party plugins
in-process and runs a couple of small local network listeners for kill
detection (LoL's official Live Client Data API on 127.0.0.1:2999, and
CS2's Game State Integration over a local TCP listener). If you find a
security issue anywhere in that, or anywhere else in the app, we want to
know before it's public.

## Reporting a vulnerability

Email contact@minimalpulse.com with:
- What you found and where (file/module if you know it).
- Steps to reproduce, or a proof of concept if you have one.
- What you think the impact is.

We'll acknowledge reports within a few days. Wisp is maintained by a
small team, so fixes may take a bit longer than you'd get from a large
vendor, but we'll keep you updated. We'll credit you in the release
notes when a fix ships, unless you'd rather stay anonymous.

Please don't open a public GitHub issue for a report before a fix is
out; email first.

## Scope

In scope:
- The Wisp application itself (this repository).
- The bundled release binaries distributed from
  https://github.com/Kaayoos/wisp/releases.
- The plugin loader / plugin SDK boundary.

Out of scope:
- Third-party plugins not maintained by MinimalPulse; report those to
  their own authors.
- The bundled FFmpeg binary's own code; report upstream to the FFmpeg
  project, though we're happy to help point you the right way.
