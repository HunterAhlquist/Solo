import SwiftUI
import UIKit

/// The smallest edit that turns one text into another, in UTF-16 units — the
/// units both Yjs and a text view count in.
nonisolated struct TextChange: Equatable, Sendable {
    var start: Int
    var removed: Int
    var inserted: [UInt16]

    /// Nil when the texts are the same. The edit never splits a surrogate
    /// pair, which would put half a character into the shared document.
    static func between(_ old: [UInt16], _ new: [UInt16]) -> TextChange? {
        let shortest = min(old.count, new.count)
        var prefix = 0
        while prefix < shortest, old[prefix] == new[prefix] { prefix += 1 }
        if prefix > 0, UTF16.isLeadSurrogate(old[prefix - 1]) { prefix -= 1 }
        var suffix = 0
        while suffix < shortest - prefix, old[old.count - 1 - suffix] == new[new.count - 1 - suffix] { suffix += 1 }
        if suffix > 0, UTF16.isTrailSurrogate(old[old.count - suffix]) { suffix -= 1 }
        let removed = old.count - prefix - suffix
        let inserted = Array(new[prefix..<(new.count - suffix)])
        return removed == 0 && inserted.isEmpty ? nil : TextChange(start: prefix, removed: removed, inserted: inserted)
    }

    /// Where a position in the old text ends up in the new one.
    func shift(_ position: Int) -> Int {
        if position <= start { return position }
        if position >= start + removed { return position + inserted.count - removed }
        return start + inserted.count
    }
}

/// What the buttons around the editor ask of it. The text view is UIKit's, so
/// the formatting commands reach it through this rather than through state.
final class NoteEditorProxy {
    fileprivate weak var coordinator: SyncedTextEditor.Coordinator?

    var hasSelection: Bool { (coordinator?.textView?.selectedRange.length ?? 0) > 0 }

    func focus() { coordinator?.textView?.becomeFirstResponder() }
    func dismissKeyboard() { coordinator?.textView?.resignFirstResponder() }

    /// Puts markers around the selection, leaving it selected between them.
    func wrap(_ prefix: String, _ suffix: String) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange
        let selected = (view.text as NSString).substring(with: range)
        replace(range, with: prefix + selected + suffix)
        view.selectedRange = NSRange(location: range.location + prefix.utf16.count, length: range.length)
    }

    /// Starts the caret's line with a marker.
    func prefixLine(_ prefix: String) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange
        let line = (view.text as NSString).lineRange(for: NSRange(location: range.location, length: 0))
        replace(NSRange(location: line.location, length: 0), with: prefix)
        view.selectedRange = NSRange(location: range.location + prefix.utf16.count, length: range.length)
    }

    /// Replaces the selection. `ownLine` gives an embed a line to itself.
    func insert(_ text: String, ownLine: Bool = false) {
        guard let view = coordinator?.textView else { return }
        let range = view.selectedRange
        var snippet = text
        if ownLine {
            let whole = view.text as NSString
            if range.location > 0, whole.character(at: range.location - 1) != 10 { snippet = "\n" + snippet }
            if NSMaxRange(range) >= whole.length || whole.character(at: NSMaxRange(range)) != 10 { snippet += "\n" }
        }
        replace(range, with: snippet)
        view.selectedRange = NSRange(location: range.location + snippet.utf16.count, length: 0)
    }

    /// Brings a line of the note — counted from zero — to the top of the view.
    func scroll(toLine line: Int) {
        guard let view = coordinator?.textView else { return }
        let text = view.textStorage.string as NSString
        var offset = 0, remaining = line
        while remaining > 0, offset < text.length {
            offset = NSMaxRange(text.lineRange(for: NSRange(location: offset, length: 0)))
            remaining -= 1
        }
        guard offset < text.length else { return }
        view.layoutManager.ensureLayout(forCharacterRange: NSRange(location: 0, length: offset + 1))
        let row = view.layoutManager.lineFragmentRect(forGlyphAt: view.layoutManager.glyphIndexForCharacter(at: offset), effectiveRange: nil)
        let highest = max(0, view.contentSize.height + view.adjustedContentInset.bottom - view.bounds.height)
        let top = min(max(row.minY + view.textContainerInset.top - 12, 0), highest)
        view.setContentOffset(CGPoint(x: 0, y: top), animated: true)
    }

    private func replace(_ range: NSRange, with text: String) {
        guard let view = coordinator?.textView, view.isEditable,
              let start = view.position(from: view.beginningOfDocument, offset: range.location),
              let end = view.position(from: start, offset: range.length),
              let target = view.textRange(from: start, to: end) else { return }
        // Through the input system rather than the storage, so Undo takes it back.
        view.replace(target, withText: text)
        coordinator?.changedLocally()
    }
}

