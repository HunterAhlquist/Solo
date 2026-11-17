#if os(macOS)
import AppKit
import SwiftUI

// The note editor on a Mac. The same shared document, the same styling of it (`NoteStyler`,
// `NoteLayoutManager`) and the same rows of pictures as on an iPhone, around AppKit's text
// view instead of UIKit's. What differs is what a Mac does differently: the text view sits
// in a scroll view and is not one, focus is a first responder and not a keyboard, and a click
// can tell a link from the text around it without first asking whether the note is being typed in.

/// What the buttons around the editor ask of it. The text view is AppKit's, so the formatting
/// commands reach it through this rather than through state.
final class NoteEditorProxy {
    fileprivate weak var coordinator: SyncedTextEditor.Coordinator?

    var hasSelection: Bool { (coordinator?.textView?.selectedRange().length ?? 0) > 0 }

    func focus() {
        guard let view = coordinator?.textView else { return }
        view.window?.makeFirstResponder(view)
    }

    func dismissKeyboard() {
        guard let view = coordinator?.textView, view.window?.firstResponder === view else { return }
        view.window?.makeFirstResponder(nil)
    }

    /// Puts markers around the selection, leaving it selected between them.
    func wrap(_ prefix: String, _ suffix: String) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange()
        let selected = (view.string as NSString).substring(with: range)
        replace(range, with: prefix + selected + suffix)
        view.setSelectedRange(NSRange(location: range.location + prefix.utf16.count, length: range.length))
    }

    /// Starts the caret's line with a marker.
    func prefixLine(_ prefix: String) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange()
        let line = (view.string as NSString).lineRange(for: NSRange(location: range.location, length: 0))
        replace(NSRange(location: line.location, length: 0), with: prefix)
        view.setSelectedRange(NSRange(location: range.location + prefix.utf16.count, length: range.length))
    }

    /// Replaces the selection. `ownLine` gives an embed a line to itself.
    func insert(_ text: String, ownLine: Bool = false) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange()
        var snippet = text
        if ownLine {
            let whole = view.string as NSString
            if range.location > 0, whole.character(at: range.location - 1) != 10 { snippet = "\n" + snippet }
            if NSMaxRange(range) >= whole.length || whole.character(at: NSMaxRange(range)) != 10 { snippet += "\n" }
        }
        replace(range, with: snippet)
        view.setSelectedRange(NSRange(location: range.location + snippet.utf16.count, length: 0))
    }

    /// Brings a line of the note — counted from zero — to the top of the view.
    func scroll(toLine line: Int) {
        guard let view = coordinator?.textView, let manager = view.layoutManager, let scroll = view.enclosingScrollView else { return }
        let text = view.string as NSString
        var offset = 0, remaining = line
        while remaining > 0, offset < text.length {
            offset = NSMaxRange(text.lineRange(for: NSRange(location: offset, length: 0)))
            remaining -= 1
        }
        guard offset < text.length else { return }
        manager.ensureLayout(forCharacterRange: NSRange(location: 0, length: offset + 1))
        let row = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: offset), effectiveRange: nil)
        let top = view.clamped(row.minY + view.insets.top - 12 - scroll.contentInsets.top)
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.3
            scroll.contentView.animator().setBoundsOrigin(NSPoint(x: 0, y: top))
        }
        scroll.reflectScrolledClipView(scroll.contentView)
    }

    private func replace(_ range: NSRange, with text: String) {
        guard let view = coordinator?.textView, view.isEditable else { return }
        // Through the input system rather than the storage, so Undo takes it back.
        view.insertText(text, replacementRange: range)
        coordinator?.changedLocally()
    }
}

