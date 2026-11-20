import SwiftUI

/// What someone who has just installed the app is shown before it asks for an instance:
/// hello, then what an instance is and where one comes from. Solo is self-hosted, so the
/// app is no use until there is one, and a field asking for its address says none of that.
nonisolated enum Onboarding {
    enum Step: String, Sendable {
        case welcome, hosting, connect
    }

    /// Set once someone has said they have an instance; cleared when they ask to see this again.
    static let seenKey = "solo.onboarding.seen"

    /// The documentation's page on running an instance.
    static let installGuide = URL(string: "https://hunterahlquist.github.io/Solo/admin/install/")!

    /// Where a launch with no instance begins. Someone who has been through this, or who
    /// has connected to an instance before and so knows what one is, is asked for the
    /// address straight away; the way back to the start is on that screen.
    static func start(seen: Bool, knowsAnInstance: Bool) -> Step {
        seen || knowsAnInstance ? .connect : .welcome
    }

    #if DEBUG || PERF
    /// A step to open on whatever is kept, for looking at one: `SOLO_ONBOARDING=hosting`.
    /// Compiled out of release builds.
    static var forced: Step? {
        ProcessInfo.processInfo.environment["SOLO_ONBOARDING"].flatMap(Step.init(rawValue:))
    }
    #endif
}

/// The launch with no instance: the first-run screens, and then the one that asks for it.
struct FirstRunView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(Onboarding.seenKey) private var seen = false
    @State private var step: Onboarding.Step?
    /// Which way the next screen comes from: the right going on, the left going back.
    @State private var forward = true

    private var current: Onboarding.Step {
        if let step { return step }
        #if DEBUG || PERF
        if let forced = Onboarding.forced { return forced }
        #endif
        return Onboarding.start(seen: seen, knowsAnInstance: !model.recentsToOffer.isEmpty)
    }

    var body: some View {
        ZStack {
            AppBackground()
            Group {
                switch current {
                case .welcome:
                    WelcomeView { go(.hosting) }
                case .hosting:
                    HostingView {
                        seen = true
                        go(.connect)
                    }
                case .connect:
                    InstanceEntryView(restart: {
                        seen = false
                        go(.welcome, forward: false)
                    })
                }
            }
            // One ground under all three: a screen that brought its own would slide a second
            // copy of it across the first.
            .environment(\.backdrop, .none)
            .transition(.asymmetric(
                insertion: .move(edge: forward ? .trailing : .leading).combined(with: .opacity),
                removal: .move(edge: forward ? .leading : .trailing).combined(with: .opacity)))
            .id(current)
        }
    }

    private func go(_ next: Onboarding.Step, forward: Bool = true) {
        self.forward = forward
        withAnimation(.smooth(duration: 0.45)) { step = next }
    }
}

// MARK: Screens

/// Hello: the mark in pieces to play with, and one way on.
struct WelcomeView: View {
    @Environment(\.theme) private var theme
    var start: () -> Void

    var body: some View {
        OnboardingPage {
            StageView(extent: SoloLogo.stageExtent, build: SoloLogo.model)
        } words: {
            VStack(spacing: -2) {
                Text("Welcome to")
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
                Text("Solo")
                    .font(.system(size: 64, weight: theme.headingWeight))
            }
            .multilineTextAlignment(.center)
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isHeader)
        } actions: {
            OnboardingButton("Start", systemImage: "arrow.right", prominent: true, action: start)
        }
    }
}

/// What an instance is, where it can live, and the two things to do about it.
struct HostingView: View {
    @Environment(\.theme) private var theme
    @Environment(\.openURL) private var openURL
    var haveOne: () -> Void

    var body: some View {
        OnboardingPage {
            ZStack {
                // The drawing is behind the models: a model pulled across an arrow covers it.
                HostingDiagram()
                StageView(extent: HostingModels.extent, sway: 0, build: HostingModels.model)
            }
        } words: {
            Text("Solo is an open source self-hosted project management software. In order to use this app, you can host on a home server, a cloud provider, or even that old dusty laptop you're hoarding in your closet. You'll set it up with docker, and will need to set up an OAuth 2 provider for authentication.")
                .font(Platform.isMac ? .body : .callout)
                .foregroundStyle(theme.textSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        } actions: {
            VStack(spacing: 10) {
                OnboardingButton("Installing Solo Server", systemImage: "arrow.up.right", prominent: false) {
                    openURL(Onboarding.installGuide)
                }
                OnboardingButton("I have an Instance!", systemImage: "arrow.right", prominent: true, action: haveOne)
            }
        }
    }
}

// MARK: Parts

/// A first-run screen: something to look at, something to read, and what to press, top to
/// bottom — or, where there is no height for that (a phone on its side), the picture
/// beside the rest.
private struct OnboardingPage<Stage: View, Words: View, Actions: View>: View {
    @ViewBuilder var stage: Stage
    @ViewBuilder var words: Words
    @ViewBuilder var actions: Actions

    var body: some View {
        GeometryReader { geometry in
            let sideways = geometry.size.height < 520 && geometry.size.width > geometry.size.height
            Group {
                if sideways {
                    HStack(spacing: 20) {
                        stage
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        reading(spacing: 18)
                            .frame(width: min(380, geometry.size.width * 0.46))
                    }
                } else {
                    VStack(spacing: 20) {
                        stage
                            .frame(maxWidth: 720, maxHeight: .infinity)
                        reading(spacing: 24)
                            .frame(maxWidth: 460)
                    }
                }
            }
            .padding(.horizontal, 24)
            .padding(.top, 12)
            .padding(.bottom, 20)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    /// The words give way before the buttons do: on a small screen it is the text that scrolls.
    private func reading(spacing: CGFloat) -> some View {
        VStack(spacing: spacing) {
            ViewThatFits(in: .vertical) {
                words
                ScrollView { words }
                    .scrollBounceBehavior(.basedOnSize)
            }
            actions
        }
    }
}

/// The wide button of a first-run screen, its arrow after its words.
private struct OnboardingButton: View {
    var title: String
    var systemImage: String
    var prominent: Bool
    var action: () -> Void

    init(_ title: String, systemImage: String, prominent: Bool, action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.prominent = prominent
        self.action = action
    }

    var body: some View {
        if prominent {
            Button(action: action) { label.onAccent() }
                .buttonStyle(.glassProminent)
                .buttonBorderShape(.capsule)
        } else {
            Button(action: action) { label }
                .buttonStyle(.glass)
                .buttonBorderShape(.capsule)
        }
    }

    private var label: some View {
        HStack(spacing: 8) {
            Text(title)
            Image(systemName: systemImage)
        }
        .fontWeight(.semibold)
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }
}
