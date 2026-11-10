// Served by the API at GET /desktop-auth?state={flow} (DesktopAuthController)
// for the desktop shell's and the iPad app's browser sign-in. Signs in with the
// instance's identity provider on this instance's own origin, then hands the ID
// token to the complete endpoint. The endpoint returns a one-time authorization
// code to the desktop process's loopback listener, or to the iPad app's URL
// scheme; PKCE binds its redemption to the process that started the flow.
//
// Two ways to reach Google:
//  - By default, Google's button, whose sign-in opens in a popup. A popup
//    needs a click, so this page waits for one.
//  - With Auth:DesktopRedirect, straight to Google's own sign-in page (an
//    OpenID Connect request), with no click and no second window. Google posts
//    the ID token and the flow's state back to /desktop-auth, which serves
//    this page again with both in meta tags.
// Every other provider (Microsoft, generic OIDC) gets the authorization code
// flow with PKCE, as the web client does: straight to the provider, back here
// with ?code=…&state=…, and the code traded for an ID token at the provider's
// token endpoint from this page. No client secret: Google is only different
// because its web clients demand one there.
// Whichever way, the token's nonce is the flow's state, which complete checks.
//
// The page wears the instance's theme, like the web sign-in, in the palette
// for the system's light or dark setting: the app's own choice is stored on
// another origin, out of reach here.
//
// A file rather than an inline <script> because the instance's CSP allows
// script-src 'self' with no 'unsafe-inline' (see SecurityHeaders.cs) — inline
// here would be silently blocked, leaving a page with no sign-in button.
const status = document.getElementById('status');
const lead = document.getElementById('lead');
const meta = name => document.querySelector(`meta[name="${name}"]`)?.content;
const query = new URLSearchParams(location.search);
const returnedCredential = meta('solo-credential');
const returnedError = meta('solo-error');
const state = meta('solo-state') ?? query.get('state');
// Back from a code-flow provider: the code, or the provider's refusal.
const returnedCode = query.get('code');
const returnedProviderError = query.get('error');

