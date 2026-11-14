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

/// A plain-text editor whose text is a shared document. What is typed here is
/// written into the document as it is typed; what others type is patched into
/// the view around the caret, so two people in one paragraph don't fight.
struct SyncedTextEditor: UIViewRepresentable {
    var sync: DocSync
    var proxy: NoteEditorProxy
    /// Read so the view updates when either changes.
    var revision: Int
    var editable: Bool
    var onFocus: (Bool) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(sync: sync) }

    func makeUIView(context: Context) -> UITextView {
        let view = UITextView()
        view.delegate = context.coordinator
        view.backgroundColor = .clear
        view.font = .preferredFont(forTextStyle: .body)
        view.textColor = .label
        view.adjustsFontForContentSizeCategory = true
        view.textContainerInset = UIEdgeInsets(top: 16, left: 14, bottom: 96, right: 14)
        view.alwaysBounceVertical = true
        view.keyboardDismissMode = .interactive
        // Markdown is punctuation: `--` and straight quotes have to stay what was typed.
        view.smartDashesType = .no
        view.smartQuotesType = .no
        view.accessibilityLabel = "Note text"
        context.coordinator.textView = view
        context.coordinator.onFocus = onFocus
        proxy.coordinator = context.coordinator
        context.coordinator.applyRemote()
        return view
    }

    func updateUIView(_ view: UITextView, context: Context) {
        context.coordinator.onFocus = onFocus
        proxy.coordinator = context.coordinator
        if view.isEditable != editable {
            view.isEditable = editable
        }
        context.coordinator.applyRemote()
    }

    final class Coordinator: NSObject, UITextViewDelegate {
        let sync: DocSync
        weak var textView: UITextView?
        var onFocus: (Bool) -> Void = { _ in }
        /// The text as the shared document and the view last agreed on it.
        private var known: [UInt16] = []

        init(sync: DocSync) {
            self.sync = sync
        }

        /// Brings the view up to the document, changing only what differs and
        /// carrying the selection across the change.
        func applyRemote() {
            guard let view = textView else { return }
            let latest = Array(sync.shared.string().utf16)
            guard let change = TextChange.between(known, latest) else { return }
            let selection = view.selectedRange
            // Attributed, because text put straight into the storage would otherwise arrive without the view's font.
            let inserted = NSAttributedString(string: String(decoding: change.inserted, as: UTF16.self), attributes: [
                .font: view.font ?? UIFont.preferredFont(forTextStyle: .body), .foregroundColor: UIColor.label,
            ])
            view.textStorage.replaceCharacters(in: NSRange(location: change.start, length: change.removed), with: inserted)
            known = latest
            let from = change.shift(selection.location), to = change.shift(NSMaxRange(selection))
            view.selectedRange = NSRange(location: from, length: max(0, to - from))
        }

        /// Writes whatever the view now holds that the document doesn't.
        func changedLocally() {
            guard let view = textView else { return }
            let latest = Array(view.text.utf16)
            guard let change = TextChange.between(known, latest) else { return }
            sync.shared.replaceText(start: change.start, length: change.removed,
                                    with: String(decoding: change.inserted, as: UTF16.self))
            known = latest
            sync.changedLocally()
        }

        func textViewDidChange(_ textView: UITextView) { changedLocally() }

        func textViewDidBeginEditing(_ textView: UITextView) {
            sync.report(editing: true)
            onFocus(true)
        }

        func textViewDidEndEditing(_ textView: UITextView) {
            sync.report(editing: false)
            onFocus(false)
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
    var keyboardUp: Bool

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
            if keyboardUp {
                Divider().frame(height: 22)
                button("Hide keyboard", "keyboard.chevron.compact.down") { proxy.dismissKeyboard() }
            }
        }
    }

    private func button(_ title: String, _ symbol: String, action: @escaping () -> Void) -> some View {
        Button(title, systemImage: symbol, action: action)
    }
}
