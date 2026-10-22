---
title: Configuration
lede: The settings of the container, the identity provider presets, and what to know about proxies, volumes, updates and backups.
---

## How to set a value

Solo reads its settings from environment variables. Set them in the `environment` section of the Compose file. Restart the container after a change.

A variable name uses two underscores between its parts, for example `Auth__ClientId`.

## Settings

### Required

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Default` | The connection to the PostgreSQL database. |
| `Auth__Authority` | The issuer address of the identity provider. |
| `Auth__ClientId` | The OIDC client ID. |
| `Auth__Provider` | `google`, `microsoft` or `generic`. |
| `Setup__Username` | The user name of the setup account. Solo uses it only in the setup wizard. |
| `Setup__Password` | The password of the setup account. It must have 20 characters or more. |

### Optional

| Variable | Default | Purpose |
|---|---|---|
| `Auth__SessionDays` | `30` | The number of days that a desktop session lasts with no use. |
| `Auth__DesktopRedirect` | `false` | Google only. The desktop app goes directly to Google's sign-in, in one window. See [Desktop sign-in with Google](#desktop-sign-in-with-google). |
| `Database__AutoMigrate` | `true` | Apply database migrations at startup. |
| `Themes__Directory` | `/data/themes` | The directory that Solo scans for theme packages. |
| `Extensions__Directory` | `/data/extensions` | The directory that Solo scans for extension packages. |
| `Extensions__ApiBaseUrl` | The listen address | The address at which background work of an extension reaches the Solo API. |
| `ForwardedHeaders__KnownProxies` | None | Addresses of proxies on public addresses, with commas between them. |
| `ForwardedHeaders__ForwardLimit` | `1` | The number of proxies in front of Solo. |
| `RateLimiting__Auth__PermitLimit` | `10` | The number of sign-in and setup requests that one address can make in one window. |
| `RateLimiting__Auth__WindowSeconds` | `60` | The length of that window, in seconds. |
| `RateLimiting__Api__Burst` | `300` | The number of API requests that one caller can make in a burst. |
| `RateLimiting__Api__TokensPerPeriod` | `100` | The number of API requests that a caller gets back in each period. |
| `RateLimiting__Api__PeriodSeconds` | `10` | The length of that period, in seconds. |

The MCP extension adds one setting:

| Variable | Default | Purpose |
|---|---|---|
| `Mcp__ApiBaseUrl` | The listen address | The address at which the MCP extension reaches the Solo API. |

Set `Extensions__ApiBaseUrl` or `Mcp__ApiBaseUrl` only when the container cannot connect to its own listen address.

## Identity provider

One instance uses one identity provider.

| Provider | `Auth__Authority` | `Auth__Provider` |
|---|---|---|
| Google | `https://accounts.google.com` | `google` |
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant-id>/v2.0` | `microsoft` |
| A different OIDC issuer | The address of the issuer | `generic` |

Solo identifies a person by the issuer and the subject of the token. A person can sign in only after a project owner invited the email address of the account. The super admin from the setup wizard is the exception.

### Desktop sign-in with Google

The desktop app signs in to a Google instance on the page `https://<your address>/desktop-auth`. On macOS this page opens in a sign-in window above the app. On other systems it opens in the browser. The page uses the theme of the instance.

By default, the page shows Google's button, and the button opens the Google account selection in a second window. With redirect mode, the page goes directly to Google's sign-in, in the same window, with no click. Redirect mode applies to the desktop app only. The web sign-in does not change. To use redirect mode:

1. In the Google Cloud console, open your OAuth client.
2. Add `https://<your address>/desktop-auth` as an authorized redirect URI. Use the exact address: no slash at the end and no query.
3. Set `Auth__DesktopRedirect` to `true`, then restart Solo.

Do step 2 before step 3. If the redirect URI is not registered, Google refuses the desktop sign-in.

Use an `https` address. With an `http` address, Safari shows a warning about a non-secure form when Google returns to the page.

## Reverse proxy

The container serves HTTP on port 8080. Terminate TLS in a reverse proxy or a tunnel, and connect it to the container with `http`.

Solo limits requests for each client address. To learn the client address behind a proxy, Solo reads the `X-Forwarded-For` header. Solo trusts that header only from these senders:

- The loopback address.
- A private or link-local address, such as a container network or a LAN.

This covers a proxy on the same host, a proxy on your LAN, and a tunnel container next to Solo.

- If your proxy connects from a public address, name the address in `ForwardedHeaders__KnownProxies`.
- If two proxies are in front of Solo, set `ForwardedHeaders__ForwardLimit` to `2`.

Solo answers health probes at `/healthz`.

## Volumes

| Volume | Path in the container | Content |
|---|---|---|
| `solo-pgdata` | `/var/lib/postgresql/data` (in the `postgres` service) | The database. |
| `solo-themes` | `/data/themes` | Theme packages that you add. |
| `solo-extensions` | `/data/extensions` | Extension packages that you add. |

The three built-in themes are part of the image. The themes volume is only for themes that you add.

### Host paths

You can use a host path in place of a named volume:

```yaml
volumes:
  - /mnt/tank/apps/solo/themes:/data/themes
```

The container runs as a user that is not root, with UID `1654`. Make the host directory readable for that user.

If Solo cannot read the directory, it does not load the packages. Solo then starts with the built-in themes only, and gives the reason in the Admin settings and in the startup log.

## Database

Solo uses one PostgreSQL database. Solo applies its migrations at startup. Each extension that stores data has its own schema in the same database, with the name `ext_<id>`. Solo applies the migrations of each extension after its own.

To apply migrations yourself, set `Database__AutoMigrate` to `false`. Solo then also does not apply the migrations of extensions.

## Update

Pull the new image and start the container again:

```bash
docker compose pull
docker compose up -d
```

The image has these tags:

| Tag | Example | Content |
|---|---|---|
| `latest` | `latest` | The newest server release. |
| Full version | `1.14.1` | One exact release. |
| Major and minor version | `1.14` | The newest patch release of that minor version. |
| Commit | `sha-0a1b2c3` | The build of one commit. |

Themes and extensions in the volumes stay in place across an update.

## Backup

Make a backup of the database. It holds all projects, the audit log, and the data of each extension.

The themes volume and the extensions volume hold only the packages that you copied there. Keep the original package files and you can copy them again.