/// A Markdown editor whose text is a shared document. What is typed here is
/// written into the document as it is typed; what others type is patched into
/// the view around the caret, so two people in one paragraph don't fight.
///
/// It formats as it goes: the text is always the note's exact Markdown, drawn
/// as what it means, with each piece of syntax folding away until the caret
/// comes back to it.
struct SyncedTextEditor: UIViewRepresentable {
    var sync: DocSync
    var proxy: NoteEditorProxy
    /// Read so the view updates when either changes.
    var revision: Int
    var editable: Bool
    /// What to show until the shared document arrives: the note's last saved copy.
    var fallback: String
    var accent: UIColor
    var media: NoteEditorMedia
    /// Changes when what the rows of pictures show may have: an upload, a deletion, who may write.
    var mediaRevision: Int
    /// A link tapped while the note wasn't being typed in.
    var onLink: (String) -> Void = { _ in }
    var onFocus: (Bool) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(sync: sync, media: media) }

    func makeUIView(context: Context) -> NoteTextView {
        // TextKit 1, for a layout manager that can draw behind lines.
        let storage = NSTextStorage()
        let manager = NoteLayoutManager()
        let container = NSTextContainer(size: CGSize(width: 0, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        manager.addTextContainer(container)
        storage.addLayoutManager(manager)

        let view = NoteTextView(frame: .zero, textContainer: container)
        view.delegate = context.coordinator
        view.backgroundColor = .clear
        view.textContainerInset = UIEdgeInsets(top: 16, left: 14, bottom: 96, right: 14)
        view.alwaysBounceVertical = true
        view.keyboardDismissMode = .interactive
        // Markdown is punctuation: `--` and straight quotes have to stay what was typed.
        view.smartDashesType = .no
        view.smartQuotesType = .no
        view.accessibilityLabel = "Note text"
        view.isEditable = editable

        view.placeholder.font = .preferredFont(forTextStyle: .body)
        view.placeholder.adjustsFontForContentSizeCategory = true
        view.placeholder.textColor = .tertiaryLabel
        view.placeholder.isHidden = true
        view.addSubview(view.placeholder)

        let coordinator = context.coordinator
        coordinator.textView = view
        coordinator.onFocus = onFocus
        coordinator.onLink = onLink
        coordinator.fallback = fallback
        // A note that can't be edited never begins editing, so its links need a tap of their own.
        let tap = UITapGestureRecognizer(target: coordinator, action: #selector(Coordinator.tapped(_:)))
        tap.cancelsTouchesInView = false
        tap.delegate = coordinator
        view.addGestureRecognizer(tap)
        coordinator.setAccent(accent)
        coordinator.mediaRevision = mediaRevision
        view.typingAttributes = coordinator.styler.base
        view.onWidthChange = { [weak coordinator] in coordinator?.restyle() }
        proxy.coordinator = coordinator
        coordinator.applyRemote()
        return view
    }

    func updateUIView(_ view: NoteTextView, context: Context) {
        let coordinator = context.coordinator
        coordinator.onFocus = onFocus
        coordinator.onLink = onLink
        coordinator.fallback = fallback
        coordinator.media = media
        proxy.coordinator = coordinator
        var stale = coordinator.setAccent(accent)
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
        }
    }

    final class Coordinator: NSObject, UITextViewDelegate, UIGestureRecognizerDelegate {
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
        private var mediaHosts: [String: UIHostingController<AnyView>] = [:]
        private var restyling = false

        init(sync: DocSync, media: NoteEditorMedia) {
            self.sync = sync
            self.media = media
            super.init()
            NotificationCenter.default.addObserver(self, selector: #selector(textSizeChanged),
                                                   name: UIContentSizeCategory.didChangeNotification, object: nil)
        }

        @objc private func textSizeChanged() {
            styler.refreshFonts()
            restyle()
        }

        /// True when the colour changed and the text needs styling again.
        @discardableResult
        func setAccent(_ accent: UIColor) -> Bool {
            guard styler.accent != accent else { return false }
            styler.accent = accent
            (textView?.layoutManager as? NoteLayoutManager)?.accent = accent
            textView?.tintColor = accent
            return true
        }

        /// What the view should hold: the shared document once it has arrived,
        /// and until then what the note last read as — its saved copy, or for
        /// a note nobody able to write has opened yet, the text it was imported with.
        private var shownText: String {
            if let imported = sync.unseededMarkdown { return imported }
            let live = sync.shared.string()
            return live.isEmpty && !sync.synced ? fallback : live
        }

        /// Brings the view up to the document, changing only what differs and
        /// carrying the selection across the change. True when something changed.
        @discardableResult
        func applyRemote() -> Bool {
            guard let view = textView else { return false }
            let latest = Array(shownText.utf16)
            guard let change = TextChange.between(known, latest) else { return false }
            let selection = view.selectedRange
            let inserted = NSAttributedString(string: String(decoding: change.inserted, as: UTF16.self), attributes: styler.base)
            view.textStorage.replaceCharacters(in: NSRange(location: change.start, length: change.removed), with: inserted)
            known = latest
            let from = change.shift(selection.location), to = change.shift(NSMaxRange(selection))
            view.selectedRange = NSRange(location: from, length: max(0, to - from))
            restyle()
            return true
        }

        /// Writes whatever the view now holds that the document doesn't.
        func changedLocally() {
            // Only text that is the document's own can be written back into it.
            guard let view = textView, sync.canWrite else { return }
            let latest = Array(view.text.utf16)
            guard let change = TextChange.between(known, latest) else { return }
            sync.shared.replaceText(start: change.start, length: change.removed,
                                    with: String(decoding: change.inserted, as: UTF16.self))
            known = latest
            sync.changedLocally()
            restyle()
        }

        // MARK: Live formatting

        /// Styles the note for where the caret is now. Cheap to call: the text
        /// is parsed again only when it changed, and only runs whose look
        /// changed are touched.
        func restyle() {
            // Restyling under an input method's marked text would end its composition.
            guard let view = textView, view.markedTextRange == nil, !restyling else { return }
            restyling = true
            defer { restyling = false }
            let text = view.textStorage.string
            if parsed?.text != text {
                parsed = (text, LiveMarkdown(text))
            }
            guard let markdown = parsed?.markdown else { return }
            // Away from the keyboard nothing is being edited, so nothing is revealed.
            let selection = view.isFirstResponder && view.isEditable ? view.selectedRange : nil
            let desired = styler.styled(text, spans: markdown.spans(selection: selection), mediaSize: mediaSize)
            styler.apply(desired, to: view.textStorage)
            view.typingAttributes = styler.base
            placeMedia()

            let empty = view.textStorage.length == 0
            view.placeholder.isHidden = !empty
            if empty {
                view.placeholder.text = !sync.synced ? "Loading…" : view.isEditable ? "Start writing…" : "This note is empty."
                view.placeholder.sizeToFit()
                view.placeholder.frame.origin = CGPoint(x: view.textContainerInset.left + view.textContainer.lineFragmentPadding,
                                                        y: view.textContainerInset.top)
            }
        }

        // MARK: Links

        /// Where the text under a point leads, if it is a link.
        private func link(at point: CGPoint) -> String? {
            guard let view = textView, view.textStorage.length > 0 else { return nil }
            let manager = view.layoutManager
            let spot = CGPoint(x: point.x - view.textContainerInset.left, y: point.y - view.textContainerInset.top)
            let index = manager.characterIndex(for: spot, in: view.textContainer, fractionOfDistanceBetweenInsertionPoints: nil)
            guard index < view.textStorage.length,
                  let address = view.textStorage.attribute(.noteLink, at: index, effectiveRange: nil) as? String else { return nil }
            // The nearest character to a tap in the margin is still a character; only a tap on the text counts.
            let glyph = manager.boundingRect(forGlyphRange: NSRange(location: manager.glyphIndexForCharacter(at: index), length: 1),
                                             in: view.textContainer)
            return glyph.insetBy(dx: -8, dy: -4).contains(spot) ? address : nil
        }

        /// A tap in a note that is being read opens the link under it rather
        /// than putting the caret there. Once typing, a tap is a tap.
        func textViewShouldBeginEditing(_ textView: UITextView) -> Bool {
            guard let view = self.textView, let touch = view.lastTouch, Date().timeIntervalSince(touch.at) < 1,
                  let address = link(at: touch.point) else { return true }
            view.forgetTouch()
            onLink(address)
            return false
        }

        @objc func tapped(_ gesture: UITapGestureRecognizer) {
            guard let view = textView, !view.isEditable, let address = link(at: gesture.location(in: view)) else { return }
            onLink(address)
        }

        func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                               shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool { true }

        private func mediaSize(_ row: String) -> CGSize? {
            // Nothing is measured until the view has a width to lay rows out in.
            guard let view = textView, view.bounds.width > 120 else { return nil }
            return media.size(row)
        }

        /// Lays each row of pictures over the line that was made room for it.
        private func placeMedia() {
            guard let view = textView else { return }
            let manager = view.layoutManager, storage = view.textStorage
            var occurrences: [String: Int] = [:]
            var placed = Set<String>()
            storage.enumerateAttribute(.noteMedia, in: NSRange(location: 0, length: storage.length)) { value, range, _ in
                guard let row = value as? String, let size = mediaSize(row) else { return }
                let count = occurrences[row, default: 0]
                occurrences[row] = count + 1
                let key = "\(row)#\(count)"
                guard let host = mediaHosts[key] ?? makeHost(key: key, row: row) else { return }
                manager.ensureLayout(forCharacterRange: range)
                let line = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: range.location), effectiveRange: nil)
                let frame = CGRect(x: view.textContainerInset.left + view.textContainer.lineFragmentPadding,
                                   y: view.textContainerInset.top + line.minY + (line.height - size.height) / 2,
                                   width: size.width, height: size.height)
                if host.view.frame != frame { host.view.frame = frame }
                placed.insert(key)
            }
            for (key, host) in mediaHosts where !placed.contains(key) {
                remove(host)
                mediaHosts[key] = nil
            }
        }

        /// A row is SwiftUI, hosted as a child of whatever controller holds
        /// the text view — a child so that it can present its viewer.
        private func makeHost(key: String, row: String) -> UIHostingController<AnyView>? {
            guard let view = textView, let content = media.row(row) else { return nil }
            let host = UIHostingController(rootView: content)
            host.view.backgroundColor = .clear
            host.safeAreaRegions = []
            var responder: UIResponder? = view
            while let next = responder?.next, !(next is UIViewController) { responder = next }
            let parent = responder?.next as? UIViewController
            parent?.addChild(host)
            view.addSubview(host.view)
            host.didMove(toParent: parent)
            mediaHosts[key] = host
            return host
        }

        private func remove(_ host: UIHostingController<AnyView>) {
            host.willMove(toParent: nil)
            host.view.removeFromSuperview()
            host.removeFromParent()
        }

        /// Drops the rows on show, so the next pass builds them afresh.
        func forgetMedia() {
            mediaHosts.values.forEach(remove)
            mediaHosts = [:]
        }

        // MARK: UITextViewDelegate

        func textViewDidChange(_ textView: UITextView) { changedLocally() }

        func textViewDidChangeSelection(_ textView: UITextView) { restyle() }

        func textViewDidBeginEditing(_ textView: UITextView) {
            sync.report(editing: true)
            onFocus(true)
            restyle()
        }

        func textViewDidEndEditing(_ textView: UITextView) {
            sync.report(editing: false)
            onFocus(false)
            restyle()
        }
    }
}

