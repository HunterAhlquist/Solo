// MCP (Model Context Protocol) endpoint for the desktop shell: POST /mcp on
// the app's local origin. Lets an MCP client on this machine (Claude Code,
// Claude Desktop, …) browse projects and create/edit tasks on the configured
// Solo instance. The tools call the instance's REST API with the session token
// the signed-in client already holds (fetched from the renderer per request),
// so MCP access begins and ends with the app's sign-in state — no second
// credential store, nothing to revoke separately.
//
// Stateless by design: every POST builds a fresh server + transport pair and
// answers plain JSON (no SSE stream, no session ids). MCP clients re-send
// initialize on demand, so the endpoint survives app restarts without session
// bookkeeping, and GET — a server-push stream we don't offer — can honestly 405.
const { McpServer } = require('@modelcontextprotocol/sdk/server/mcp.js');
const { StreamableHTTPServerTransport } = require('@modelcontextprotocol/sdk/server/streamableHttp.js');
const { z } = require('zod');
const pkg = require('../package.json');

function createMcpHandler({ getTarget, getSessionToken, isEnabled }) {
    return async (req, res) => {
        // Off by default, opt-in via Server Settings, consulted per request so
        // toggling never needs a server restart. Fail closed: no getter = off.
        if (!isEnabled?.()) {
            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end('MCP is disabled. Enable it in Solo → Server Settings…');
            return;
        }

        // The server's Host check already blocks DNS rebinding; this blocks
        // the other browser vector, a cross-origin fetch from a web page.
        // Browsers always attach Origin to cross-origin POSTs; real MCP
        // clients are not browsers and send none.
        if (req.headers.origin && !isLocalOrigin(req.headers.origin)) {
            res.writeHead(403).end();
            return;
        }

        if (req.method !== 'POST') {
            respondRpcError(res, 405, -32000,
                'This MCP endpoint is stateless: POST only, no event stream.',
                { Allow: 'POST' });
            return;
        }

        let body;
        try {
            body = await readJsonBody(req, 4 * 1024 * 1024);
        } catch {
            respondRpcError(res, 400, -32700, 'Request body is not valid JSON.');
            return;
        }

        const server = buildServer({ getTarget, getSessionToken });
        const transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: undefined,
            enableJsonResponse: true,
        });
        res.on('close', () => {
            transport.close();
            server.close();
        });

        try {
            await server.connect(transport);
            await transport.handleRequest(req, res, body);
        } catch (err) {
            if (!res.headersSent) {
                respondRpcError(res, 500, -32603, `MCP request failed: ${err.message}`);
            } else {
                res.end();
            }
        }
    };
}

// ---------------------------------------------------------------------------
// Tools. Read tools walk the hierarchy (projects → boards → tasks) to find
// ids; write tools mirror the REST API. Updates are read-modify-write: the
// API's PUTs replace the whole record and demand the current version for
// optimistic concurrency, so each update fetches first and merges — callers
// only pass the fields they mean to change.
// ---------------------------------------------------------------------------