/// A Markdown editor whose text is a shared document: see the iPhone's, in `DocEditor.swift`,
/// which this is the Mac's half of.
struct SyncedTextEditor: NSViewRepresentable {
    var sync: DocSync
    var proxy: NoteEditorProxy
    /// Read so the view updates when either changes.
    var revision: Int
    var editable: Bool
    /// What to show until the shared document arrives: the note's last saved copy.
    var fallback: String
    var accent: PlatformColor
    var media: NoteEditorMedia
    /// Changes when what the rows of pictures show may have: an upload, a deletion, who may write.
    var mediaRevision: Int
    /// Read so the view updates when anyone else's caret moves.
    var presence: Int = 0
    /// What stands above the text and scrolls away with it: the note's name and state.
    var header: AnyView
    /// What the window's bar and the format strip cover. The view runs under them, so the
    /// text passes behind their glass, and is inset by this much so it can be scrolled clear.
    var insets: EdgeInsets
    /// A link clicked.
    var onLink: (String) -> Void = { _ in }
    var onFocus: (Bool) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(sync: sync, media: media) }

    func makeNSView(context: Context) -> NSScrollView {
        // TextKit 1, for a layout manager that can draw behind lines.
        let storage = NSTextStorage()
        let manager = NoteLayoutManager()
        let container = NSTextContainer(size: CGSize(width: 0, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        manager.addTextContainer(container)
        storage.addLayoutManager(manager)

        let view = NoteTextView(frame: .zero, textContainer: container)
        // Every line's place is worked out from the top, never guessed: a row of pictures is a
        // view placed over its line, which an estimate puts in the wrong place.
        manager.allowsNonContiguousLayout = false
        manager.delegate = context.coordinator
        view.delegate = context.coordinator
        view.drawsBackground = false
        view.isRichText = false
        view.importsGraphics = false
        view.allowsUndo = true
        view.usesFindBar = true
        view.isIncrementalSearchingEnabled = true
        view.isVerticallyResizable = true
        view.isHorizontallyResizable = false
        view.autoresizingMask = [.width]
        view.minSize = .zero
        view.maxSize = CGSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        // Markdown is punctuation: `--` and straight quotes have to stay what was typed.
        view.isAutomaticDashSubstitutionEnabled = false
        view.isAutomaticQuoteSubstitutionEnabled = false
        view.isAutomaticTextReplacementEnabled = false
        view.isAutomaticLinkDetectionEnabled = false
        view.setAccessibilityLabel("Note text")
        view.isEditable = editable

        view.placeholder.font = .writing
        view.placeholder.textColor = .tertiaryLabel
        view.placeholder.isHidden = true
        view.addSubview(view.placeholder)

        let scroll = NSScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        // The insets are handed in; left to itself the view would work out its own.
        scroll.automaticallyAdjustsContentInsets = false
        scroll.documentView = view
        scroll.contentView.postsBoundsChangedNotifications = true

        let coordinator = context.coordinator
        coordinator.textView = view
        coordinator.onFocus = onFocus
        coordinator.onLink = onLink
        coordinator.fallback = fallback
        coordinator.setAccent(accent)
        coordinator.mediaRevision = mediaRevision
        view.typingAttributes = coordinator.styler.base
        view.onWidthChange = { [weak coordinator] in
            coordinator?.layoutChrome()
            coordinator?.restyle()
        }
        view.onFocus = { [weak coordinator] in coordinator?.focused($0) }
        view.linkAt = { [weak coordinator] in coordinator?.link(at: $0) }
        view.tickAt = { [weak coordinator] in coordinator?.tick(at: $0) ?? false }
        view.onLink = { [weak coordinator] in coordinator?.onLink($0) }
        NotificationCenter.default.addObserver(coordinator, selector: #selector(Coordinator.scrolled),
                                               name: NSView.boundsDidChangeNotification, object: scroll.contentView)
        proxy.coordinator = coordinator
        coordinator.setInsets(insets)
        coordinator.setHeader(header)
        coordinator.applyRemote()
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        let coordinator = context.coordinator
        guard let view = coordinator.textView else { return }
        coordinator.onFocus = onFocus
        coordinator.onLink = onLink
        coordinator.fallback = fallback
        coordinator.media = media
        proxy.coordinator = coordinator
        coordinator.setInsets(insets)
        var stale = coordinator.setAccent(accent)
        if coordinator.setHeader(header) { stale = true }
        if coordinator.mediaRevision != mediaRevision {
            coordinator.mediaRevision = mediaRevision
            coordinator.forgetMedia()
            stale = true
        }
        // The text first, then whether it can be typed in: it only becomes
        // editable once it is the shared document's own text.
        let changed = coordinator.applyRemote()
        if view.isEditable != editable {
            view.isEditable = editable
            stale = true
        }
        if !changed, stale {
            coordinator.restyle()
        } else if !changed {
            // Nothing of the note's own changed: this pass is for somebody's caret.
            coordinator.placeCarets()
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate, NSLayoutManagerDelegate {
        let sync: DocSync
        let styler = NoteStyler()
        var media: NoteEditorMedia
        var mediaRevision = 0
        var fallback = ""
        weak var textView: NoteTextView?
        var onFocus: (Bool) -> Void = { _ in }
        var onLink: (String) -> Void = { _ in }
        /// The text as the shared document and the view last agreed on it.
        private var known: [UInt16] = []
        /// The last text parsed, so moving the caret doesn't parse it again.
        private var parsed: (text: String, markdown: LiveMarkdown)?
        private var mediaHosts: [String: NSHostingController<AnyView>] = [:]
        private var headerHost: NSHostingController<AnyView>?
        private var caretViews: [UInt64: PeerCaretView] = [:]
        private var selectionViews: [NSView] = []
        private var caretTask: Task<Void, Never>?
        private var restyling = false
        private var placing = false
        private var placementPending = false
        /// The widest a line of the note runs; past it the text keeps to the middle of the view.
        private static let measure: CGFloat = 820

        init(sync: DocSync, media: NoteEditorMedia) {
            self.sync = sync
            self.media = media
            super.init()
        }

        /// The text has been laid out again — after an edit or a change of width. Lines may be
        /// somewhere else now, so what sits on them is moved: once, after the pass, however
        /// many times it reports in.
        nonisolated func layoutManager(_ layoutManager: NSLayoutManager, didCompleteLayoutFor textContainer: NSTextContainer?,
                                       atEnd layoutFinishedFlag: Bool) {
            MainActor.assumeIsolated {
                guard !placing, !placementPending else { return }
                placementPending = true
                DispatchQueue.main.async { [weak self] in
                    guard let self else { return }
                    self.placementPending = false
                    self.placeMedia()
                    self.placeCarets()
                }
            }
        }

        /// The glyph swap for list markers is the layout manager's own; the text view makes
        /// its coordinator the delegate, so it is passed on.
        nonisolated func layoutManager(_ layoutManager: NSLayoutManager, shouldGenerateGlyphs glyphs: UnsafePointer<CGGlyph>,
                                       properties: UnsafePointer<NSLayoutManager.GlyphProperty>,
                                       characterIndexes: UnsafePointer<Int>, font: NSFont, forGlyphRange glyphRange: NSRange) -> Int {
            (layoutManager as? NoteLayoutManager)?.layoutManager(layoutManager, shouldGenerateGlyphs: glyphs, properties: properties,
                                                                 characterIndexes: characterIndexes, font: font,
                                                                 forGlyphRange: glyphRange) ?? 0
        }

        /// True when the colour changed and the text needs styling again.
        @discardableResult
        func setAccent(_ accent: PlatformColor) -> Bool {
            guard styler.accent != accent else { return false }
            styler.accent = accent
            (textView?.layoutManager as? NoteLayoutManager)?.accent = accent
            textView?.insertionPointColor = accent
            return true
        }

        // MARK: Chrome

        /// Takes the room the bars cover out of what the text can be scrolled to rest in.
        func setInsets(_ insets: EdgeInsets) {
            guard let view = textView, let scroll = view.enclosingScrollView else { return }
            let wanted = NSEdgeInsets(top: insets.top, left: 0, bottom: insets.bottom, right: 0)
            let current = scroll.contentInsets
            guard current.top != wanted.top || current.bottom != wanted.bottom else { return }
            // A view resting at its top stays there when the top moves — as it does when it first learns the bar's height.
            let atTop = scroll.contentView.bounds.minY <= -current.top + 1
            scroll.contentInsets = wanted
            scroll.scrollerInsets = NSEdgeInsets(top: 0, left: 0, bottom: 0, right: 0)
            if atTop {
                scroll.contentView.setBoundsOrigin(NSPoint(x: 0, y: -wanted.top))
                scroll.reflectScrolledClipView(scroll.contentView)
            }
        }

        /// Puts the header at the head of the text. True when the text moved to make room.
        @discardableResult
        func setHeader(_ header: AnyView) -> Bool {
            guard let view = textView else { return false }
            if let headerHost {
                headerHost.rootView = header
            } else {
                let host = NSHostingController(rootView: header)
                attach(host, to: view)
                headerHost = host
            }
            return layoutChrome()
        }

        /// Sizes the header and the margins for the view's width. True when the text's place changed.
        @discardableResult
        func layoutChrome() -> Bool {
            guard let view = textView, view.bounds.width > 0 else { return false }
            let margin = max((view.bounds.width - Self.measure) / 2, 0)
            var top: CGFloat = 16
            if let headerHost {
                let width = view.bounds.width - margin * 2
                let height = headerHost.sizeThatFits(in: CGSize(width: width, height: .greatestFiniteMagnitude)).height
                let frame = CGRect(x: margin, y: 0, width: width, height: height)
                if headerHost.view.frame != frame { headerHost.view.frame = frame }
                top = height + 12
            }
            let wanted = NSEdgeInsets(top: top, left: margin + 14, bottom: 96, right: margin + 14)
            guard !NSEdgeInsetsEqual(view.insets, wanted) else { return false }
            view.insets = wanted
            return true
        }

        /// What the view should hold: the shared document once it has arrived, and until then
        /// what the note last read as.
        private var shownText: String {
            if let imported = sync.unseededMarkdown { return imported }
            let live = sync.shared.string()
            return live.isEmpty && !sync.synced ? fallback : live
        }

        /// Brings the view up to the document, changing only what differs and
        /// carrying the selection across the change. True when something changed.
        @discardableResult
        func applyRemote() -> Bool {
            guard let view = textView, let storage = view.textStorage else { return false }
            let latest = Array(shownText.utf16)
            guard let change = TextChange.between(known, latest) else { return false }
            let selection = view.selectedRange()
            let inserted = NSAttributedString(string: String(decoding: change.inserted, as: UTF16.self), attributes: styler.base)
            storage.replaceCharacters(in: NSRange(location: change.start, length: change.removed), with: inserted)
            known = latest
            // AppKit's Undo remembers where each edit was. Text that changed under it leaves
            // those places pointing somewhere else, and undoing to them throws; what was
            // typed before someone else's change is no longer taken back.
            view.undoManager?.removeAllActions()
            let from = change.shift(selection.location), to = change.shift(NSMaxRange(selection))
            view.setSelectedRange(NSRange(location: from, length: max(0, to - from)))
            restyle()
            return true
        }

        /// Writes whatever the view now holds that the document doesn't.
        func changedLocally() {
            // Only text that is the document's own can be written back into it.
            guard let view = textView, sync.canWrite else { return }
            let latest = Array(view.string.utf16)
            guard let change = TextChange.between(known, latest) else { return }
            sync.shared.replaceText(start: change.start, length: change.removed,
                                    with: String(decoding: change.inserted, as: UTF16.self))
            known = latest
            sync.changedLocally()
            restyle()
            publishCaret()
        }

        // MARK: Carets

        private var typing: Bool {
            guard let view = textView else { return false }
            return view.window?.firstResponder === view && view.isEditable
        }

        /// Tells the others where this caret is — a moment after it stops moving, since naming a
        /// place in the text means reading the whole document's structure.
        func publishCaret() {
            caretTask?.cancel()
            caretTask = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(90))
                guard !Task.isCancelled, let self, let view = self.textView else { return }
                // An offset in the view names a place in the document only while the two hold the same text.
                let agreed = (view.textStorage?.length ?? 0) == self.known.count
                self.sync.publishCaret(self.typing && agreed ? view.selectedRange() : nil)
            }
        }

        /// Draws everyone else's carets and selections over the text, in their colours.
        func placeCarets() {
            guard let view = textView, let manager = view.layoutManager, let container = view.textContainer else { return }
            selectionViews.forEach { $0.removeFromSuperview() }
            selectionViews = []
            let carets = sync.carets()
            let dark = view.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            let text = view.string as NSString, length = text.length
            let origin = view.textContainerOrigin
            var shown = Set<UInt64>()

            for caret in carets where caret.head <= length && caret.anchor <= length && length > 0 {
                let glyph = manager.glyphIndexForCharacter(at: min(caret.head, length - 1))
                // The height of the line it is on, not of the character it is at: folded syntax is a sliver.
                var line = manager.lineFragmentUsedRect(forGlyphAt: glyph, effectiveRange: nil)
                var x = manager.lineFragmentRect(forGlyphAt: glyph, effectiveRange: nil).minX + manager.location(forGlyphAt: glyph).x
                if caret.head >= length {
                    // Past the last character: at the end of its line, or on the empty line after a return.
                    if text.character(at: length - 1) == 10, manager.extraLineFragmentRect.height > 0 {
                        line = manager.extraLineFragmentRect
                        x = line.minX + container.lineFragmentPadding
                    } else {
                        x = line.maxX
                    }
                }
                guard x.isFinite, line.minY.isFinite else { continue }
                // A row of pictures is one tall line; a caret there stands at its foot.
                let height = min(line.height, 30)
                let frame = CGRect(x: origin.x + x - 1, y: origin.y + line.maxY - height, width: 2, height: height)
                let color = PresenceColor.color(caret.user.color, dark: dark)

                let marker = caretViews[caret.client] ?? PeerCaretView()
                marker.show(caret.user, color: color)
                if marker.superview == nil {
                    marker.frame = frame
                    view.addSubview(marker)
                    caretViews[caret.client] = marker
                } else if marker.frame != frame {
                    NSAnimationContext.runAnimationGroup { context in
                        context.duration = 0.08
                        marker.animator().frame = frame
                    }
                }
                shown.insert(caret.client)

                guard caret.anchor != caret.head else { continue }
                let characters = NSRange(location: min(caret.anchor, caret.head), length: abs(caret.anchor - caret.head))
                let glyphs = manager.glyphRange(forCharacterRange: characters, actualCharacterRange: nil)
                manager.enumerateEnclosingRects(forGlyphRange: glyphs, withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0),
                                                in: container) { rect, _ in
                    guard rect.width > 0.5, rect.height > 0.5 else { return }
                    let wash = Wash(frame: rect.offsetBy(dx: origin.x, dy: origin.y))
                    wash.wantsLayer = true
                    wash.layer?.backgroundColor = color.withAlphaComponent(0.22).cgColor
                    view.addSubview(wash, positioned: .below, relativeTo: nil)
                    self.selectionViews.append(wash)
                }
            }
            for (client, marker) in caretViews where !shown.contains(client) {
                marker.removeFromSuperview()
                caretViews[client] = nil
            }
        }

        // MARK: Live formatting

        /// Styles the note for where the caret is now. Cheap to call: the text
        /// is parsed again only when it changed, and only runs whose look
        /// changed are touched.
        func restyle() {
            // Restyling under an input method's marked text would end its composition.
            guard let view = textView, let storage = view.textStorage, !view.hasMarkedText(), !restyling else { return }
            restyling = true
            defer { restyling = false }
            let text = storage.string
            if parsed?.text != text {
                parsed = (text, LiveMarkdown(text))
            }
            guard let markdown = parsed?.markdown else { return }
            // Nothing is revealed in a note that isn't being typed in.
            let selection = typing ? view.selectedRange() : nil
            let desired = styler.styled(text, spans: markdown.spans(selection: selection), mediaSize: mediaSize)
            styler.apply(desired, to: storage)
            view.typingAttributes = styler.base
            placeMedia()
            placeCarets()

            let empty = storage.length == 0
            view.placeholder.isHidden = !empty
            if empty {
                view.placeholder.stringValue = !sync.synced ? "Loading…" : view.isEditable ? "Start writing…" : "This note is empty."
                view.placeholder.sizeToFit()
                view.placeholder.setFrameOrigin(CGPoint(x: view.insets.left + (view.textContainer?.lineFragmentPadding ?? 0),
                                                        y: view.insets.top))
            }
        }

        // MARK: Links

        /// Where the text under a point leads, if it is a link — and not one the caret is
        /// already inside, which is a link being written and not one to follow.
        func link(at point: CGPoint) -> String? {
            guard let view = textView, let manager = view.layoutManager, let container = view.textContainer,
                  let storage = view.textStorage, storage.length > 0 else { return nil }
            let origin = view.textContainerOrigin
            let spot = CGPoint(x: point.x - origin.x, y: point.y - origin.y)
            let index = manager.characterIndex(for: spot, in: container, fractionOfDistanceBetweenInsertionPoints: nil)
            var run = NSRange()
            guard index < storage.length,
                  let address = storage.attribute(.noteLink, at: index, longestEffectiveRange: &run,
                                                  in: NSRange(location: 0, length: storage.length)) as? String else { return nil }
            // The nearest character to a click in the margin is still a character; only a click on the text counts.
            let glyph = manager.boundingRect(forGlyphRange: NSRange(location: manager.glyphIndexForCharacter(at: index), length: 1),
                                             in: container)
            guard glyph.insetBy(dx: -4, dy: -2).contains(spot) else { return nil }
            if typing, NSLocationInRange(view.selectedRange().location, NSRange(location: run.location, length: run.length + 1)) {
                return nil
            }
            return address
        }

        // MARK: Task boxes

        /// Ticks or clears the task box under a point, by changing what it is written as, in
        /// a note that may be written in. True if there was one: the click was for it, and
        /// the caret stays where it is.
        func tick(at point: CGPoint) -> Bool {
            guard let view = textView, view.isEditable, sync.canWrite, let manager = view.layoutManager as? NoteLayoutManager,
                  let storage = view.textStorage else { return false }
            let origin = view.textContainerOrigin
            guard let box = manager.checkbox(at: CGPoint(x: point.x - origin.x, y: point.y - origin.y), slop: 3),
                  NSMaxRange(box) <= storage.length else { return false }
            let text = storage.string as NSString
            guard text.character(at: box.location) == 91, text.character(at: box.location + 2) == 93 else { return false }
            let selection = view.selectedRange()
            // Through the input system rather than the storage, so Undo takes it back.
            view.insertText(text.character(at: box.location + 1) == 32 ? "x" : " ",
                            replacementRange: NSRange(location: box.location + 1, length: 1))
            view.setSelectedRange(selection)
            changedLocally()
            return true
        }

        private func mediaSize(_ row: String) -> CGSize? {
            // Nothing is measured until the view has a width to lay rows out in.
            guard let view = textView, view.bounds.width > 120 else { return nil }
            return media.size(row)
        }

        /// Lays each row of pictures over the line that was made room for it.
        private func placeMedia() {
            guard let view = textView, let manager = view.layoutManager, let storage = view.textStorage, !placing else { return }
            // Asking where a line is can lay text out, which reports in above: not while this is the one asking.
            placing = true
            defer { placing = false }
            let origin = view.textContainerOrigin, padding = view.textContainer?.lineFragmentPadding ?? 0
            var occurrences: [String: Int] = [:]
            var placed = Set<String>()
            storage.enumerateAttribute(.noteMedia, in: NSRange(location: 0, length: storage.length)) { value, range, _ in
                guard let row = value as? String, let size = mediaSize(row) else { return }
                let count = occurrences[row, default: 0]
                occurrences[row] = count + 1
                let key = "\(row)#\(count)"
                guard let host = mediaHosts[key] ?? makeHost(key: key, row: row) else { return }
                // From the top down to this row, so its place is the real one and not an estimate.
                manager.ensureLayout(forCharacterRange: NSRange(location: 0, length: NSMaxRange(range)))
                let line = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: range.location), effectiveRange: nil)
                let frame = CGRect(x: origin.x + padding, y: origin.y + line.minY + (line.height - size.height) / 2,
                                   width: size.width, height: size.height)
                if host.view.frame != frame { host.view.frame = frame }
                placed.insert(key)
            }
            for (key, host) in mediaHosts where !placed.contains(key) {
                host.view.removeFromSuperview()
                mediaHosts[key] = nil
            }
        }

        private func makeHost(key: String, row: String) -> NSHostingController<AnyView>? {
            guard let view = textView, let content = media.row(row) else { return nil }
            let host = NSHostingController(rootView: content)
            attach(host, to: view)
            mediaHosts[key] = host
            return host
        }

        private func attach(_ host: NSHostingController<AnyView>, to view: NSTextView) {
            // Placed by frame, like everything else laid over the text: no constraints of its own.
            host.sizingOptions = []
            host.view.translatesAutoresizingMaskIntoConstraints = true
            host.view.autoresizingMask = []
            view.addSubview(host.view)
        }

        /// Drops the rows on show, so the next pass builds them afresh.
        func forgetMedia() {
            mediaHosts.values.forEach { $0.view.removeFromSuperview() }
            mediaHosts = [:]
        }

        // MARK: The text view's

        func textDidChange(_ notification: Notification) { changedLocally() }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard !restyling else { return }
            restyle()
            publishCaret()
        }

        /// The view took or gave up the keyboard.
        func focused(_ focused: Bool) {
            sync.report(editing: focused && textView?.isEditable == true)
            onFocus(focused && textView?.isEditable == true)
            // Not yet: the click that began this has still to put the caret down, and it does
            // so against the text as it is laid out now.
            DispatchQueue.main.async { [weak self] in self?.restyle() }
            publishCaret()
        }

        @objc func scrolled() {
            textView?.noteAnchor()
        }
    }

    /// Somebody's selection, washed over the text. Clicks pass through it to the text.
    private final class Wash: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}

