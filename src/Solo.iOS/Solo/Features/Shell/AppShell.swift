import SwiftUI

struct AppShell: View {
    @Environment(Session.self) private var session

    var body: some View {
        Text("Signed in as \(session.user.name)")
    }
}