function buildServer(ctx) {
    const server = new McpServer(
        { name: 'solo', version: pkg.version },
        {
            instructions:
                'Task tools for the Solo project-management app. Hierarchy: projects → ' +
                'boards → tasks; a task may sit under an epic (same board) and carry ' +
                'sub-tasks. Discover ids with list_projects → list_boards → list_tasks. ' +
                'Every write acts as the user signed in to the Solo desktop app.',
        });

    // Wraps a handler into MCP result shape; API failures become isError text
    // results (visible to the model) rather than protocol errors.
    const tool = (name, title, description, inputSchema, handler) => {
        server.registerTool(name, { title, description, inputSchema }, async args => {
            try {
                const result = await handler(args ?? {});
                return {
                    content: [{
                        type: 'text',
                        text: result === null ? 'ok' : JSON.stringify(result, null, 2),
                    }],
                };
            } catch (err) {
                return {
                    content: [{ type: 'text', text: String(err?.message ?? err) }],
                    isError: true,
                };
            }
        });
    };

    const guid = description => z.guid().describe(description);
    const taskName = z.string().min(1).max(300).describe('Name (plain text, ≤300 chars)');
    const taskDescription = z.string().max(100_000)
        .describe('Description (Markdown, ≤100k chars)');

    tool('list_projects', 'List projects',
        'Lists every Solo project the signed-in user is a member of.',
        {},
        () => api(ctx, 'GET', '/api/projects'));

    tool('list_boards', 'List boards',
        'Lists the boards of a project.',
        { projectId: guid('Project id') },
        ({ projectId }) => api(ctx, 'GET', `/api/projects/${projectId}/boards`));

    tool('list_epics', 'List epics',
        'Lists the epics on a board. Tasks may be grouped under an epic via epicId.',
        { boardId: guid('Board id') },
        ({ boardId }) => api(ctx, 'GET', `/api/boards/${boardId}/epics`));

    tool('list_labels', 'List labels',
        'Lists a project\'s labels. A task or sub-task may carry one label (labelId); ' +
        'labels are scoped by kind (Task/SubTask) and optionally to a single board.',
        { projectId: guid('Project id') },
        ({ projectId }) => api(ctx, 'GET', `/api/projects/${projectId}/labels`));

    tool('list_tasks', 'List tasks',
        'Lists every task on a board in rank order, including status, epic, ' +
        'label and sub-tasks.',
        { boardId: guid('Board id') },
        ({ boardId }) => api(ctx, 'GET', `/api/boards/${boardId}/tasks`));

    tool('get_task', 'Get a task',
        'Fetches one task by id, including its sub-tasks.',
        { taskId: guid('Task id') },
        ({ taskId }) => api(ctx, 'GET', `/api/tasks/${taskId}`));

    tool('create_task', 'Create a task',
        'Creates a task on a board. New tasks start Unstarted and are appended ' +
        'to the end of the board.',
        {
            boardId: guid('Board to create the task on'),
            name: taskName,
            description: taskDescription.optional(),
            epicId: z.guid().optional()
                .describe('Epic on the same board; omit for an ad-hoc task'),
            labelId: z.guid().optional()
                .describe('Task label from the project (see list_labels)'),
        },
        ({ boardId, ...request }) => api(ctx, 'POST', `/api/boards/${boardId}/tasks`, request));

    tool('update_task', 'Update a task',
        'Edits a task\'s name, description and/or label. Omitted fields keep ' +
        'their current value; pass null to clear the description or label. ' +
        'Status and epic have their own tools.',
        {
            taskId: guid('Task id'),
            name: taskName.optional(),
            description: taskDescription.nullable().optional(),
            labelId: z.guid().nullable().optional().describe('Task label, or null to clear'),
        },
        async ({ taskId, name, description, labelId }) => {
            const current = await api(ctx, 'GET', `/api/tasks/${taskId}`);
            return api(ctx, 'PUT', `/api/tasks/${taskId}`, {
                name: name ?? current.name,
                description: description !== undefined ? description : current.description,
                labelId: labelId !== undefined ? labelId : current.labelId,
                version: current.version,
            });
        });

    tool('set_task_status', 'Set task status',
        'Moves a task between the Kanban columns.',
        {
            taskId: guid('Task id'),
            status: z.enum(['Unstarted', 'InProgress', 'Finished'])
                .describe('Target column'),
        },
        ({ taskId, status }) => api(ctx, 'PUT', `/api/tasks/${taskId}/status`, { status }));

    tool('set_task_epic', 'Set task epic',
        'Attaches a task to an epic on its board, or detaches it to ad-hoc.',
        {
            taskId: guid('Task id'),
            epicId: z.guid().nullable()
                .describe('Epic on the same board, or null to detach'),
        },
        ({ taskId, epicId }) => api(ctx, 'PUT', `/api/tasks/${taskId}/epic`, { epicId }));

    tool('create_subtask', 'Create a sub-task',
        'Adds a checklist sub-task to a task.',
        {
            taskId: guid('Parent task id'),
            name: taskName,
            labelId: z.guid().optional()
                .describe('Sub-task label from the project (see list_labels)'),
        },
        ({ taskId, ...request }) => api(ctx, 'POST', `/api/tasks/${taskId}/subtasks`, request));

    tool('update_subtask', 'Update a sub-task',
        'Edits a sub-task\'s name and/or label. Omitted fields keep their ' +
        'current value; pass null to clear the label.',
        {
            taskId: guid('Parent task id'),
            subTaskId: guid('Sub-task id'),
            name: taskName.optional(),
            labelId: z.guid().nullable().optional().describe('Sub-task label, or null to clear'),
        },
        async ({ taskId, subTaskId, name, labelId }) => {
            const siblings = await api(ctx, 'GET', `/api/tasks/${taskId}/subtasks`);
            const current = siblings.find(s => s.id === subTaskId);
            if (!current) {
                throw new Error('That task has no sub-task with this id.');
            }

            return api(ctx, 'PUT', `/api/subtasks/${subTaskId}`, {
                name: name ?? current.name,
                labelId: labelId !== undefined ? labelId : current.labelId,
                version: current.version,
            });
        });

    tool('set_subtask_finished', 'Finish a sub-task',
        'Checks or unchecks a sub-task.',
        {
            subTaskId: guid('Sub-task id'),
            isFinished: z.boolean().describe('true = finished, false = reopen'),
        },
        ({ subTaskId, isFinished }) =>
            api(ctx, 'PUT', `/api/subtasks/${subTaskId}/finished`, { isFinished }));

    return server;
}

