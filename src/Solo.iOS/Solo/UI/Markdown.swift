import MarkdownUI
import SwiftUI

/// Rendered markdown plus Solo's task-link syntax: `||PROJECT-BOARD-N||`
/// becomes a link that opens that task in the app.
///
/// User markdown is hostile input. Raw HTML is never rendered, and a link
/// only opens when its scheme is http, https or mailto — anything else is
/// dropped where it is tapped.
struct SoloMarkdown: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @Environment(\.openURL) private var openURL

    var text: String
    /// A preview of your own draft must not navigate away mid-edit.
    var navigationLocked = false
    /// Extra rewriting before rendering, for extensions that label their own links.
    var transform: ((String) -> String)?

    private static let taskScheme = "solo-task"
    private static let allowedSchemes: Set<String> = ["http", "https", "mailto"]

    var body: some View {
        Markdown(rendered, baseURL: session.api.baseURL)
            .markdownTheme(markdownTheme)
            .environment(\.openURL, OpenURLAction { url in
                guard !navigationLocked else { return .handled }
                if ExtensionLinks.open(url, session: session) {
                    return .handled
                }
                if url.scheme == Self.taskScheme, let reference = TaskReference(serial: url.host() ?? "") {
                    navigator.open(reference)
                    return .handled
                }
                if let route = SoloLinks.route(for: url, instance: session.api.baseURL) {
                    navigator.open(route)
                    return .handled
                }
                guard let scheme = url.scheme?.lowercased(), Self.allowedSchemes.contains(scheme) else {
                    return .discarded
                }
                return .systemAction
            })
            .textSelection(.enabled)
    }

    private var rendered: String {
        let linked = text.replacing(TaskReference.token) { match in
            let reference = TaskReference(projectAlias: String(match.1), boardAlias: String(match.2), number: Int(match.3) ?? 0)
            return "[\(reference.serial)](\(Self.taskScheme)://\(reference.serial))"
        }
        return transform?(linked) ?? linked
    }

    private var markdownTheme: MarkdownUI.Theme {
        let palette = theme
        return MarkdownUI.Theme()
            .text {
                ForegroundColor(palette.textPrimary)
                FontSize(.em(1))
            }
            .link {
                ForegroundColor(palette.primary)
                UnderlineStyle(.single)
            }
            .strong { FontWeight(.semibold) }
            .code {
                FontFamilyVariant(.monospaced)
                FontSize(.em(0.9))
                ForegroundColor(palette.secondary)
                BackgroundColor(palette.backgroundGray)
            }
            .heading1 { configuration in
                configuration.label
                    .markdownTextStyle { FontWeight(.bold); FontSize(.em(1.5)) }
                    .markdownMargin(top: 12, bottom: 6)
            }
            .heading2 { configuration in
                configuration.label
                    .markdownTextStyle { FontWeight(.bold); FontSize(.em(1.28)) }
                    .markdownMargin(top: 12, bottom: 6)
            }
            .heading3 { configuration in
                configuration.label
                    .markdownTextStyle { FontWeight(.semibold); FontSize(.em(1.12)) }
                    .markdownMargin(top: 10, bottom: 4)
            }
            .paragraph { configuration in
                configuration.label
                    .fixedSize(horizontal: false, vertical: true)
                    .relativeLineSpacing(.em(0.2))
                    .markdownMargin(top: 0, bottom: 10)
            }
            .blockquote { configuration in
                HStack(spacing: 0) {
                    RoundedRectangle(cornerRadius: 2).fill(palette.primary.opacity(0.7)).frame(width: 3)
                    configuration.label
                        .markdownTextStyle { ForegroundColor(palette.textSecondary) }
                        .padding(.leading, 10)
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .codeBlock { configuration in
                ScrollView(.horizontal) {
                    configuration.label
                        .markdownTextStyle {
                            FontFamilyVariant(.monospaced)
                            FontSize(.em(0.88))
                        }
                        .padding(10)
                }
                .background(palette.backgroundGray, in: RoundedRectangle(cornerRadius: 8))
                .markdownMargin(top: 4, bottom: 10)
            }
            .listItem { configuration in
                configuration.label.markdownMargin(top: .em(0.2))
            }
            .thematicBreak {
                Divider().overlay(palette.divider).markdownMargin(top: 12, bottom: 12)
            }
            .table { configuration in
                configuration.label
                    .markdownTableBorderStyle(.init(color: palette.divider))
                    .markdownMargin(top: 4, bottom: 10)
            }
    }
}

/// Links into the instance itself that the app can show natively: the web
/// app's own routes, pasted into a description by someone on a desktop.
enum SoloLinks {
    static func route(for url: URL, instance: URL) -> Route? {
        guard url.host() == instance.host(), url.port == instance.port else { return nil }
        let parts = url.pathComponents.filter { $0 != "/" }
        guard parts.count >= 2, parts[0].lowercased() == "projects" else { return nil }
        let project = Aliases.normalize(parts[1])
        if parts.count >= 4, parts[2].lowercased() == "boards" {
            let task = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?
                .first { $0.name == "task" }?.value.flatMap(Int.init)
            return .board(project: project, board: Aliases.normalize(parts[3]), task: task)
        }
        return parts.count == 2 ? .project(alias: project) : nil
    }
}

// MARK: - Editing

/// What an editor sheet is editing, and what happens when it is saved.
struct MarkdownDraft: Identifiable {
    let id = UUID()
    var title: String
    var text: String
    var limit: Int
    var saveLabel = "Save"
    var placeholder = "Write markdown…"
    /// The project the text belongs to, for the things an extension can link from it.
    var projectId: UUID?
    var onSave: (String) async -> Void
}

/// Write and preview, with the formatting a phone keyboard makes awkward one
/// tap away. Saving is explicit: nothing is sent per keystroke.
struct MarkdownEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    @Environment(Session.self) private var session

    let draft: MarkdownDraft
    @State private var text: String
    @State private var selection: TextSelection?
    @State private var previewing = false
    @State private var showingCheatsheet = false
    @State private var pickingTask = false
    @State private var saving = false
    @FocusState private var focused: Bool

    init(draft: MarkdownDraft) {
        self.draft = draft
        _text = State(initialValue: draft.text)
    }

    var body: some View {
        NavigationStack {
            Group {
                if previewing {
                    ScrollView {
                        if text.nilIfBlank == nil {
                            Text("Nothing to preview.")
                                .foregroundStyle(theme.textSecondary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding()
                        } else {
                            SoloMarkdown(text: text, navigationLocked: true)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding()
                        }
                    }
                } else {
                    TextEditor(text: $text, selection: $selection)
                        .font(.system(.body, design: .monospaced))
                        .focused($focused)
                        .scrollContentBackground(.hidden)
                        .padding(.horizontal, 12)
                        .overlay(alignment: .topLeading) {
                            if text.isEmpty {
                                Text(draft.placeholder)
                                    .font(.system(.body, design: .monospaced))
                                    .foregroundStyle(theme.textDisabled)
                                    .padding(.horizontal, 17)
                                    .padding(.vertical, 8)
                                    .allowsHitTesting(false)
                            }
                        }
                }
            }
            .themedBackground()
            .navigationTitle(draft.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", systemImage: "xmark") { dismiss() }
                }
                ToolbarItem(placement: .principal) {
                    Picker("Mode", selection: $previewing) {
                        Text("Write").tag(false)
                        Text("Preview").tag(true)
                    }
                    .pickerStyle(.segmented)
                    .frame(width: 180)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(draft.saveLabel) { save() }
                        .fontWeight(.semibold)
                        .disabled(saving || text.count > draft.limit)
                }
            }
            // A bar, so what is written scrolls behind it instead of stopping short at its edge.
            .safeAreaBar(edge: .bottom, spacing: 0) {
                VStack(spacing: 0) {
                    if !previewing {
                        FormatBar { formatButtons }
                    }
                    HStack {
                        Button("Markdown") { showingCheatsheet = true }
                            .font(.footnote)
                        Spacer()
                        Text("\(text.count.formatted()) / \(draft.limit.formatted())")
                            .font(.footnote.monospacedDigit())
                            .foregroundStyle(text.count > draft.limit ? theme.error : theme.textSecondary)
                    }
                    .padding(.horizontal, 16)
                    .padding(.bottom, 8)
                }
            }
            .sheet(isPresented: $showingCheatsheet) { MarkdownCheatsheet() }
            .sheet(isPresented: $pickingTask) {
                TaskPickerSheet(title: "Link a task") { reference in
                    insert(reference.markdownToken)
                }
            }
            .onAppear { focused = true }
        }
        .interactiveDismissDisabled(text != draft.text)
    }

    @ViewBuilder
    private var formatButtons: some View {
        Button("Bold", systemImage: "bold") { wrap("**", "**") }
        Button("Italic", systemImage: "italic") { wrap("*", "*") }
        Button("Code", systemImage: "chevron.left.forwardslash.chevron.right") { wrap("`", "`") }
        Button("List", systemImage: "list.bullet") { prefixLine("- ") }
        Button("Checklist", systemImage: "checklist") { prefixLine("- [ ] ") }
        Button("Heading", systemImage: "number") { prefixLine("## ") }
        Button("Link", systemImage: "link") { wrap("[", "](https://)") }
        Button("Task link", systemImage: "rectangle.stack.badge.plus") { pickingTask = true }
        MarkdownEditorExtensions(projectId: draft.projectId) { insert($0) }
        if focused {
            Divider().frame(height: 22)
            Button("Hide keyboard", systemImage: "keyboard.chevron.compact.down") { focused = false }
        }
    }

    private var selectedRange: Range<String.Index> {
        if case .selection(let range) = selection?.indices, range.upperBound <= text.endIndex {
            return range
        }
        return text.endIndex..<text.endIndex
    }

    private func wrap(_ prefix: String, _ suffix: String) {
        let range = selectedRange
        let selected = String(text[range])
        text.replaceSubrange(range, with: prefix + selected + suffix)
        // Leave the caret inside the markers when there was nothing to wrap.
        let start = text.index(range.lowerBound, offsetBy: prefix.count)
        let end = text.index(start, offsetBy: selected.count)
        selection = TextSelection(range: start..<end)
    }

    private func prefixLine(_ prefix: String) {
        let range = selectedRange
        let lineStart = text[..<range.lowerBound].lastIndex(of: "\n").map { text.index(after: $0) } ?? text.startIndex
        text.insert(contentsOf: prefix, at: lineStart)
        let caret = text.index(range.upperBound, offsetBy: prefix.count, limitedBy: text.endIndex) ?? text.endIndex
        selection = TextSelection(insertionPoint: caret)
    }

    private func insert(_ snippet: String) {
        let range = selectedRange
        text.replaceSubrange(range, with: snippet)
        selection = TextSelection(insertionPoint: text.index(range.lowerBound, offsetBy: snippet.count))
    }

    private func save() {
        saving = true
        Task {
            await draft.onSave(text)
            dismiss()
        }
    }
}

