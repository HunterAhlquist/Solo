import SwiftUI

/// One image, as large as the screen allows, with pinch to look closer.
struct DocPictureViewer: View {
    @Environment(\.dismiss) private var dismiss
    var picture: UIImage
    var name: String

    var body: some View {
        NavigationStack {
            GeometryReader { proxy in
                ZoomablePicture(picture: picture, bars: proxy.safeAreaInsets)
                    .ignoresSafeArea()
            }
            .background(.black)
            .navigationTitle(name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close", systemImage: "xmark") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    ShareLink(item: Image(uiImage: picture), preview: SharePreview(name, image: Image(uiImage: picture)))
                }
            }
        }
    }
}
