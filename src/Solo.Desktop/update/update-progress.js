'use strict';

// Figures arrive from main (main/update-progress.js) as electron-updater
// reports them: { name, version, transferred, total, bytesPerSecond, percent }.
const heading = document.getElementById('heading');
const track = document.querySelector('.track');
const fill = document.querySelector('.fill');
const status = document.getElementById('status');
const cancel = document.getElementById('cancel');

cancel.addEventListener('click', () => {
    cancel.disabled = true;
    window.soloUpdate.cancel();
});

window.soloUpdate.onState(state => {
    heading.textContent = `Downloading ${state.name} ${state.version}…`;

    if (!(state.total > 0)) {
        return;
    }

    const percent = Math.min(100, Math.max(0, state.percent));
    track.classList.remove('indeterminate');
    track.setAttribute('aria-valuenow', String(Math.round(percent)));
    fill.style.width = `${percent}%`;

    // Past the last byte the file is still being verified and, on macOS, handed
    // to Squirrel. There is nothing left for Cancel to stop.
    if (percent >= 100) {
        status.textContent = 'Finishing up…';
        cancel.disabled = true;
        return;
    }

    const parts = [`${megabytes(state.transferred)} of ${megabytes(state.total)} MB`];
    if (state.bytesPerSecond > 0) {
        parts.push(speed(state.bytesPerSecond));
        parts.push(timeLeft((state.total - state.transferred) / state.bytesPerSecond));
    }
    status.textContent = parts.join(' · ');
});

function megabytes(bytes) {
    return (bytes / 1024 / 1024).toFixed(1);
}

function speed(bytesPerSecond) {
    return bytesPerSecond >= 1024 * 1024
        ? `${megabytes(bytesPerSecond)} MB/s`
        : `${Math.round(bytesPerSecond / 1024)} KB/s`;
}

function timeLeft(seconds) {
    if (seconds < 60) {
        return `${Math.max(1, Math.round(seconds))} s left`;
    }

    return `${Math.round(seconds / 60)} min left`;
}