/// A row of formatting buttons that rides above the keyboard as its own glass
/// bar. Its own bar rather than the keyboard's accessory row, which a sheet
/// doesn't reliably show.
struct FormatBar<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        // As wide as its buttons where there is room for them all — a pill in the middle of the
        // page, as on an iPad — and the width of the page, scrolling, where there isn't.
        ViewThatFits(in: .horizontal) {
            row
                .fixedSize(horizontal: true, vertical: false)
                .frame(height: 46)
                .glassEffect(.regular.interactive(), in: .capsule)
            ScrollView(.horizontal) { row }
                .scrollIndicators(.hidden)
                .frame(height: 46)
                .glassEffect(.regular.interactive(), in: .capsule)
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 12)
        .padding(.bottom, 8)
    }

    private var row: some View {
        HStack(spacing: 4) { content }
            .labelStyle(.iconOnly)
            .buttonStyle(FormatButtonStyle())
            .padding(.horizontal, 10)
    }
}

private struct FormatButtonStyle: ButtonStyle {
    @Environment(\.theme) private var theme

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(theme.primary)
            .frame(width: 38, height: 38)
            .contentShape(.rect)
            .opacity(configuration.isPressed ? 0.5 : 1)
    }
}

/// A quick reference, not a manual — one row per construct, including Solo's own task-link syntax.
struct MarkdownCheatsheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    private static let rows: [(String, String)] = [
        ("# Heading", "Heading (## and ### go smaller)"),
        ("**bold**", "Bold"),
        ("*italic*", "Italic"),
        ("~~struck~~", "Strikethrough"),
        ("`code`", "Inline code"),
        ("```  …  ```", "Code block (own lines)"),
        ("- item", "Bulleted list"),
        ("1. item", "Numbered list"),
        ("> quote", "Block quote"),
        ("[title](https://url)", "Link"),
        ("||PROJ-BOARD-7||", "Link to a task by serial number"),
    ]

    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach(Self.rows, id: \.0) { syntax, result in
                        HStack {
                            Text(syntax).font(.system(.callout, design: .monospaced))
                            Spacer()
                            Text(result).font(.callout).foregroundStyle(theme.textSecondary)
                                .multilineTextAlignment(.trailing)
                        }
                        .themedRow()
                    }
                } footer: {
                    Text("A task link uses the serial number shown at the top of the task, and works across boards and projects.")
                }
            }
            .themedList()
            .navigationTitle("Markdown")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}

/// A read box that opens the editor when tapped, for descriptions.
struct MarkdownField: View {
    @Environment(\.theme) private var theme
    var text: String?
    var canWrite: Bool
    var emptyPrompt = "Add a description…"
    var onEdit: () -> Void

    var body: some View {
        Group {
            if let text = text?.nilIfBlank {
                SoloMarkdown(text: text)
            } else {
                Text(canWrite ? emptyPrompt : "No description.")
                    .foregroundStyle(theme.textSecondary)
            }
        }
        .frame(maxWidth: .infinity, minHeight: 44, alignment: .topLeading)
        .card()
        .overlay(alignment: .topTrailing) {
            if canWrite {
                Button("Edit description", systemImage: "square.and.pencil", action: onEdit)
                    .labelStyle(.iconOnly)
                    .font(.footnote)
                    .padding(8)
                    .foregroundStyle(theme.textSecondary)
            }
        }
    }
}