/// AppKit's text view, with what a note needs of it: margins that differ top and bottom,
/// word of its width changing and of the keyboard coming and going, and a click on a link.
final class NoteTextView: NSTextView {
    var onWidthChange: (() -> Void)?
    var onFocus: ((Bool) -> Void)?
    var linkAt: ((CGPoint) -> String?)?
    var onLink: ((String) -> Void)?
    /// Ticks the task box under a point; true if there was one.
    var tickAt: ((CGPoint) -> Bool)?
    let placeholder = NSTextField(labelWithString: "")
    private var lastWidth: CGFloat = 0

    /// The room around the text. AppKit's own inset is the same on opposite sides, and a note
    /// has its name above it and room to scroll past its end below: the inset says how much
    /// room there is in all, and the origin where in it the text starts.
    var insets = NSEdgeInsets(top: 16, left: 14, bottom: 96, right: 14) {
        didSet {
            textContainerInset = NSSize(width: (insets.left + insets.right) / 2, height: (insets.top + insets.bottom) / 2)
            invalidateTextContainerOrigin()
            needsDisplay = true
        }
    }

    override var textContainerOrigin: NSPoint { NSPoint(x: insets.left, y: insets.top) }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        guard abs(newSize.width - lastWidth) > 0.5 else { return }
        // After the pass that is resizing the view: restyling changes the text, which is not
        // to be done while it is being laid out.
        DispatchQueue.main.async { [weak self] in
            guard let self, abs(self.frame.width - self.lastWidth) > 0.5 else { return }
            self.lastWidth = self.frame.width
            self.onWidthChange?()
            self.restoreAnchor()
        }
    }

    override func becomeFirstResponder() -> Bool {
        let became = super.becomeFirstResponder()
        if became { onFocus?(true) }
        return became
    }

    override func resignFirstResponder() -> Bool {
        let resigned = super.resignFirstResponder()
        if resigned { onFocus?(false) }
        return resigned
    }

    override func mouseDown(with event: NSEvent) {
        if tickAt?(convert(event.locationInWindow, from: nil)) == true { return }
        if let address = linkAt?(convert(event.locationInWindow, from: nil)) {
            onLink?(address)
            return
        }
        super.mouseDown(with: event)
    }

    // MARK: Keeping the place

    // A change of width wraps every line differently, so the same scroll offset shows
    // somewhere else in the note. What the reader was looking at is remembered as the
    // character at the top of the view, and put back there once the text has been laid out
    // at the new width.

    /// The character whose line is at the top of what shows, and how far into that line the top is.
    private var anchor: (character: Int, into: CGFloat)?
    private var restoring = false

    /// A scroll offset, held to where the note can rest.
    func clamped(_ offset: CGFloat) -> CGFloat {
        guard let scroll = enclosingScrollView else { return offset }
        let lowest = -scroll.contentInsets.top
        let highest = max(lowest, frame.height + scroll.contentInsets.bottom - scroll.contentView.bounds.height)
        return min(max(offset, lowest), highest)
    }

    func noteAnchor() {
        // Only while the layout is for the width the view has: in between, an offset says
        // nothing about which line is where.
        guard !restoring, abs(frame.width - lastWidth) <= 0.5, let scroll = enclosingScrollView,
              let manager = layoutManager, let container = textContainer, let storage = textStorage else { return }
        let top = scroll.contentView.bounds.minY + scroll.contentInsets.top - insets.top
        // At the head of the note, where its name is, there is nothing to hold on to: it stays at the top.
        guard storage.length > 0, top > 0 else {
            anchor = nil
            return
        }
        let character = manager.characterIndex(for: CGPoint(x: 4, y: top), in: container, fractionOfDistanceBetweenInsertionPoints: nil)
        let line = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: character), effectiveRange: nil)
        anchor = (character, max(top - line.minY, 0))
    }

    private func restoreAnchor() {
        guard let anchor, let scroll = enclosingScrollView, let manager = layoutManager, let storage = textStorage,
              anchor.character < storage.length else { return }
        manager.ensureLayout(forCharacterRange: NSRange(location: 0, length: anchor.character + 1))
        let line = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: anchor.character), effectiveRange: nil)
        restoring = true
        scroll.contentView.setBoundsOrigin(NSPoint(x: 0, y: clamped(line.minY + min(anchor.into, line.height) + insets.top
                                                                    - scroll.contentInsets.top)))
        scroll.reflectScrolledClipView(scroll.contentView)
        restoring = false
    }
}

