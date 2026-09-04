# Security

## What the package contains

The download holds no secrets of mine. The setup wizard collects your own SFTP password and panel
API key at run time and writes them to `secrets.json` in the per-server data folder, owner-only
(`0600`). `config.json` holds only the safe settings and never a credential.

## How plugin updates are verified

Each release publishes `NukeStats.dll` alongside a `.sha256` and a `.minisig`. The updater checks
the SHA-256 for integrity and the Ed25519 minisign signature against `installer/trusted.pub`, which
ships with the toolkit and is the trust root. The [README](README.md#updating) covers what happens
when a signature cannot be checked.

## The dashboard

The web command centre has no login. It binds to `127.0.0.1` by default, so leave it there or on a
trusted LAN, and do not put it on a publicly reachable IP. There is more on this in the
[README](README.md#security).

## Reporting a vulnerability

Open a private security advisory on the repository. Please do not file a public issue for anything
exploitable, such as a score or economy exploit, remote code execution, or a credential leak, until
it is fixed.
