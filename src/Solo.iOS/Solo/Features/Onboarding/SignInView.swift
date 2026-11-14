import NukeUI
import SwiftUI

/// The instance's front door: its name, its tagline and its theme's login
/// artwork, with one button that opens the system sign-in sheet.
struct SignInView: View {
    @Environment(AppModel.self) private var model
    @Environment(ThemeStore.self) private var themes
    @Environment(\.theme) private var theme
    @State private var signingIn = false
    @State private var problem: String?

    var body: some View {
        ZStack {
            LoginBackground()

            VStack(spacing: 22) {
                VStack(spacing: 8) {
                    if let logo = logoURL {
                        LazyImage(url: logo) { state in
                            if let image = state.image {
                                image.resizable().scaledToFit()
                            } else {
                                wordmark
                            }
                        }
                        .frame(maxHeight: 64)
                    } else {
                        wordmark
                    }
                    Text(model.config?.siteTagline ?? "")
                        .font(.callout)
                        .foregroundStyle(theme.textSecondary)
                        .multilineTextAlignment(.center)
                }

                if let notice = model.notice {
                    Label(notice, systemImage: "info.circle")
                        .font(.footnote)
                        .foregroundStyle(theme.textSecondary)
                        .multilineTextAlignment(.center)
                }

                Button {
                    signIn()
                } label: {
                    HStack(spacing: 8) {
                        if signingIn {
                            ProgressView().tint(theme.onPrimary)
                        } else {
                            Image(systemName: "person.badge.key.fill")
                        }
                        Text("Sign in with \(model.config?.providerName ?? "single sign-on")")
                            .fontWeight(.semibold)
                            .lineLimit(2)
                            .minimumScaleFactor(0.75)
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
                    .onAccent()
                }
                .buttonStyle(.glassProminent)
                .disabled(signingIn)

                if let problem {
                    Text(problem)
                        .font(.footnote)
                        .foregroundStyle(theme.error)
                        .multilineTextAlignment(.center)
                }

                Button {
                    Task { await model.changeInstance() }
                } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "server.rack")
                        Text(model.instanceURL?.host() ?? "")
                            .lineLimit(2)
                            .minimumScaleFactor(0.75)
                        Text("· Change").fixedSize()
                    }
                    .font(.footnote)
                    .foregroundStyle(theme.textSecondary)
                }
            }
            .padding(28)
            .frame(maxWidth: 400)
            .glassEffect(.regular, in: .rect(cornerRadius: 32))
            .padding(24)
        }
    }

    private var wordmark: some View {
        Text(model.config?.siteName ?? "Solo")
            .font(.system(.largeTitle, weight: theme.headingWeight))
            .foregroundStyle(theme.textPrimary)
            .multilineTextAlignment(.center)
            // A name is shown whole. One long word — an address used as a name — can't wrap, so it
            // is set smaller until it fits rather than cut off.
            .lineLimit(4)
            .minimumScaleFactor(0.4)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var logoURL: URL? {
        guard let login = themes.document?.login, let api = model.api else { return nil }
        let path = theme.isDark ? login.logo : (login.logoLight ?? login.logo)
        return path.map(api.url)
    }

    private func signIn() {
        signingIn = true
        problem = nil
        Task {
            do {
                try await model.signIn()
            } catch {
                problem = (error as? LocalizedError)?.errorDescription ?? "Sign-in failed. Try again."
            }
            signingIn = false
        }
    }
}

/// The theme's login background, laid out by its `fit` and washed with its scrim.
struct LoginBackground: View {
    @Environment(AppModel.self) private var model
    @Environment(ThemeStore.self) private var themes
    @Environment(\.theme) private var theme

    var body: some View {
        let background = themes.document?.login?.background
        ZStack {
            (background?.color.flatMap(CSSColor.init)?.color ?? Color(uiColor: .systemBackground))
            if let background, let api = model.api,
               let path = theme.isDark ? background.image : (background.imageLight ?? background.image) {
                GeometryReader { proxy in
                    LazyImage(url: api.url(path)) { state in
                        if let image = state.image {
                            fitted(image, background.fit, in: proxy.size)
                        }
                    }
                    .frame(width: proxy.size.width, height: proxy.size.height, alignment: alignment(background.position))
                    .clipped()
                }
                // A busy picture behind the card is the fastest way to make it unreadable.
                Color(uiColor: .systemBackground).opacity(background.scrim ?? 0)
            }
        }
        .ignoresSafeArea()
    }

    @ViewBuilder
    private func fitted(_ image: Image, _ fit: String?, in size: CGSize) -> some View {
        switch fit?.lowercased() {
        case "contain":
            image.resizable().scaledToFit()
        case "width":
            image.resizable().aspectRatio(contentMode: .fit).frame(width: size.width)
        case "height":
            image.resizable().aspectRatio(contentMode: .fit).frame(height: size.height).fixedSize()
        case "stretch":
            image.resizable().frame(width: size.width, height: size.height)
        default:
            image.resizable().scaledToFill()
        }
    }

    private func alignment(_ position: String?) -> Alignment {
        let value = position?.lowercased() ?? "center"
        let vertical: VerticalAlignment = value.contains("top") ? .top : value.contains("bottom") ? .bottom : .center
        let horizontal: HorizontalAlignment = value.contains("left") ? .leading : value.contains("right") ? .trailing : .center
        return Alignment(horizontal: horizontal, vertical: vertical)
    }
}