/// Somebody else's caret: a bar in their colour, with their initials on a tag at its head.
final class PeerCaretView: NSView {
    private let flag = NSTextField(labelWithString: "")
    private var shown: (AwarenessUser, PlatformColor)?

    override var isFlipped: Bool { true }

    override init(frame: CGRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.cornerRadius = 1
        layer?.masksToBounds = false
        flag.font = .systemFont(ofSize: 9, weight: .bold)
        flag.alignment = .center
        flag.textColor = .white
        flag.wantsLayer = true
        flag.layer?.cornerRadius = 7
        flag.layer?.masksToBounds = true
        flag.drawsBackground = false
        addSubview(flag)
        setAccessibilityElement(true)
        setAccessibilityRole(.staticText)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func show(_ user: AwarenessUser, color: PlatformColor) {
        if let shown, shown.0 == user, shown.1 == color { return }
        shown = (user, color)
        layer?.backgroundColor = color.cgColor
        flag.layer?.backgroundColor = color.cgColor
        flag.stringValue = user.initials
        setAccessibilityLabel("\(user.name)'s cursor")
        // Above the bar and a little to its right, clear of the text it is in.
        let width = max(14, flag.intrinsicContentSize.width + 8)
        flag.frame = CGRect(x: 0, y: -13, width: width, height: 14)
    }
}
#endif
