// Served by the API at GET /desktop-auth?state={flow} (DesktopAuthController)
// for the desktop shell's browser sign-in. Runs the ordinary GIS flow on this
// instance's own origin, then hands the credential to the complete endpoint,
// which pins the verified user to the flow the shell is polling.
//
// A file rather than an inline <script> because the instance's CSP allows
// script-src 'self' with no 'unsafe-inline' (see SecurityHeaders.cs) — inline
// here would be silently blocked, leaving a page with no sign-in button.
const status = document.getElementById('status');
const lead = document.getElementById('lead');
const state = new URLSearchParams(location.search).get('state');

function fail(message) {
  status.textContent = message;
  status.className = 'error';
}

async function complete(credential) {
  // Relative, like the client's own calls, so a sub-path instance works.
  const response = await fetch('api/auth/desktop/complete', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${credential}`,
    },
    body: JSON.stringify({ state }),
  });

  if (response.ok) {
    lead.textContent = 'You’re signed in.';
    document.getElementById('button').remove();
    status.textContent = 'Return to the Solo app — this tab can be closed.';
    status.className = 'ok';
  } else if (response.status === 404) {
    fail('This sign-in link has expired. Go back to Solo and try again.');
  } else {
    fail('Something went wrong handing the sign-in to the app. Try again from Solo.');
  }
}

async function run() {
  if (!state) {
    fail('Missing sign-in state. Start the sign-in from the Solo app.');
    return;
  }

  const config = await fetch('api/config/auth').then(r => r.json())
      .catch(() => null);
  if (!config?.clientId || config.provider?.toLowerCase() !== 'google') {
    fail('This Solo instance is not configured for Google sign-in.');
    return;
  }

  const script = document.createElement('script');
  script.src = 'https://accounts.google.com/gsi/client';
  script.onload = () => {
    google.accounts.id.initialize({
      client_id: config.clientId,
      callback: response => complete(response.credential),
      auto_select: true,
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
    // With a live Google session, One Tap can finish this without a click.
    try { google.accounts.id.prompt(); } catch { /* button remains */ }
  };
  script.onerror = () => fail('Couldn’t load Google sign-in. Check your connection.');
  document.head.appendChild(script);
}

run();
