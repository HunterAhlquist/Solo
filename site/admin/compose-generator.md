---
title: Compose generator
nav_title: Compose generator
lede: Make a Docker Compose file for Solo with new passwords, your storage paths and an optional Cloudflare Tunnel.
# Scripts on this page cannot open a connection of any kind, so the values in
# the form cannot leave the browser. The inline script in the layout's head is
# why script-src allows 'unsafe-inline'.
csp: "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; form-action 'none'; base-uri 'none'; object-src 'none'"
---

> This generator runs only in your browser. It sends nothing to a server and it saves nothing. The page tells your browser to block all network connections from scripts, so your values cannot leave it. Your browser makes the passwords with `crypto.getRandomValues`.
{: .tip}

To check this, open the developer tools of your browser and look at the network activity while you use the form. The values stay only until you close or reload the page.

<noscript>
<p class="generator__noscript">The generator needs JavaScript. Use the template in <a href="{{ '/admin/install/' | relative_url }}#3-write-the-compose-file">Install</a> instead.</p>
</noscript>

## Instance

<div class="generator">
  <div class="field-grid">
    <div class="field">
      <label class="field__label" for="cg-image">Image</label>
      <input class="input" id="cg-image" type="text" value="ghcr.io/hunterahlquist/solo:latest" autocomplete="off" spellcheck="false" autocapitalize="off" aria-describedby="cg-image-help cg-image-error">
      <span class="field__help" id="cg-image-help">Use <code>solo</code> for an image that you built yourself.</span>
      <span class="field__error" id="cg-image-error" hidden></span>
    </div>
    <div class="field">
      <label class="field__label" for="cg-port">Host port</label>
      <input class="input" id="cg-port" type="text" inputmode="numeric" value="30080" autocomplete="off" spellcheck="false" aria-describedby="cg-port-help cg-port-error">
      <span class="field__help" id="cg-port-help">The port on the host. Solo listens on 8080 in the container.</span>
      <span class="field__error" id="cg-port-error" hidden></span>
    </div>
  </div>
  <label class="check">
    <input type="checkbox" id="cg-updates" checked>
    <span>Check for updates<span class="field__help">Solo reads a public file on GitHub to find new releases, and sends nothing about the instance. Clear this to make no outbound call.</span></span>
  </label>
</div>

## Sign-in

<div class="generator">
  <div class="field-grid">
    <div class="field">
      <label class="field__label" for="cg-provider">Identity provider</label>
      <select class="input" id="cg-provider">
        <option value="google">Google</option>
        <option value="microsoft">Microsoft Entra ID</option>
        <option value="generic">A different OIDC issuer</option>
      </select>
    </div>
    <div class="field" id="cg-tenant-field" hidden>
      <label class="field__label" for="cg-tenant">Tenant ID</label>
      <input class="input" id="cg-tenant" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" placeholder="00000000-0000-0000-0000-000000000000">
    </div>
    <div class="field" id="cg-authority-field" hidden>
      <label class="field__label" for="cg-authority">Issuer address</label>
      <input class="input" id="cg-authority" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" placeholder="https://auth.example.com" aria-describedby="cg-authority-error">
      <span class="field__error" id="cg-authority-error" hidden></span>
    </div>
  </div>
  <div class="field">
    <label class="field__label" for="cg-client-id">Client ID</label>
    <input class="input" id="cg-client-id" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" aria-describedby="cg-client-id-help">
    <span class="field__help" id="cg-client-id-help">From <a href="{{ '/admin/install/' | relative_url }}#1-register-an-oidc-client">step 1 of Install</a>. For Google, use the full ID that ends in <code>.apps.googleusercontent.com</code>.</span>
  </div>
</div>

## Passwords

<div class="generator">
  <div class="field">
    <label class="field__label" for="cg-setup-username">Setup user name</label>
    <input class="input" id="cg-setup-username" type="text" value="setup" autocomplete="off" spellcheck="false" autocapitalize="off" aria-describedby="cg-setup-username-error">
    <span class="field__error" id="cg-setup-username-error" hidden></span>
  </div>
  <div class="field">
    <label class="field__label" for="cg-setup-password">Setup password</label>
    <div class="input-row">
      <input class="input" id="cg-setup-password" type="text" readonly autocomplete="off" spellcheck="false" aria-describedby="cg-setup-password-help">
      <button class="button" type="button" data-regenerate="cg-setup-password"><svg class="icon" aria-hidden="true" focusable="false"><use href="#i-refresh"/></svg> New</button>
    </div>
    <span class="field__help" id="cg-setup-password-help">You enter this in the setup wizard. Solo disables the setup account when the wizard is complete.</span>
  </div>
  <div class="field">
    <label class="field__label" for="cg-db-password">Database password</label>
    <div class="input-row">
      <input class="input" id="cg-db-password" type="text" readonly autocomplete="off" spellcheck="false" aria-describedby="cg-db-password-help">
      <button class="button" type="button" data-regenerate="cg-db-password"><svg class="icon" aria-hidden="true" focusable="false"><use href="#i-refresh"/></svg> New</button>
    </div>
    <span class="field__help" id="cg-db-password-help">Only the two containers use it. You do not have to keep a copy.</span>
  </div>
