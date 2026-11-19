import SwiftUI

// The app keeps any number of instances, each signed in apart, and shows one. These are the
// ways between them: the list on the account page (a Mac's Settings window), the menu on a
// screen that isn't the app — the sign-in page, or one saying the instance can't be reached —
// and the sheet that adds one. A Mac has them in its menu bar as well (`SoloCommands`).

/// Every instance kept on this device: the one showing marked, the others a tap away.
struct InstancesSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.theme) private var theme
    @State private var confirmation: Confirmation?
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    #endif

    var body: some View {
        Section {
            ForEach(model.instances) { instance in
                row(instance)
            }
            Button("Add Instance", systemImage: "plus") {
                // The sheet is the main window's, which on a Mac this page is not in.
                #if os(macOS)
                openWindow(id: "main")
                #endif
                model.addingInstance = true
            }
        } header: {
            Text("Instances")
        } footer: {
            if model.instances.count > 1 {
                Text("Each instance stays signed in. The one showing is live; the others are asked for what's unread "
                    + "every few minutes.")
            }
        }
        .themedRow()
        .confirmation($confirmation)
        .task { await model.refreshElsewhere() }
    }

    @ViewBuilder
    private func row(_ instance: SavedInstance) -> some View {
        #if os(macOS)
        // A Mac has no swipe to find, and a right click is easy not to: the way to remove
        // one is in the row.
        HStack(spacing: 12) {
            choose(instance)
            Button("Remove", systemImage: "minus.circle") { remove(instance) }
                .labelStyle(.iconOnly)
                .buttonStyle(.borderless)
                .foregroundStyle(theme.textSecondary)
                .help("Remove \(instance.name)")
        }
        .contextMenu {
            Button("Remove", systemImage: "trash", role: .destructive) { remove(instance) }
        }
        #else
        choose(instance)
            .swipeActions {
                // Not the destructive role: that takes the row away as the swipe ends, before the
                // question it leads to has been answered.
                Button("Remove", systemImage: "trash") { remove(instance) }
                    .tint(theme.error)
            }
            .contextMenu {
                Button("Remove", systemImage: "trash", role: .destructive) { remove(instance) }
            }
        #endif
    }

    private func choose(_ instance: SavedInstance) -> some View {
        let showing = instance.url == model.instanceURL
        let unread = showing ? 0 : model.unreadElsewhere[instance.url] ?? 0
        return Button {
            Task { await model.switchTo(instance.url) }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: "server.rack")
                    .foregroundStyle(theme.primary)
                    .frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(instance.name)
                        .foregroundStyle(theme.textPrimary)
                    Text(instance.signedInAs == nil ? "\(instance.address) · signed out" : instance.address)
                        .font(.caption)
                        .foregroundStyle(theme.textSecondary)
                }
                .lineLimit(1)
                Spacer(minLength: 8)
                if unread > 0 {
                    Badge(text: "\(unread)", color: theme.accentWash, foreground: theme.primary)
                        .accessibilityLabel("\(unread) unread")
                }
                if showing {
                    Image(systemName: "checkmark")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(theme.primary)
                }
            }
            .contentShape(.rect)
        }
        .accessibilityAddTraits(showing ? .isSelected : [])
    }

    private func remove(_ instance: SavedInstance) {
        confirmation = Confirmation(
            title: "Remove \(instance.name)?",
            message: instance.signedInAs == nil
                ? "\(instance.address) will no longer be listed here."
                : "You'll be signed out of \(instance.address) on this device.",
            action: "Remove") {
                await model.remove(instance.url)
            }
    }
}

/// The list as a page of its own: a section of a Mac's Settings window.
struct InstancesView: View {
    var body: some View {
        GlassList {
            InstancesSection()
        }
        .themedList()
        .navigationTitle("Instances")
    }
}

/// The way to another instance from a screen that isn't the app. With nothing else kept and
/// nothing to remove it is one button, to the screen that asks for an address; otherwise a
/// menu of the others, of adding one, and of dropping this one.
struct InstanceChoice<Label: View>: View {
    @Environment(AppModel.self) private var model
    @ViewBuilder var label: Label

    var body: some View {
        if model.instances.isEmpty {
            Button {
                Task { if let url = model.instanceURL { await model.remove(url) } }
            } label: {
                label
            }
        } else {
            Menu {
                ForEach(model.otherInstances) { instance in
                    Button(model.label(for: instance), systemImage: "server.rack") {
                        Task { await model.switchTo(instance.url) }
                    }
                }
                Divider()
                Button("Add Instance…", systemImage: "plus") { model.addingInstance = true }
                if let current = model.current {
                    Button("Remove This Instance", systemImage: "trash", role: .destructive) {
                        Task { await model.remove(current.url) }
                    }
                } else if let url = model.instanceURL {
                    // An address that never answered: there is nothing kept of it to remove.
                    Button("Forget \(url.host() ?? "This Address")", systemImage: "xmark") {
                        Task { await model.remove(url) }
                    }
                }
            } label: {
                label
            }
        }
    }
}

/// Adding an instance beside the ones already kept: the first-launch screen in a sheet, with
/// a way out, over whatever is showing.
struct AddInstanceSheet: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            InstanceEntryView(adding: true)
                .navigationTitle("Add Instance")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Cancel") { dismiss() }
                    }
                }
        }
    }
}
