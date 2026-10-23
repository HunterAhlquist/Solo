### Pending device checks

Automated tests cover the server, the relay over the real hub, the browser module's sync, link and zip logic, and the Blazor parts. These need a person with the device:

- [ ] iPad + Apple Pencil: pressure ramps the line from the chosen size toward the thickest; a finger resting on the screen while drawing doesn't draw (palm rejection) and pans instead.
- [ ] iPad: Scribble in a note's text, and in a sketch's text box.
- [ ] iPad/iPhone Safari: "Insert images" and "Link a task" insert at the cursor; a pasted photo uploads as WebP or JPEG.
- [ ] iPad/iPhone: background the app mid-edit, come back, and the note resyncs without losing typing (status goes Offline → Connecting… → Saved).
- [ ] Two people in one note: each sees the other's cursor in their color, and Markdown symbols only under their own cursor.
- [ ] A Read member sees writers' cursors, can't type, and shows as "viewing".
- [ ] Desktop app (Electron): images show (blob: URLs), "Export for Obsidian" and "Export PNG" downloads save.
- [ ] Obsidian round trip: import a real vault zip, export it, and open the export in Obsidian.
- [ ] Sketch boil looks subtle at 72 ppi on a Retina screen; reduced motion holds it still.
- [ ] TrueNAS: deploy as its own app (not `solo`, not `solo-preview`) on a cloned dataset, its own hostname and an unused port, with this release's zip as the only Design Docs package in its extensions directory.
