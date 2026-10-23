function connectionError(error, platform, appName) {
    const message = `Could not reach the server: ${error.message}`;
    if (platform !== 'darwin' || !error.message?.includes('ERR_ADDRESS_UNREACHABLE')) return message;
    return `${message}. For a local instance, allow ${appName} in System Settings → Privacy & Security → Local Network, then try again. If macOS is asking for access, answer that prompt first.`;
}

module.exports = { connectionError };