/// The formatting a touch keyboard makes awkward, one tap away. It rides above
/// the keyboard as its own glass bar rather than as a row of the keyboard.
struct NoteFormatBar: View {
    var proxy: NoteEditorProxy
    var onLinkNote: () -> Void
    var onLinkTask: () -> Void
    var onSketch: () -> Void
    var onImage: () -> Void
    var onAttachments: () -> Void

    var body: some View {
        FormatBar {
            button("Bold", "bold") { proxy.wrap("**", "**") }
            button("Italic", "italic") { proxy.wrap("*", "*") }
            button("Highlight", "highlighter") { proxy.wrap("==", "==") }
            button("Code", "chevron.left.forwardslash.chevron.right") { proxy.wrap("`", "`") }
            Divider().frame(height: 22)
            button("Heading", "number") { proxy.prefixLine("## ") }
            button("List", "list.bullet") { proxy.prefixLine("- ") }
            button("Checklist", "checklist") { proxy.prefixLine("- [ ] ") }
            button("Quote", "text.quote") { proxy.prefixLine("> ") }
            Divider().frame(height: 22)
            button("Link", "link") { proxy.wrap("[", "](https://)") }
            button("Link a note", "doc.text") { onLinkNote() }
            button("Link a task", "rectangle.stack.badge.plus") { onLinkTask() }
            Divider().frame(height: 22)
            button("New sketch", "scribble.variable") { onSketch() }
            button("Add image", "photo.badge.plus") { onImage() }
            button("Attachments", "paperclip") { onAttachments() }
            Divider().frame(height: 22)
            button("Done", "keyboard.chevron.compact.down") { proxy.dismissKeyboard() }
        }
    }

    private func button(_ title: String, _ symbol: String, action: @escaping () -> Void) -> some View {
        Button(title, systemImage: symbol, action: action)
    }
}
