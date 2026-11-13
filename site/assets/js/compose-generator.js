// The Compose generator on admin/compose-generator.md. Everything happens in
// this page: the page's Content-Security-Policy blocks every connection a
// script could open, and nothing is stored.
//
// The output follows deploy/docker-compose.yml. Change the two together.
(function () {
    'use strict';

    var PASSWORD_LENGTH = 32;
    // Letters and digits only: a password goes unquoted into the Npgsql
    // connection string, where ; and = would need escaping.
    var PASSWORD_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    var CONTAINER_UID = 1654;

    function $(id) {
        return document.getElementById(id);
    }

    function newPassword() {
        // A byte at or above the largest multiple of the alphabet's size is
        // thrown away, so that every character is equally likely.
        var limit = 256 - (256 % PASSWORD_ALPHABET.length);
        var bytes = new Uint8Array(PASSWORD_LENGTH * 2);
        var password = '';
        while (password.length < PASSWORD_LENGTH) {
            crypto.getRandomValues(bytes);
            for (var i = 0; i < bytes.length && password.length < PASSWORD_LENGTH; i++) {
                if (bytes[i] < limit) {
                    password += PASSWORD_ALPHABET[bytes[i] % PASSWORD_ALPHABET.length];
                }
            }
        }

        return password;
    }

    // A text field without control characters, which an <input> lets through
    // when they are pasted.
    function text(id) {
        return $(id).value.replace(/[\u0000-\u001f\u007f]/g, '').trim();
    }

    // A double-quoted YAML scalar. Compose then reads $ as the start of a
    // variable, and $$ as a literal dollar sign.
    function yamlString(value) {
        return '"' + value
            .replace(/\\/g, '\\\\')
            .replace(/"/g, '\\"')
            .replace(/\$/g, '$$$$') + '"';
    }

    function shellQuote(value) {
        return "'" + value.replace(/'/g, "'\\''") + "'";
    }

    function showError(id, message) {
        var input = $(id);
        var error = $(id + '-error');
        if (message) {
            input.setAttribute('aria-invalid', 'true');
        } else {
            input.removeAttribute('aria-invalid');
        }

        error.textContent = message || '';
        error.hidden = !message;
        return message;
    }

    // An empty field means a named Docker volume. A colon would split the
    // short volume syntax in the wrong place.
    function hostPath(id) {
        var path = text(id);
        var problem = null;
        if (path && !/^(\/|\.\.?\/)/.test(path)) {
            problem = 'Start the path with /, ./ or ../.';
        } else if (path.indexOf(':') !== -1) {
            problem = 'A path cannot contain a colon.';
        }

        showError(id, problem);
        return path;
    }

    function readForm() {
        // Each issue names the field to fix, so the list can lead to it.
        var issues = [];
        function required(id, placeholder) {
            var value = text(id);
            if (value) {
                return value;
            }

            issues.push(id);
            return placeholder;
        }

        var image = text('cg-image');
        var port = text('cg-port');
        var provider = $('cg-provider').value;
        var tunnel = $('cg-tunnel').checked;
        var authority = text('cg-authority');

        showError('cg-image', /^[a-z0-9][A-Za-z0-9._\/:@-]*$/.test(image)
            ? null
            : 'Enter an image name such as ghcr.io/hunterahlquist/solo:latest.');

        var portNumber = Number(port);
        var publishPort = !tunnel || $('cg-tunnel-port').checked;
        showError('cg-port', !publishPort || (/^\d+$/.test(port) && portNumber >= 1 && portNumber <= 65535)
            ? null
            : 'Enter a number from 1 to 65535.');

        showError('cg-authority', provider !== 'generic' || !authority || /^https:\/\//.test(authority)
            ? null
            : 'The issuer address starts with https://.');

        showError('cg-setup-username', text('cg-setup-username') ? null : 'Enter a user name.');

        if (provider === 'microsoft') {
            authority = 'https://login.microsoftonline.com/'
                + required('cg-tenant', 'CHANGE_ME_TENANT_ID') + '/v2.0';
        } else if (provider === 'generic') {
            authority = required('cg-authority', 'CHANGE_ME_ISSUER_ADDRESS');
        } else {
            authority = 'https://accounts.google.com';
        }

        var pgdata = hostPath('cg-pgdata');
        var themes = hostPath('cg-themes');
        var extensions = hostPath('cg-extensions');

        document.querySelectorAll('#cg-settings [aria-invalid="true"]').forEach(function (input) {
            issues.push(input.id);
        });

        return {
            image: image,
            port: publishPort ? port : null,
            updates: $('cg-updates').checked,
            provider: provider,
            authority: authority,
            clientId: required('cg-client-id', 'CHANGE_ME_OIDC_CLIENT_ID'),
            setupUsername: text('cg-setup-username'),
            setupPassword: $('cg-setup-password').value,
            dbPassword: $('cg-db-password').value,
            pgdata: pgdata,
            themes: themes,
            extensions: extensions,
            tunnelToken: tunnel
                ? required('cg-tunnel-token', 'CHANGE_ME_TUNNEL_TOKEN')
                : null,
            issues: issues
        };
    }

    function compose(form) {
        var namedVolumes = [];
        function volume(hostPath, name, target) {
            if (hostPath) {
                return '      - ' + yamlString(hostPath + ':' + target);
            }

            namedVolumes.push(name);
            return '      - ' + name + ':' + target;
        }

        var lines = [
            '# Solo, made with the Compose generator in the Solo docs.',
            '# This file holds passwords. Keep it private.',
            'services:',
            '  solo:',
            '    image: ' + yamlString(form.image),
            '    restart: unless-stopped',
            '    depends_on:',
            '      postgres:',
            '        condition: service_healthy'
        ];

        if (form.port) {
            lines.push(
                '    ports:',
                '      - ' + yamlString(form.port + ':8080'));
        }

        lines.push(
            '    environment:',
            '      ConnectionStrings__Default: ' + yamlString(
                'Host=postgres;Port=5432;Database=solo;Username=solo;Password=' + form.dbPassword),
            '      Auth__Authority: ' + yamlString(form.authority),
            '      Auth__ClientId: ' + yamlString(form.clientId),
            '      Auth__Provider: ' + yamlString(form.provider),
            '      Setup__Username: ' + yamlString(form.setupUsername),
            '      Setup__Password: ' + yamlString(form.setupPassword),
            '      Themes__Directory: "/data/themes"',
            '      Extensions__Directory: "/data/extensions"');

        if (!form.updates) {
            lines.push('      Updates__Enabled: "false"');
        }

        lines.push(
            '    volumes:',
            volume(form.themes, 'solo-themes', '/data/themes'),
            volume(form.extensions, 'solo-extensions', '/data/extensions'),
            '    healthcheck:',
            '      test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080"]',
            '      interval: 30s',
            '      timeout: 5s',
            '      retries: 3',
            '      start_period: 30s',
            '');

        if (form.tunnelToken) {
            lines.push(
                '  cloudflared:',
                '    image: cloudflare/cloudflared:latest',
                '    restart: unless-stopped',
                '    command: tunnel --no-autoupdate run',
                '    environment:',
                '      TUNNEL_TOKEN: ' + yamlString(form.tunnelToken),
                '    depends_on:',
                '      - solo',
                '');
        }

        lines.push(
            '  postgres:',
            '    image: postgres:17',
            '    restart: unless-stopped',
            '    environment:',
            '      POSTGRES_DB: solo',
            '      POSTGRES_USER: solo',
            '      POSTGRES_PASSWORD: ' + yamlString(form.dbPassword),
            '    volumes:',
            volume(form.pgdata, 'solo-pgdata', '/var/lib/postgresql/data'),
            '    healthcheck:',
            '      test: ["CMD-SHELL", "pg_isready -U solo -d solo"]',
            '      interval: 10s',
            '      timeout: 5s',
            '      retries: 5');

        if (namedVolumes.length) {
            lines.push('', 'volumes:');
            namedVolumes.forEach(function (name) {
                lines.push('  ' + name + ':');
            });
        }

        return lines.join('\n') + '\n';
    }

    // The packages directories need the container's user. Postgres sets up
    // its own directory, so it is not in the list.
    function chownCommands(form) {
        var paths = [form.themes, form.extensions].filter(Boolean).map(shellQuote);
        if (!paths.length) {
            return '';
        }

        return 'mkdir -p ' + paths.join(' ') + '\n'
            + 'sudo chown ' + CONTAINER_UID + ':' + CONTAINER_UID + ' ' + paths.join(' ');
    }

    function render() {
        var provider = $('cg-provider').value;
        $('cg-tenant-field').hidden = provider !== 'microsoft';
        $('cg-authority-field').hidden = provider !== 'generic';
        $('cg-tunnel-fields').hidden = !$('cg-tunnel').checked;

        var form = readForm();
        $('cg-output').textContent = compose(form);

        var list = $('cg-issue-list');
        list.replaceChildren();
        tabs.forEach(function (tab) { delete tab.dataset.attention; });
        form.issues.forEach(function (id) {
            var tab = tabOf(id);
            tab.dataset.attention = '';

            var link = document.createElement('button');
            link.type = 'button';
            link.className = 'link-button';
            link.textContent = document.querySelector('label[for="' + id + '"]').textContent
                + ' (' + tab.textContent + ')';
            link.addEventListener('click', function () {
                selectTab(tab);
                $(id).focus();
            });

            var item = document.createElement('li');
            item.appendChild(link);
            list.appendChild(item);
        });
        $('cg-issues').hidden = !form.issues.length;

        var chown = chownCommands(form);
        $('cg-chown').textContent = chown;
        $('cg-chown-block').hidden = !chown;
    }

    function download() {
        var file = new Blob([$('cg-output').textContent], { type: 'application/yaml' });
        var link = document.createElement('a');
        link.href = URL.createObjectURL(file);
        link.download = 'docker-compose.yml';
        link.click();
        window.setTimeout(function () { URL.revokeObjectURL(link.href); }, 0);
    }

    // Tabs: the WAI-ARIA pattern, with the arrow keys moving between them.
    // Without this script every panel shows and the tab list stays hidden.
    var tabs = Array.prototype.slice.call(document.querySelectorAll('#cg-settings [role="tab"]'));

    function tabOf(fieldId) {
        var panel = $(fieldId).closest('[role="tabpanel"]');
        return $(panel.getAttribute('aria-labelledby'));
    }

    function selectTab(selected) {
        tabs.forEach(function (tab) {
            var isSelected = tab === selected;
            tab.setAttribute('aria-selected', String(isSelected));
            tab.tabIndex = isSelected ? 0 : -1;
            $(tab.getAttribute('aria-controls')).hidden = !isSelected;
        });
    }

    tabs.forEach(function (tab, index) {
        tab.addEventListener('click', function () { selectTab(tab); });
        tab.addEventListener('keydown', function (event) {
            var step = { ArrowRight: 1, ArrowLeft: -1 }[event.key];
            var target = event.key === 'Home' ? tabs[0]
                : event.key === 'End' ? tabs[tabs.length - 1]
                : step ? tabs[(index + step + tabs.length) % tabs.length]
                : null;
            if (target) {
                event.preventDefault();
                selectTab(target);
                target.focus();
            }
        });
    });

    document.querySelector('#cg-settings [role="tablist"]').hidden = false;
    selectTab(tabs[0]);

    $('cg-setup-password').value = newPassword();
    $('cg-db-password').value = newPassword();

    document.querySelectorAll('[data-regenerate]').forEach(function (button) {
        button.addEventListener('click', function () {
            $(button.dataset.regenerate).value = newPassword();
            render();
        });
    });

    document.querySelectorAll('.generator').forEach(function (section) {
        section.addEventListener('input', render);
        section.addEventListener('change', render);
    });

    $('cg-download').addEventListener('click', download);

    // The clipboard API exists only on a secure page, which Pages always is.
    if (navigator.clipboard) {
        var copy = $('cg-copy');
        copy.hidden = false;
        copy.addEventListener('click', function () {
            navigator.clipboard.writeText($('cg-output').textContent).then(function () {
                copy.querySelector('span').textContent = 'Copied';
                copy.querySelector('use').setAttribute('href', '#i-check');
                window.setTimeout(function () {
                    copy.querySelector('span').textContent = 'Copy';
                    copy.querySelector('use').setAttribute('href', '#i-content-copy');
                }, 1500);
            });
        });
    }

    render();
})();
