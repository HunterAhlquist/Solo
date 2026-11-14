import SwiftUI

/// One image, as large as the screen allows, with pinch to look closer.
struct DocPictureViewer: View {
    @Environment(\.dismiss) private var dismiss
    var picture: UIImage
    var name: String

    @State private var zoom: CGFloat = 1
    @GestureState private var pinch: CGFloat = 1

    var body: some View {
        NavigationStack {
            GeometryReader { proxy in
                ScrollView([.horizontal, .vertical]) {
                    Image(uiImage: picture)
                        .resizable()
                        .scaledToFit()
                        .frame(width: proxy.size.width * zoom * pinch, height: proxy.size.height * zoom * pinch)
                }
                .scrollIndicators(.hidden)
                .gesture(MagnifyGesture().updating($pinch) { value, state, _ in state = value.magnification }
                    .onEnded { zoom = min(max(zoom * $0.magnification, 1), 6) })
                .onTapGesture(count: 2) { withAnimation(.snappy) { zoom = zoom > 1 ? 1 : 2.5 } }
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
