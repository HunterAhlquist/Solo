import SwiftUI

struct SavedInstance: Codable, Equatable, Identifiable {
    let url: URL
    let name: String

    var id: URL { url }
}

/// The instances the app knows, and the one it opens. Kept in UserDefaults;
/// each instance's own data (its session included) stays in the web view's
/// storage for that origin, so switching back finds it still signed in.
enum SavedInstances {
    private static let listKey = "solo.instances"
    private static let currentKey = "solo.instances.current"

    static var all: [SavedInstance] {
        get {
            UserDefaults.standard.data(forKey: listKey)
                .flatMap { try? JSONDecoder().decode([SavedInstance].self, from: $0) } ?? []
        }
        set { UserDefaults.standard.set(try? JSONEncoder().encode(newValue), forKey: listKey) }
    }

    static var current: URL? {
        get { UserDefaults.standard.url(forKey: currentKey) }
        set { UserDefaults.standard.set(newValue, forKey: currentKey) }
    }

    /// The instance to open. One built in with `SOLO_INSTANCE_URL` joins the
    /// list the first time a build carries it, and is opened then.
    static func resolve(builtIn: URL?) -> URL? {
        if let builtIn, !all.contains(where: { $0.url == builtIn }) {
            all.append(SavedInstance(url: builtIn, name: builtIn.host ?? builtIn.absoluteString))
            current = builtIn
        }

        if let current, all.contains(where: { $0.url == current }) {
            return current
        }

        return all.first?.url
    }

    /// What a person types, with or without the scheme, as the instance's
    /// address: https unless they say otherwise, with no query or trailing
    /// slash, keeping a path for an instance hosted under one. The same rules
    /// as the desktop app's normalizeInstanceUrl.
    static func normalize(_ input: String) -> URL? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return nil }
        if !text.lowercased().hasPrefix("http://") && !text.lowercased().hasPrefix("https://") {
            text = "https://" + text
        }

        guard var components = URLComponents(string: text), components.host?.isEmpty == false else { return nil }
        components.scheme = components.scheme?.lowercased()
        components.query = nil
        components.fragment = nil
        while components.path.hasSuffix("/") { components.path.removeLast() }
        return components.url
    }

    /// Asks the address what it is, so a typo fails here rather than as a
    /// blank page.
    static func probe(_ url: URL) async throws -> SavedInstance {
        var request = URLRequest(url: url.appendingPathComponent("api/config/auth"))
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.timeoutInterval = 8

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch {
            throw AppSignIn.Failure(errorDescription: "Couldn't reach \(url.host ?? "that address").")
        }

        guard let status = (response as? HTTPURLResponse)?.statusCode, (200..<300).contains(status) else {
            throw AppSignIn.Failure(errorDescription: "The server didn't answer like a Solo instance.")
        }
        guard let config = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              config["setupCompleted"] is Bool
        else {
            throw AppSignIn.Failure(errorDescription: "The server answered, but not like a Solo instance.")
        }

        let name = (config["displayName"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        return SavedInstance(url: url, name: name ?? url.host ?? url.absoluteString)
    }
}

/// The native sheet for choosing an instance, adding one or removing one.
/// Opening one reloads the app on it. With none open it is the app's first
/// screen, and it can't be dismissed until one is (see showInstances).
struct InstancesView: View {
    /// Where someone without an instance finds out how to run one.
    static let installGuide = URL(string: "https://hunterahlquist.github.io/Solo/admin/install/")!

    let current: URL?
    let onOpen: (SavedInstance) -> Void
    let onDone: (() -> Void)?

    @State private var instances = SavedInstances.all
    @State private var address = ""
    @State private var error: String?
    @State private var adding = false

    var body: some View {
        NavigationView {
            Form {
                if current == nil {
                    Section {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Add your first instance").font(.title2.bold())
                            Text("Solo works with a Solo instance: the server you or your team run. Enter its address below to get started.")
                                .foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 4)
                        Link("Don't have one? Set up an instance", destination: Self.installGuide)
                    }
                }

                if !instances.isEmpty {
                    Section {
                        ForEach(instances) { instance in
                            Button {
                                if instance.url != current { onOpen(instance) }
                            } label: {
                                HStack {
                                    VStack(alignment: .leading) {
                                        Text(instance.name).foregroundStyle(.primary)
                                        Text(instance.url.absoluteString).font(.caption).foregroundStyle(.secondary)
                                    }
                                    Spacer()
                                    if instance.url == current {
                                        Image(systemName: "checkmark").foregroundStyle(.tint)
                                    }
                                }
                            }
                            // The open one can't be removed from under itself.
                            .deleteDisabled(instance.url == current)
                        }
                        .onDelete { offsets in
                            instances.remove(atOffsets: offsets)
                            SavedInstances.all = instances
                        }
                    }
                }

                Section {
                    TextField("solo.example.com", text: $address)
                        .keyboardType(.URL)
                        .textContentType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.go)
                        .onSubmit(add)
                    Button(adding ? "Connecting…" : "Add and open", action: add)
                        .disabled(adding || address.trimmingCharacters(in: .whitespaces).isEmpty)
                } header: {
                    Text("Add an instance")
                } footer: {
                    if let error {
                        Text(error).foregroundStyle(.red)
                    } else {
                        Text("The address of a Solo server, as you'd open it in a browser.")
                    }
                }
            }
            .navigationTitle(current == nil ? "Welcome to Solo" : "Instances")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                // The condition sits inside the item: an `if` directly in the
                // toolbar builder needs iOS 16.
                ToolbarItem(placement: .confirmationAction) {
                    if let onDone {
                        Button("Done", action: onDone)
                    }
                }
            }
        }
        .navigationViewStyle(.stack)
    }

    private func add() {
        guard !adding else { return }
        guard let url = SavedInstances.normalize(address) else {
            error = "Enter a valid http(s) address."
            return
        }
        if let existing = instances.first(where: { $0.url == url }) {
            onOpen(existing)
            return
        }

        adding = true
        error = nil
        Task {
            defer { adding = false }
            do {
                let instance = try await SavedInstances.probe(url)
                SavedInstances.all.append(instance)
                onOpen(instance)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}
