import SwiftUI

/// What someone who has just installed the app is shown before it asks for an instance:
/// hello, then what an instance is and where one comes from. Solo is self-hosted, so the
/// app is no use until there is one, and a field asking for its address says none of that.
nonisolated enum Onboarding {
    enum Step: String, Sendable, CaseIterable, Identifiable {
        case welcome, hosting, connect
        var id: Self { self }
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

    /// How many times slower than usual one screen gives way to the next, for catching it
    /// half way: `SOLO_ONBOARDING_PACE=8`.
    static var pace: Double {
        ProcessInfo.processInfo.environment["SOLO_ONBOARDING_PACE"].flatMap(Double.init) ?? 1
    }
    #else
    static let pace = 1.0
    #endif
}

/// The launch with no instance: the first-run screens, and then the one that asks for it.
struct FirstRunView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(Onboarding.seenKey) private var seen = false

    private var start: Onboarding.Step {
        #if DEBUG || PERF
        if let forced = Onboarding.forced { return forced }
        #endif
        return Onboarding.start(seen: seen, knowsAnInstance: !model.recentsToOffer.isEmpty)
    }

    var body: some View {
        ZStack {
            AppBackground()
            // Where it starts is settled once, as it appears: saying there is an instance
            // changes what the next launch starts on, not where this one is.
            OnboardingPages(start: start, seen: $seen)
        }
    }
}

/// The three screens side by side, one in view, moved between by their buttons.
///
/// A row that scrolls, and not one view replacing another: a view sliding out under a
/// transition takes its text and buttons with it and leaves its 3D where it was, since
/// what RealityKit draws is not moved by the animation that moves the rest. A scrolling
/// row moves the views themselves, and everything in them goes together.
private struct OnboardingPages: View {
    @Binding var seen: Bool
    @State private var current: Onboarding.Step
    @State private var position: ScrollPosition
    /// Whether the two screens with models have them. They are made before they are slid
    /// to, both of them and off screen: a stage made as its screen comes in isn't drawn
    /// until the screen has stopped, and arrives empty. Someone who starts at the address
    /// has none, and nothing drawn off screen while they type it, unless they ask to see
    /// the start again.
    @State private var staged: Bool
    @State private var set: Set<Onboarding.Step> = []

    init(start: Onboarding.Step, seen: Binding<Bool>) {
        _seen = seen
        _current = State(initialValue: start)
        _position = State(initialValue: ScrollPosition(id: start))
        _staged = State(initialValue: start != .connect)
    }

    var body: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 0) {
                ForEach(Onboarding.Step.allCases) { step in
                    page(step)
                        // As wide as the screen, and as tall as the row is given: a height
                        // taken from the container as well would be the whole screen's, and
                        // run under its edges.
                        .containerRelativeFrame(.horizontal)
                        .frame(maxHeight: .infinity)
                        // Only the screen in view is there to be used or read out.
                        .allowsHitTesting(step == current)
                        .accessibilityHidden(step != current)
                        .id(step)
                }
            }
            .scrollTargetLayout()
        }
        .scrollTargetBehavior(.paging)
        .scrollPosition($position)
        .scrollIndicators(.hidden)
        // The buttons are the way from one to the next: there is nothing to swipe back to
        // from a screen that has just been told there is an instance.
        .scrollDisabled(true)
        // A window that changes size keeps the screen it was showing.
        .onGeometryChange(for: CGSize.self, of: \.size) { _ in
            position.scrollTo(id: current)
        }
        // One ground under all three, drawn behind the row: a screen that brought its own
        // would carry a second copy of it across the first.
        .environment(\.backdrop, .none)
    }

    @ViewBuilder private func page(_ step: Onboarding.Step) -> some View {
        switch step {
        case .welcome:
            WelcomeView(staged: staged, onStage: { set.insert(.welcome) }) { go(.hosting) }
        case .hosting:
            HostingView(staged: staged, onStage: { set.insert(.hosting) }) {
                seen = true
                go(.connect)
            }
        case .connect:
            InstanceEntryView(restart: {
                seen = false
                go(.welcome)
            })
        }
    }

    private func go(_ next: Onboarding.Step) {
        guard next != current else { return }
        current = next
        Task {
            // Back to the start from an address screen that had no stages behind it: they
            // are made now, and the row waits for the one it is going to — briefly, and
            // not for ever.
            if next != .connect, !staged {
                staged = true
                let patience = ContinuousClock.now + .seconds(1.5)
                while !set.contains(next), ContinuousClock.now < patience {
                    try? await Task.sleep(for: .milliseconds(40))
                }
            }
            withAnimation(.smooth(duration: 0.5 * Onboarding.pace)) { position.scrollTo(id: next) }
        }
    }
}

// MARK: Screens

/// Hello: the mark in pieces to play with, and one way on.
struct WelcomeView: View {
    @Environment(\.theme) private var theme
    /// Whether the models are on stage, and what to call when they are: see `OnboardingPages`.
    var staged = true
    var onStage: () -> Void = {}
    var start: () -> Void

    var body: some View {
        OnboardingPage {
            if staged { StageView(scene: SoloLogo.stage, ready: onStage) }
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
    var staged = true
    var onStage: () -> Void = {}
    var haveOne: () -> Void

    var body: some View {
        OnboardingPage {
            ZStack {
                // The drawing is behind the models: a model pulled across an arrow covers it.
                HostingDiagram()
                if staged { StageView(scene: HostingModels.stage, ready: onStage) }
            }
        } words: {
            Text("Solo is an open source self-hosted project management software. In order to use this app, you can host on a home server, a cloud provider, or even that old dusty laptop you're hoarding in your closet. You'll set it up with docker, and will need to set up an OIDC provider for authentication.")
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
            // Clear glass, said outright: a Mac's plain glass button takes the accent as its
            // fill, and the two buttons of a screen were then one colour.
            Button(action: action) { label }
                .buttonStyle(.clearGlass)
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
