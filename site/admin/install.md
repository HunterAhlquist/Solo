---
title: Install
lede: Run Solo with Docker Compose, connect your identity provider, and complete the setup wizard.
---

## What you need

- A host that runs Docker with Docker Compose.
- An OIDC client ID from your identity provider: Google, Microsoft Entra ID, or a different OIDC issuer.
- The address at which people will open Solo, for example `https://solo.example.com`.

Solo runs as one container and uses one PostgreSQL database. The container listens on port 8080 and does not serve TLS. Put a reverse proxy or a tunnel with TLS in front of it.

## 1. Register an OIDC client

Create a client in your identity provider.

1. Select the client type for a browser application (a single-page application).
2. Register the address of your Solo instance:
   - **Google.** Add the address as an authorized JavaScript origin.
   - **Other providers.** Add `https://<your address>/authentication/login-callback` as a redirect address. The flow is authorization code with PKCE.
3. Copy the client ID.

For Google, copy the full client ID. It ends in `.apps.googleusercontent.com`. With a shortened ID, sign-in works but each request to Solo fails.

> One instance uses one identity provider. Solo does not support a change of provider after people sign in.
{: .warning}

## 2. Get the image

The release image is `ghcr.io/hunterahlquist/solo`. If your host cannot pull it, build the image from the repository:

```bash
git clone https://github.com/HunterAhlquist/Solo.git
cd Solo
docker build -t solo .
```

Then use `image: solo` in the Compose file.

## 3. Write the Compose file

The repository has a template with comments: [`deploy/docker-compose.yml`](https://github.com/HunterAhlquist/Solo/blob/main/deploy/docker-compose.yml). This is the same file with the comments removed:

```yaml
services:
  solo:
    image: ghcr.io/hunterahlquist/solo:latest
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    ports:
      - "30080:8080"
    environment:
      ConnectionStrings__Default: "Host=postgres;Port=5432;Database=solo;Username=solo;Password=CHANGE_ME_DB_PASSWORD"
      Auth__Authority: "https://accounts.google.com"
      Auth__ClientId: "CHANGE_ME_OIDC_CLIENT_ID"
      Auth__Provider: "google"
      Setup__Username: "setup"
      Setup__Password: "CHANGE_ME_SETUP_PASSWORD"
      Themes__Directory: "/data/themes"
      Extensions__Directory: "/data/extensions"
    volumes:
      - solo-themes:/data/themes
      - solo-extensions:/data/extensions
    healthcheck:
      test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 30s

  postgres:
    image: postgres:17
    restart: unless-stopped
    environment:
      POSTGRES_DB: solo
      POSTGRES_USER: solo
      POSTGRES_PASSWORD: "CHANGE_ME_DB_PASSWORD"
    volumes:
      - solo-pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U solo -d solo"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  solo-pgdata:
  solo-themes:
  solo-extensions:
```

Replace each `CHANGE_ME` value before the first start.

| Value | What to enter |
|---|---|
| `CHANGE_ME_DB_PASSWORD` | A password for the database. Use the same value in the two places. |
| `CHANGE_ME_OIDC_CLIENT_ID` | The client ID from step 1. |
| `CHANGE_ME_SETUP_PASSWORD` | A random password of 20 characters or more. |

To make a setup password:

```bash
openssl rand -base64 32
```

For Microsoft Entra ID or a different issuer, change the three `Auth__` values. See [Configuration](configuration.md#identity-provider).

> The setup account is the only protection of a new instance until you complete the wizard. Solo disables the account if the password is shorter than 20 characters or starts with `CHANGE_ME`. The wizard and the startup log then give the reason.
{: .danger}

## 4. Start Solo

```bash
docker compose up -d
```

On the first start, Solo creates its database tables. Then point your reverse proxy or tunnel at port `30080` of the host. Use `http` for that connection.

## 5. Complete the setup wizard

Open the address of the instance in a browser. Solo shows the **Set up Solo** wizard.

1. **Operator credentials.** Enter the `Setup__Username` and `Setup__Password` values.
2. **Retention.** Enter the number of days that Solo keeps audit entries. You can change this later.
3. **Sign in.** Sign in with your own account from the identity provider. This account becomes the super admin.
4. **Finish.** Enter the setup credentials again to confirm.

Solo then opens the Projects page, and disables the setup account permanently.

If the **Sign in** step shows that the instance has no identity provider, set the three `Auth__` values. Then restart the container and load the page again.

## After the installation

- [Create a project](../guide/projects.md#create-a-project) and invite your team.
- Read [Configuration](configuration.md) for the reverse proxy settings and the other options.
- Install the [Roadmaps and MCP extensions](packages.md#install-an-extension), or a [theme](packages.md#install-a-theme).
- Give your team the [desktop app](../guide/desktop.md).