</div>

## Storage

Enter a directory on the host for each type of data. Leave a field empty to keep that data in a Docker volume.

<div class="generator">
  <div class="field">
    <label class="field__label" for="cg-pgdata">Database</label>
    <input class="input" id="cg-pgdata" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" placeholder="/mnt/tank/apps/solo/pgdata" aria-describedby="cg-pgdata-help cg-pgdata-error">
    <span class="field__help" id="cg-pgdata-help">All projects, the audit log and the data of each extension.</span>
    <span class="field__error" id="cg-pgdata-error" hidden></span>
  </div>
  <div class="field">
    <label class="field__label" for="cg-themes">Themes</label>
    <input class="input" id="cg-themes" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" placeholder="/mnt/tank/apps/solo/themes" aria-describedby="cg-themes-help cg-themes-error">
    <span class="field__help" id="cg-themes-help">Theme packages that you add. The built-in themes are in the image.</span>
    <span class="field__error" id="cg-themes-error" hidden></span>
  </div>
  <div class="field">
    <label class="field__label" for="cg-extensions">Extensions</label>
    <input class="input" id="cg-extensions" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" placeholder="/mnt/tank/apps/solo/extensions" aria-describedby="cg-extensions-help cg-extensions-error">
    <span class="field__help" id="cg-extensions-help">Extension packages. Solo writes here when you install an update from Admin settings.</span>
    <span class="field__error" id="cg-extensions-error" hidden></span>
  </div>
</div>

## Cloudflare Tunnel

A tunnel publishes Solo at a public address with no open port on your router. Cloudflare gives the address its TLS certificate.

<div class="generator">
  <label class="check">
    <input type="checkbox" id="cg-tunnel">
    <span>Add a Cloudflare Tunnel</span>
  </label>
  <div id="cg-tunnel-fields" hidden>
    <ol class="generator__steps">
      <li>In the Cloudflare dashboard, open <strong>Zero Trust</strong> &gt; <strong>Networks</strong> &gt; <strong>Tunnels</strong>.</li>
      <li>Create a tunnel of the type <strong>Cloudflared</strong>, and select <strong>Docker</strong> as the environment.</li>
      <li>Copy the token from the command that Cloudflare shows. It is the long value after <code>--token</code>.</li>
      <li>Add a public hostname. Set the service type to <strong>HTTP</strong> and the URL to <code>solo:8080</code>.</li>
    </ol>
    <div class="field">
      <label class="field__label" for="cg-tunnel-token">Tunnel token</label>
      <input class="input" id="cg-tunnel-token" type="text" autocomplete="off" spellcheck="false" autocapitalize="off" aria-describedby="cg-tunnel-token-help">
      <span class="field__help" id="cg-tunnel-token-help">The tunnel container uses this token to connect to Cloudflare.</span>
    </div>
    <label class="check">
      <input type="checkbox" id="cg-tunnel-port">
      <span>Also publish the host port<span class="field__help">Not necessary for the tunnel. Select this to also open Solo on your network at the host port.</span></span>
    </label>
  </div>
</div>

## Your Compose file

<div class="generator">
  <div class="generator__issues" id="cg-issues" role="status" hidden>
    <strong>Complete these values before the first start:</strong>
    <ul id="cg-issue-list"></ul>
  </div>
  <div class="highlighter-rouge"><div class="highlight"><pre class="highlight"><code id="cg-output"></code></pre></div></div>
  <p><button class="button button--filled" type="button" id="cg-download"><svg class="icon" aria-hidden="true" focusable="false"><use href="#i-download"/></svg> Download docker-compose.yml</button></p>
</div>

> The file holds your passwords and your tunnel token. Keep it private. Do not add it to a public repository.
{: .danger}

## Next steps

1. Save the file as `docker-compose.yml` in an empty directory on the host.
2. If you entered directories for themes or extensions, make them and give them to the user of the container, UID `1654`.
   <div id="cg-chown-block" hidden><p>Run this in the directory of the Compose file:</p><div class="highlighter-rouge"><div class="highlight"><pre class="highlight"><code id="cg-chown"></code></pre></div></div></div>
3. Start Solo:

   ```bash
   docker compose up -d
   ```
4. Open the address of the instance and complete the [setup wizard](install.md#5-complete-the-setup-wizard) with the setup user name and password from above.

<script src="{{ '/assets/js/compose-generator.js' | relative_url }}" defer></script>
