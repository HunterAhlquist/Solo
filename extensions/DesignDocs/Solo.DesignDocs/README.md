# Solo.DesignDocs

Design Docs, packaged as a Solo extension. A vault is an Obsidian-style folder
of markdown notes inside a project. Everyone with a note open edits it together
live, sees each other's cursors, links notes to tasks and tasks to notes, drops
images in, and draws pixel-style sketches with a pen or a finger. A vault comes
in from an Obsidian zip and goes back out as one.

Everything the feature needs is in this project, its client half
([Solo.DesignDocs.Client](../Solo.DesignDocs.Client)) and the browser module in
`Solo.DesignDocs.Client/js`. Nothing about design docs exists in `Solo.Api`,
`Solo.Data` or `Solo.Client`; core only offers the contract 1.10 seams it uses.
Delete the package and the instance loses design docs and nothing else: the
sidebar and project home lose their Design Docs sections, the task drawer its
linked notes, the description editor its **Link a doc** action, and doc links in
task descriptions render as ordinary links.

## Layout

```
extension.json      id "design-docs", schema "ext_design_docs", both assemblies
Contracts/          DTOs, limits, topics and name rules, linked into the client too
Controllers/        vaults, folders, notes, images, sketches, import, links, colors
Data/               DesignDocsDbContext and the per-document update log
Live/               the relay: ILiveMessageHandler for document topics, seed
                    leases, per-document locks, part reassembly
Migrations/         in schema ext_design_docs
content/            built from ../Solo.DesignDocs.Client/js (not committed):
                    design-docs.js, design-docs.css, the Silkscreen font and its licence
```

## Endpoints

All under `/api/ext/design-docs`. Routes naming a project go through core's
project policies, and every id is honored only under its own project and vault;
anything else is 404. Creating, renaming, closing, reopening and deleting a
vault is for super admins, as a board's lifecycle is. Everything inside an open
vault is for the project's writers; readers read. A closed vault is read-only
for everyone.

| Route (under `projects/{projectId}/`) | Access | |
|---|---|---|
| `GET vaults`, `GET vaults/{id}/tree` | read | The list, and one vault's folders, notes, images and sketches. |
| `POST vaults`, `PUT`, `POST …/close`, `POST …/reopen`, `DELETE vaults/{id}` | super admin | Delete needs the vault closed first. |
| `POST`/`PUT`/`DELETE vaults/{id}/folders/{folderId}` | write | Create, rename or move, delete with its contents. |
| `POST`/`GET`/`PUT`/`DELETE vaults/{id}/notes/{noteId}` | write (GET: read) | `PUT` renames or moves. |
| `PUT vaults/{id}/notes/{noteId}/copy` | write | The markdown copy that search, links and export read. |
| `GET vaults/{id}/documents/{documentId}/log` | read | The update log, for export of a note nobody has open. |
| `GET vaults/{id}/notes/{noteId}/references` | read | Notes whose copy links to this one, for the rename warning. |
| `POST`/`PUT vaults/{id}/images[/{imageId}]`, `GET …/{imageId}` | write (GET: read) | PNG, JPEG, GIF, WebP up to 10 MB. SVG is refused. |
| `POST`/`PUT`/`DELETE vaults/{id}/sketches/{sketchId}` | write | |
| `POST vaults/{id}/import/notes` | write | Up to 100 notes or 5 MB per call. |
| `GET`/`POST colors` | read | The members' color slots in this project, and claiming the caller's. |

Outside a project route, with a plain `[Authorize]` and every answer scoped
through `ISoloQuery`: `GET notes/{noteId}/locate`, `POST links/labels` and
`POST tasks/lookup`.

## Decisions worth knowing before changing anything

**The server relays and stores; it never runs Yjs.** Each note is a Yjs
document whose updates go through core's hub on the topic
`design-docs:doc:{id}`. The server checks the caller's role and the vault's
state, appends each update to `DocUpdates` with a sequence number, and relays
it. A client that falls behind asks for everything after its `lastSeq`. One
client compacts the log into a snapshot once it grows. There is no .NET Yjs
port in the server, so nothing here can merge documents, and nothing has to.

**`lastSeq` only moves once synced.** A client advances it from relayed
updates and acks only after its catch-up answer has been applied. Otherwise an
update relayed before the catch-up arrives would move it past updates the
client never saw, and they'd stay missing. `js/test/provider.test.js` covers
this with a relay that drops, delays and interleaves messages.

**Long messages travel as parts in both directions.** The hub caps a message
at 48,000 characters. Anything longer is split into `part` messages of 46,000
and reassembled (`Live/PartAssembler.cs`, `js/src/sync/protocol.js`).

**The first person to open a new note seeds it.** A lease (`Live/SeedLeases.cs`)
makes sure exactly one writer turns the stored markdown into the first update;
everyone else waits for it. A reader never seeds. CRLF becomes LF on seed.

**Notes keep Obsidian's links as written.** `[[Note#Heading|Alias]]`, `![[image.png]]`
and `![](path)` resolve against the vault by Obsidian's rules: relative to the
note's folder, then the vault path, then a name found once
([Obsidian: links](https://obsidian.md/help/links)). An embedded note shows as
a link; transclusion is for later. An image shows only if it resolves inside
the vault, so a note can't pull images from anywhere else.

**Sketches are full screen.** A sketch is its own Yjs document, drawn into a
paper-sized canvas in paint pixels and stamped with a seeded brush, so every
client draws the same strokes the same way. Its embed in the note is
`![[Name.sketch]]`, exported as a PNG beside the note.

**The bundle must not need `unsafe-eval`.** Core's content security policy
forbids it, so `js/build.mjs` fails the build if the output contains `eval(` or
`new Function(`.

**State is per process.** Seed leases, document locks and part buffers live in
memory. That fits Solo's one-container deployment; a second instance would
need them in Postgres.

**A project alias of `NOTE` or `TASK` collides** with the `/docs/note/{id}` and
`/docs/task/{id}` link routes, which win. Pick another alias.

## Building the package

```bash
build/pack-contract.sh
build/pack-design-docs-extension.sh [output-directory] [label]
```

Runs the browser module's `npm ci`, tests and build, then produces
`solo-design-docs-<version>[-<label>].zip` with `extension.json`, `server/`,
`client/` and `content/`. The label only names the file: the manifest id stays
`design-docs`, so install one Design Docs zip per instance.

Tests: `npm test` in `Solo.DesignDocs.Client/js`, then
`dotnet test extensions/DesignDocs/Solo.DesignDocs.slnx` (needs Docker, and the
content built first).

## Migrations

```bash
dotnet ef migrations add <Name> --project extensions/DesignDocs/Solo.DesignDocs --startup-project extensions/DesignDocs/Solo.DesignDocs
```

Applied by the host at startup, gated on `Database__AutoMigrate`. A failed
migration disables this extension only.
