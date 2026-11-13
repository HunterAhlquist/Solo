import PhotosUI
import SwiftUI

struct NameIconValue {
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
}

/// Create or edit the two things that have a name, an alias and an icon:
/// projects and boards.
struct NameIconSheet<Extra: View>: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    var title: String
    var saveLabel: String
    var onSave: (NameIconValue) async -> Bool
    @ViewBuilder var extra: Extra

    @State private var name: String
    @State private var alias: String
    @State private var icon: String?
    @State private var imageId: UUID?
    /// Until the alias is typed by hand it follows the name.
    @State private var aliasEdited: Bool
    @State private var saving = false
    @FocusState private var nameFocused: Bool

    init(title: String, initial: NameIconValue? = nil, saveLabel: String? = nil,
         onSave: @escaping (NameIconValue) async -> Bool, @ViewBuilder extra: () -> Extra) {
        self.title = title
        self.saveLabel = saveLabel ?? (initial == nil ? "Create" : "Save")
        self.onSave = onSave
        self.extra = extra()
        _name = State(initialValue: initial?.name ?? "")
        _alias = State(initialValue: initial?.alias ?? "")
        _icon = State(initialValue: initial?.icon)
        _imageId = State(initialValue: initial?.imageId)
        _aliasEdited = State(initialValue: initial != nil)
    }

    private var normalizedAlias: String { Aliases.normalize(alias) }
    private var aliasValid: Bool { Aliases.isValid(normalizedAlias) }
    private var canSave: Bool { name.nilIfBlank != nil && aliasValid && !saving }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Name", text: $name)
                        .focused($nameFocused)
                        .onChange(of: name) { _, value in
                            if !aliasEdited { alias = Aliases.suggest(from: value) }
                        }
                    TextField("Alias", text: Binding(get: { alias }, set: { value in
                        alias = String(value.uppercased().prefix(Aliases.maxLength))
                        aliasEdited = true
                    }))
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                    .font(.body.monospaced())
                } footer: {
                    if !alias.isEmpty && !aliasValid {
                        Text(Aliases.patternError).foregroundStyle(theme.error)
                    } else {
                        Text("Stands in for the id in links and serial numbers — capital letters and digits, max 8.")
                    }
                }
                .themedRow()

                Section("Icon") {
                    IconImagePicker(icon: $icon, imageId: $imageId)
                }
                .themedRow()

                extra
            }
            .themedList()
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(saveLabel) {
                        saving = true
                        Task {
                            let value = NameIconValue(
                                name: String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(FieldLimits.entityName)),
                                alias: normalizedAlias, icon: icon, imageId: imageId)
                            if await onSave(value) {
                                dismiss()
                            }
                            saving = false
                        }
                    }
                    .fontWeight(.semibold)
                    .disabled(!canSave)
                }
            }
            .onAppear { nameFocused = name.isEmpty }
        }
    }
}

extension NameIconSheet where Extra == EmptyView {
    init(title: String, initial: NameIconValue? = nil, saveLabel: String? = nil,
         onSave: @escaping (NameIconValue) async -> Bool) {
        self.init(title: title, initial: initial, saveLabel: saveLabel, onSave: onSave) { EmptyView() }
    }
}

/// One of the curated symbols, or a picture of your own. A picture wins over a symbol.
struct IconImagePicker: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Binding var icon: String?
    @Binding var imageId: UUID?

    @State private var photo: PhotosPickerItem?
    @State private var uploading = false

    private let columns = [GridItem(.adaptive(minimum: 44), spacing: 8)]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                EntityIcon(imageId: imageId, icon: icon, size: 40)
                    .foregroundStyle(theme.primary)
                    .frame(width: 56, height: 56)
                    .background(theme.backgroundGray, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                VStack(alignment: .leading, spacing: 6) {
                    PhotosPicker(selection: $photo, matching: .images) {
                        Label(imageId == nil ? "Use a Picture" : "Change Picture", systemImage: "photo")
                    }
                    .buttonStyle(.glass)
                    if imageId != nil {
                        Button("Remove Picture", systemImage: "xmark", role: .destructive) { imageId = nil }
                            .font(.footnote)
                            .buttonStyle(.plain)
                            .foregroundStyle(theme.error)
                    }
                }
                if uploading {
                    ProgressView()
                }
            }

            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(SoloIcons.all, id: \.name) { entry in
                    let selected = imageId == nil && icon?.lowercased() == entry.name.lowercased()
                    Button {
                        icon = selected ? nil : entry.name
                        imageId = nil
                    } label: {
                        Image(systemName: entry.symbol)
                            .font(.system(size: 18))
                            .frame(width: 44, height: 44)
                            .foregroundStyle(selected ? theme.onPrimary : theme.textPrimary)
                            .background(selected ? theme.primary : theme.backgroundGray,
                                        in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(entry.name)
                    .accessibilityAddTraits(selected ? .isSelected : [])
                }
            }
        }
        .padding(.vertical, 4)
        .onChange(of: photo) { _, item in
            guard let item else { return }
            Task { await upload(item) }
        }
    }

    /// The API takes PNG or JPEG up to 1 MB. A photo is far bigger than an
    /// icon needs, so it is scaled to icon size first and sent as PNG.
    private func upload(_ item: PhotosPickerItem) async {
        uploading = true
        defer { uploading = false; photo = nil }
        guard let data = try? await item.loadTransferable(type: Data.self), let image = UIImage(data: data) else {
            session.toasts.show("That picture couldn't be read.", .error)
            return
        }
        let side: CGFloat = 256
        let scale = max(side / image.size.width, side / image.size.height)
        let size = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let png = UIGraphicsImageRenderer(size: CGSize(width: side, height: side), format: format).pngData { _ in
            image.draw(in: CGRect(x: (side - size.width) / 2, y: (side - size.height) / 2, width: size.width, height: size.height))
        }
        if let asset = await session.save({
            try await session.api.uploadImage(png, fileName: "icon.png", contentType: "image/png")
        }) {
            imageId = asset.id
        }
    }
}
