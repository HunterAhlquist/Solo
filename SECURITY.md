# Security

## Reporting a vulnerability

Please don't open a public issue for a security problem. Report it privately
through GitHub instead: on this repository's **Security** tab, choose
**Report a vulnerability**. Only I see the report, and we can work out the fix
and the advisory together there.

Include what you can:

- the affected version: the server image tag, the desktop app version, or the
  extension package and its version
- how to reproduce it
- what an attacker gains, and what they need first (an account, a project
  membership, a particular setting)

Solo is maintained by one person, so please give me a chance to ship a fix
before you disclose anything publicly.

## Supported versions

Security fixes go into the latest release only:

- the server image (`v*` releases)
- the desktop app (`v*` releases)
- the bundled extensions (MCP server, Roadmaps), which are released alongside
  the server

## Scope

In scope: the server (the API, the SignalR hub, authentication and
authorization), the web client, the desktop app, and the bundled extensions.

Out of scope:

- **Third-party extensions.** An extension runs inside the server with its
  database connection, so installing one is deploying code. Report a problem in
  one to its author.
- **What an instance's own admins can already do.** A super admin can install
  extensions and change instance settings; doing those things is not a
  vulnerability.
- **Your deployment's surroundings**: the reverse proxy, TLS, the host, and the
  database's network exposure.