// ---------------------------------------------------------------------------
// Solo REST calls. Node's fetch rather than Electron's net.fetch keeps this
// module Electron-free (testable in plain node) and matches how the shell's
// proxy already reaches the instance (Node networking, not Chromium's).
// ---------------------------------------------------------------------------

async function api({ getTarget, getSessionToken }, method, path, body) {
    const target = getTarget();
    if (!target) {
        throw new Error('No Solo instance is configured. Set one in the app: Solo → Server Settings…');
    }

    const token = await getSessionToken();
    if (!token) {
        throw new Error('Not signed in. Sign in to the Solo desktop app, then try again.');
    }

    const response = await fetch(target + path, {
        method,
        headers: {
            Authorization: `Bearer ${token}`,
            ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(15_000),
    });

    if (!response.ok) {
        throw new Error(`Solo API answered ${response.status} for ${method} ${path}${
            await problemDetail(response)}`);
    }

    return response.status === 204 ? null : response.json();
}

// ASP.NET problem+json carries the human-readable reason; surface it.
async function problemDetail(response) {
    const text = await response.text().catch(() => '');
    let detail = text;
    try {
        const problem = JSON.parse(text);
        detail = [problem.title, problem.detail].filter(Boolean).join(' — ') || text;
    } catch {
        // Not JSON — use the raw body.
    }

    return detail ? `: ${detail}` : '';
}

// ---------------------------------------------------------------------------

function isLocalOrigin(origin) {
    try {
        const { hostname } = new URL(origin);
        return hostname === 'localhost' || hostname === '127.0.0.1';
    } catch {
        return false;
    }
}

function respondRpcError(res, status, code, message, headers = {}) {
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', ...headers });
    res.end(JSON.stringify({ jsonrpc: '2.0', error: { code, message }, id: null }));
}

function readJsonBody(req, limit) {
    return new Promise((resolve, reject) => {
        const chunks = [];
        let size = 0;
        req.on('data', chunk => {
            size += chunk.length;
            if (size > limit) {
                reject(new Error('body too large'));
                req.destroy();
                return;
            }
            chunks.push(chunk);
        });
        req.on('end', () => {
            try {
                resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
            } catch (err) {
                reject(err);
            }
        });
        req.on('error', reject);
    });
}

module.exports = { createMcpHandler };