// The theme's CSS families, quoted where a name needs it: generic families
// (sans-serif, system-ui) and already-quoted names go through unchanged.
function fontStack(families) {
  return families
    .filter(name => typeof name === 'string' && name)
    .map(name => /^[\w-]+$/.test(name) || /^["']/.test(name) ? name : `"${name}"`)
    .join(', ');
}

const backgroundSizes = { contain: 'contain', width: '100% auto', height: 'auto 100%', stretch: '100% 100%' };

async function applyTheme() {
  const theme = await fetch('api/themes/active').then(r => (r.ok ? r.json() : null)).catch(() => null);
  if (!theme) {
    return;
  }

  const dark = matchMedia('(prefers-color-scheme: dark)').matches;
  const palette = (dark ? theme.dark : theme.light) ?? theme.dark ?? theme.light ?? {};
  const root = document.documentElement.style;
  const set = (name, value) => {
    if (typeof value === 'string' && value) {
      root.setProperty(name, value);
    }
  };
  // The theme's paths are relative to the instance; url() needs them absolute.
  const absolute = path => new URL(path, location.href).href;

  document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
  set('--bg', palette.background);
  set('--surface', palette.surface);
  set('--fg', palette.textPrimary);
  set('--dim', palette.textSecondary);
  set('--accent', palette.primary);
  set('--error', palette.error);
  set('--ok', palette.success);
  set('--radius', theme.layout?.borderRadius);

  if (theme.fontStylesheet) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = absolute(theme.fontStylesheet);
    document.head.appendChild(link);
  }
  if (theme.typography?.fontFamily?.length) {
    set('--font', fontStack(theme.typography.fontFamily));
  }
  if (theme.typography?.headingFontFamily?.length) {
    set('--heading-font', fontStack(theme.typography.headingFontFamily));
  }
  set('--heading-weight', theme.typography?.headingWeight);

  const background = theme.login?.background;
  const image = dark ? background?.image : background?.imageLight ?? background?.image;
  if (image) {
    set('--backdrop-image', `url("${absolute(image)}")`);
    set('--backdrop-size', backgroundSizes[String(background.fit).toLowerCase()] ?? 'cover');
    set('--backdrop-position', background.position);
    set('--backdrop-scrim', String(background.scrim ?? 0));
  }
  set('--backdrop-color', background?.color);

  const logo = dark ? theme.login?.logo : theme.login?.logoLight ?? theme.login?.logo;
  if (logo) {
    const img = document.getElementById('logo');
    img.src = absolute(logo);
    img.hidden = false;
    document.querySelector('h1').hidden = true;
  }
}

// Relative, like the client's own calls, so a sub-path instance works.
const configLoad = fetch('api/config/auth').then(r => r.json()).catch(() => null);

// The instance's own name, where it has one. Somebody adding a second
// instance to the desktop app lands here, and should see which one it is.
const named = configLoad.then(config => {
  if (typeof config?.displayName === 'string' && config.displayName) {
    document.querySelector('h1').textContent = config.displayName;
    document.title = `Sign in to ${config.displayName}`;
  }
});

// Shown once themed and named — or after a second regardless, so a slow or
// broken theme never leaves the page blank.
const themed = Promise.race([
  Promise.all([applyTheme(), named]),
  new Promise(resolve => setTimeout(resolve, 1000)),
])
  .catch(() => {})
  .finally(() => document.documentElement.classList.add('ready'));

function fail(message) {
  status.textContent = message;
  status.className = 'error';
}

async function complete(credential) {
  const response = await fetch('api/auth/desktop/complete', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${credential}`,
    },
    body: JSON.stringify({ state }),
  });

  if (response.ok) {
    const result = await response.json().catch(() => null);
    try {
      const callback = new URL(result?.callbackUri);
      const loopback = callback.protocol === 'http:' && callback.hostname === '127.0.0.1' &&
          callback.pathname === '/desktop-auth/callback';
      // The iPad app's sign-in sheet closes when the page reaches its scheme.
      const app = callback.protocol === 'com.hunterahlquist.solo:' &&
          callback.pathname === '/auth/callback';
      if (!loopback && !app) {
        throw new Error('Invalid app callback.');
      }

      lead.textContent = app
          ? 'You’re signed in. Returning to Solo…'
          : 'You’re signed in. Returning to Solo Desktop…';
      document.getElementById('button').remove();
      location.replace(callback.href);
    } catch {
      fail('The instance returned an invalid desktop callback. Update the server and try again.');
    }
  } else if (response.status === 404) {
    fail('This sign-in link has expired. Go back to Solo and try again.');
  } else {
    fail('Something went wrong handing the sign-in to the app. Try again from Solo.');
  }
}

// Google's OpenID Connect sign-in, returning to this page's own address. That
// address must be an authorized redirect URI, which is why this is opt-in.
function redirectToGoogle(clientId) {
  const authorize = new URL('https://accounts.google.com/o/oauth2/v2/auth');
  authorize.search = new URLSearchParams({
    client_id: clientId,
    redirect_uri: new URL('desktop-auth', location.href).href,
    response_type: 'id_token',
    response_mode: 'form_post',
    scope: 'openid email profile',
    nonce: state,
    state,
  }).toString();
  location.replace(authorize.href);
}

function renderGoogleButton(clientId) {
  const script = document.createElement('script');
  script.src = 'https://accounts.google.com/gsi/client';
  script.onload = () => {
    google.accounts.id.initialize({
      client_id: clientId,
      callback: response => complete(response.credential),
      nonce: state,
      auto_select: false,
      use_fedcm_for_prompt: true,
      cancel_on_tap_outside: false,
    });
    google.accounts.id.renderButton(document.getElementById('button'), {
      type: 'standard',
      theme: 'filled_black',
      size: 'large',
      text: 'continue_with',
      shape: 'pill',
      logo_alignment: 'left',
      width: 280,
    });
  };
  script.onerror = () => fail('Couldn’t load Google sign-in. Check your connection.');
  document.head.appendChild(script);
}

// What to call the provider in this page's messages.
function providerName(config) {
  switch (config?.provider?.toLowerCase()) {
    case 'google': return 'Google';
    case 'microsoft': return 'Microsoft';
    default: return 'Your identity provider';
  }
}

// The PKCE verifier waits out the round trip to the provider in session
// storage, which a same-tab navigation keeps and nothing cross-origin reads.
// Keyed by the flow, so two sign-ins in one tab don't trade verifiers.
const verifierKey = `solo.desktop-auth.verifier.${state}`;

const redirectUri = () => new URL('desktop-auth', location.href).href;

function base64Url(bytes) {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// The provider's own endpoints, from its discovery document. Fetching it from
// here needs the provider's origin in the instance CSP's connect-src, which
// SecurityHeaders adds for any provider but Google.
async function providerMetadata(authority) {
  const response = await fetch(`${authority.replace(/\/+$/, '')}/.well-known/openid-configuration`);
  if (!response.ok) {
    throw new Error(`discovery answered ${response.status}`);
  }
  const metadata = await response.json();
  if (!metadata?.authorization_endpoint || !metadata?.token_endpoint) {
    throw new Error('discovery is missing an endpoint');
  }
  return metadata;
}

async function redirectToProvider(config) {
  // crypto.subtle exists in secure contexts only: an instance on plain http
  // somewhere other than localhost has no way to make the PKCE challenge.
  if (!crypto?.subtle) {
    fail('Sign-in needs this instance on https. Ask its administrator.');
    return;
  }

  let metadata;
  try {
    metadata = await providerMetadata(config.authority);
  } catch {
    fail(`Couldn’t reach ${providerName(config)}. Check your connection and try again from Solo.`);
    return;
  }

  const verifier = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const challenge = base64Url(new Uint8Array(
    await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
  sessionStorage.setItem(verifierKey, verifier);

  // Added to, not replacing, the endpoint's own query: some providers put a
  // tenant or policy there.
  const authorize = new URL(metadata.authorization_endpoint);
  for (const [name, value] of Object.entries({
    client_id: config.clientId,
    redirect_uri: redirectUri(),
    response_type: 'code',
    scope: 'openid email profile',
    code_challenge: challenge,
    code_challenge_method: 'S256',
    nonce: state,
    state,
  })) {
    authorize.searchParams.set(name, value);
  }
  location.replace(authorize.href);
}

async function redeemCode(config) {
  // The code is single-use and worthless without the verifier, but it has no
  // business staying in the address bar or the history either.
  history.replaceState(null, '', `${location.pathname}?state=${encodeURIComponent(state)}`);

  const verifier = sessionStorage.getItem(verifierKey);
  sessionStorage.removeItem(verifierKey);
  if (!verifier) {
    fail('This sign-in started in another window. Go back to Solo and try again.');
    return;
  }

  const name = providerName(config);
  let tokens = null;
  try {
    const metadata = await providerMetadata(config.authority);
    const response = await fetch(metadata.token_endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        grant_type: 'authorization_code',
        client_id: config.clientId,
        code: returnedCode,
        redirect_uri: redirectUri(),
        code_verifier: verifier,
      }),
    });
    tokens = response.ok ? await response.json() : null;
  } catch {
    tokens = null;
  }

  if (!tokens?.id_token) {
    fail(`${name} didn’t finish the sign-in. Go back to Solo and try again.`);
    return;
  }
  await complete(tokens.id_token);
}

async function run() {
  if (returnedError) {
    fail('Google didn’t complete the sign-in. Go back to Solo and try again.');
    return;
  }
  if (!state) {
    fail('Missing sign-in state. Start the sign-in from the Solo app.');
    return;
  }

  // Back from Google: nothing left to click. The theme loads alongside, rather
  // than holding up the sign-in.
  if (returnedCredential) {
    lead.textContent = 'Finishing sign-in…';
    await complete(returnedCredential);
    return;
  }

  const [config, options] = await Promise.all([
    configLoad,
    fetch('api/auth/desktop/options').then(r => r.json()).catch(() => null),
    themed,
  ]);

  if (!config?.clientId || !config.authority || !config.provider) {
    fail('This Solo instance has no sign-in set up yet. Ask its administrator.');
    return;
  }

  if (config.provider.toLowerCase() !== 'google') {
    const name = providerName(config);
    if (returnedProviderError) {
      sessionStorage.removeItem(verifierKey);
      fail(`${name} didn’t complete the sign-in. Go back to Solo and try again.`);
    } else if (returnedCode) {
      lead.textContent = 'Finishing sign-in…';
      await redeemCode(config);
    } else {
      lead.textContent = `Opening ${name === 'Your identity provider' ? 'sign-in' : `${name} sign-in`}…`;
      await redirectToProvider(config);
    }
    return;
  }

  if (options?.redirect) {
    lead.textContent = 'Opening Google sign-in…';
    redirectToGoogle(config.clientId);
  } else {
    renderGoogleButton(config.clientId);
  }
}

run();
