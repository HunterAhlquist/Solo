import RealityKit
import SwiftUI

/// What someone who has just installed the app is shown before it asks for an instance:
/// hello, then what an instance is and where one comes from. Solo is self-hosted, so the
/// app is no use until there is one, and a field asking for its address says none of that.
nonisolated enum Onboarding {
    enum Step: String, Sendable, CaseIterable, Identifiable {
        case welcome, hosting, connect
        var id: Self { self }
        /// Which screen it is along the row, from the left.
        var index: Int { Self.allCases.firstIndex(of: self) ?? 0 }
    }

    /// Set once someone has said they have an instance; cleared when they ask to see this again.
    static let seenKey = "solo.onboarding.seen"

    /// Whether the first run's 3D has been shown and taken away in this run of the app.
    /// It is shown once: what RealityKit draws in a view made after another has gone is
    /// never put on screen, so a second set of stages would be pictures of nothing. Someone
    /// who comes back to these screens in the same run has them flat.
    @MainActor static var spent = false

    /// The name of the space each screen measures itself in: its own, from its own top
    /// left corner, which is where the face's places on it are given.
    static let pageSpace = "onboarding.page"

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
/// The screens are a row, moved by hand a frame at a time, and their 3D is not in them. It
/// is all on one stage, in one view over the row (`Stage.swift` says why one), and each
/// screen leaves room where its part of it goes and says where that room is. The row, what
/// is on the stage and the face all take their place from the one clock
/// (`OnboardingGuide`), so they keep step.
private struct OnboardingPages: View {
    @Binding var seen: Bool
    @State private var current: Onboarding.Step
    /// Whether the stage is there. It is from the start for someone starting at the start;
    /// someone who starts at the address has none, and nothing drawn while they type it,
    /// unless they ask to see the start again. Once there, it stays.
    @State private var staged: Bool
    /// Whether there is any 3D to be had this time: see `Onboarding.spent`.
    @State private var solid: Bool
    @State private var rig = StageRig()
    /// The face, and the clock everything here moves by.
    @State private var guide: OnboardingGuide
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(start: Onboarding.Step, seen: Binding<Bool>) {
        _seen = seen
        _current = State(initialValue: start)
        _solid = State(initialValue: !Onboarding.spent)
        _staged = State(initialValue: !Onboarding.spent && start != .connect)
        _guide = State(initialValue: OnboardingGuide(start: start))
    }

    var body: some View {
        GeometryReader { geometry in
            // SwiftUI's, said in full: the roadmap has a view of its own by that name.
            SwiftUI.TimelineView(.animation(paused: guide.flight == nil)) { timeline in
                // Which screen is in view, as a number: between two of them on the way.
                let at = guide.page(at: timeline.date)
                let inset = geometry.safeAreaInsets
                // A screen stops short of whatever is at the sides of the device's own (a
                // camera, on a phone opened out or on its side), and the next one along is
                // kept as far off as that, or its near edge would show in the space.
                let gap = inset.leading + inset.trailing
                let stride = geometry.size.width + gap
                ZStack(alignment: .topLeading) {
                    HStack(spacing: gap) {
                        ForEach(Onboarding.Step.allCases) { step in
                            page(step)
                                .frame(width: geometry.size.width, height: geometry.size.height)
                                // Only the screen in view is there to be used or read out.
                                .allowsHitTesting(step == current)
                                .accessibilityHidden(step != current)
                                .coordinateSpace(.named(Onboarding.pageSpace))
                        }
                    }
                    .frame(width: geometry.size.width, height: geometry.size.height, alignment: .leading)
                    .offset(x: -at * stride)

                    if staged {
                        StageView(rig: rig, groups: groups(at: at, date: timeline.date, stride: stride),
                                  origin: CGPoint(x: inset.leading, y: inset.top), ready: { guide.ready = true })
                            // Out to the edges of the screen, past where the screens themselves
                            // stop: a piece pulled toward an edge is not cut off short of it.
                            .padding(EdgeInsets(top: -inset.top, leading: -inset.leading, bottom: -inset.bottom, trailing: -inset.trailing))
                    } else if !solid {
                        FlatGuyLayer(guide: guide)
                    }
                }
            }
        }
        // One ground under all three, drawn behind the row: a screen that brought its own
        // would carry a second copy of it across the first.
        .environment(\.backdrop, .none)
        .environment(\.onboardingIsFlat, !solid)
        .environment(\.stageRig, staged ? rig : nil)
        .environment(guide)
        .task {
            guide.reduceMotion = reduceMotion
            // The face knows when it is picked up and put down.
            rig.onGrab = { [guide] piece in if piece == "Guy" { guide.faceTaken() } }
            rig.onRelease = { [guide] piece in if piece == "Guy" { guide.faceLetGo() } }
            guide.begin()
        }
        .onDisappear {
            if staged { Onboarding.spent = true }
        }
    }

    /// What is on the stage and where, with the row of screens at `at`: each screen's
    /// models in the room that screen left, as far to one side as the screen itself is,
    /// and the face wherever it has got to.
    private func groups(at: Double, date: Date, stride: CGFloat) -> [StageGroup] {
        // Something whose place isn't known yet is kept well out of sight.
        let nowhere = CGRect(x: -6000, y: 0, width: 300, height: 300)
        var groups = [Onboarding.Step.welcome, .hosting].map { step in
            StageGroup(id: step.rawValue, scene: step == .welcome ? SoloLogo.stage : HostingModels.stage,
                       rect: guide.slots[step]?.offsetBy(dx: (CGFloat(step.index) - at) * stride, dy: 0) ?? nowhere)
        }
        let place = guide.place(at: date)
        // Once the picture of it has come up on the last screen, the face itself is put away.
        let look = Guy.Look(pose: guide.shown, flat: Float(place?.flat ?? 0), tilt: place?.tilt ?? simd_quatf(ix: 0, iy: 0, iz: 0, r: 1),
                            hidden: guide.settled || place == nil)
        groups.append(StageGroup(id: "guy", scene: Guy.stage(look), rect: place?.rect ?? nowhere))
        return groups
    }

    @ViewBuilder private func page(_ step: Onboarding.Step) -> some View {
        switch step {
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
                go(.welcome)
            })
        }
    }

    private func go(_ next: Onboarding.Step) {
        guard next != current else { return }
        current = next
        Task {
            // Back to the start from an address screen with no stage behind it: it is made
            // now, and waited for — briefly, and not for ever.
            if next != .connect, !staged, solid {
                staged = true
                let patience = ContinuousClock.now + .seconds(1.5)
                while !guide.ready, ContinuousClock.now < patience {
                    try? await Task.sleep(for: .milliseconds(40))
                }
            }
            guide.go(to: next, over: reduceMotion ? 0.01 : 0.5 * Onboarding.pace)
        }
    }
}

extension EnvironmentValues {
    /// True when the first run is shown without its 3D: see `Onboarding.spent`.
    @Entry var onboardingIsFlat = false
    /// The stage, for a screen to pass on the touches that are meant for what is on it.
    @Entry var stageRig: StageRig? = nil
}

// MARK: The face

/// The face's part in the first run: where it is, what it is doing, and how much it has said.
///
/// On the first screen it is by the mark, smiling. Going to the second it comes down to the
/// corner over the words, which are in a speech bubble pointing at it and are not there
/// yet: it says them, a letter at a time, and when it has finished it waits, blinking now
/// and then. Going to the third it smiles again and comes to rest beside "Restart
/// Onboarding", flat on the page, where a picture of it takes its place.
@MainActor @Observable final class OnboardingGuide {
    static let speech = "Solo is an open source self-hosted project management software. In order to use this app, you can host on a home server, a cloud provider, or even that old dusty laptop you're hoarding in your closet. You'll set it up with docker, and will need to set up an OIDC provider for authentication."

    /// Where the face goes on each screen, in the screen's own space.
    var anchors: [Onboarding.Step: CGRect] = [:]
    /// The room each screen leaves for its part of the stage, in the screen's own space.
    var slots: [Onboarding.Step: CGRect] = [:]
    /// Whether the face's stage is up, and there is a face to move.
    var ready = false
    var reduceMotion = false

    private(set) var step: Onboarding.Step
    private(set) var pose = GuyPose.smile
    /// How many letters of what it has to say are said.
    private(set) var typed = 0
    /// At rest on the last screen, where the picture of it is shown.
    private(set) var landed: Bool
    /// A moment after that: the picture is all there, and the face itself is put away.
    private(set) var settled: Bool
    private(set) var flight: Flight?
    @ObservationIgnored private var script: Task<Void, Never>?

    /// In somebody's hand, or let go of within the last little while.
    private(set) var tapped = false
    @ObservationIgnored private var lettingGo: Task<Void, Never>?

    /// How long after it is let go the face goes on looking as it did in hand.
    nonisolated static let tapLingers = Duration.seconds(2)

    /// What the face looks like just now: what it is doing (`pose`), unless somebody has
    /// hold of it or has just let go, when it makes the face of something being prodded.
    /// Under that it goes on with what it was doing, which is what it comes back to —
    /// blinking where it had got to, or smiling on a screen it has since been taken to.
    /// It does not break off talking for it: a face that is saying something says it.
    var shown: GuyPose {
        if case .talk = pose { return pose }
        return tapped ? .tap : pose
    }

    /// Somebody took hold of the face. Again before it has got over the last time, and it
    /// starts over: the two seconds are counted from the last letting go.
    func faceTaken() {
        if case .talk = pose { return }
        lettingGo?.cancel()
        tapped = true
    }

    /// They let go of it. It is itself again a little while after.
    func faceLetGo(lingering: Duration = OnboardingGuide.tapLingers) {
        guard tapped else { return }
        lettingGo?.cancel()
        lettingGo = Task {
            try? await Task.sleep(for: lingering)
            if !Task.isCancelled { tapped = false }
        }
    }

    /// On its way from one screen's place to the next's.
    struct Flight {
        var from: Place
        /// Which screen was in view as it set off, as `page(at:)` gives it.
        var fromPage: Double
        var to: Onboarding.Step
        var start: Date
        /// How long the screens take to change places.
        var seconds: Double

        /// How many times longer than the screens the face takes over it. The screens
        /// slide and are there; the face is something alive crossing the room, which
        /// sets off with them and comes down a moment after they have stopped.
        static let linger = 1.7

        /// How long the face's own way takes.
        var faceSeconds: Double { seconds * Self.linger }

        /// How much of a stretch of time has gone by at a moment, from 0 to 1.
        func share(at date: Date, of length: Double) -> Double {
            min(max(date.timeIntervalSince(start) / max(length, 0.001), 0), 1)
        }

        /// How far along the screens are at a moment, from 0 to 1: away slowly, and in slowly.
        func along(at date: Date) -> Double { OnboardingGuide.eased(share(at: date, of: seconds)) }
    }

    /// Where the face is, how flat on the page, and which way it is turned.
    struct Place {
        var rect: CGRect
        var flat: Double
        var tilt: simd_quatf
    }

    init(start: Onboarding.Step) {
        step = start
        landed = start == .connect
        settled = start == .connect
    }

    /// The turn it has on each screen: as the mark's other pieces are on the first, toward
    /// its words on the second, and none on the third, where it is a picture.
    static func tilt(on step: Onboarding.Step) -> simd_quatf {
        switch step {
        case .welcome: Stage.turn(8, -14, -9)
        case .hosting: Stage.turn(5, 13, -5)
        case .connect: simd_quatf(ix: 0, iy: 0, iz: 0, r: 1)
        }
    }

    private func rest(on step: Onboarding.Step) -> Place? {
        anchors[step].map { Place(rect: $0, flat: step == .connect ? 1 : 0, tilt: Self.tilt(on: step)) }
    }

    /// Where the face is at a moment: at rest on its screen, or that far along its way.
    func place(at date: Date) -> Place? {
        guard let flight else { return rest(on: step) }
        guard let end = rest(on: flight.to) else { return flight.from }
        return Self.flying(from: flight.from, to: end, share: flight.share(at: date, of: flight.faceSeconds))
    }

    /// Away slowly, and in slowly.
    nonisolated static func eased(_ share: Double) -> Double { share * share * (3 - 2 * share) }

    /// How far through a flight the face is over the place it is going to; what is left is its landing.
    nonisolated static let arrival = 0.82

    /// Where the face is a share of the way through a flight from one place to another.
    ///
    /// Not a straight line at an even pace, which is how a thing is moved and not how one
    /// goes. It lifts off before it travels and comes in from above, on a curve; it is a
    /// little nearer the eye in the middle of the way than at either end; it turns to look
    /// where it is going and leans into it; and it sinks a touch past its place as it lands
    /// and comes back up. All of that is gone by the end, where it is exactly the place it
    /// was sent to — the last one is a picture, which a face out of true would not match.
    static func flying(from: Place, to: Place, share: Double) -> Place {
        let t = min(max(share, 0), 1)
        // It is over its place a little before the end, and the rest is the landing.
        let eased = eased(min(t / Self.arrival, 1))
        let a = CGPoint(x: from.rect.midX, y: from.rect.midY), b = CGPoint(x: to.rect.midX, y: to.rect.midY)
        let across = b.x - a.x, down = b.y - a.y
        let distance = (across * across + down * down).squareRoot()
        // How much is made of it goes by how far there is to go: over no distance at all
        // it stays where it is, with none of this.
        let much = min(distance / 220, 1)
        let lift = min(distance * 0.3, 150)

        // A curve from where it was to where it is going, pulled upward at both ends: up
        // and a little back from the way it is headed at the start, and at the end from
        // above its place and a little beyond it.
        let first = CGPoint(x: a.x - across * 0.08, y: a.y - lift)
        let second = CGPoint(x: b.x + across * 0.12, y: b.y - lift * 0.9)
        let u = 1 - eased
        var centre = CGPoint(
            x: u * u * u * a.x + 3 * u * u * eased * first.x + 3 * u * eased * eased * second.x + eased * eased * eased * b.x,
            y: u * u * u * a.y + 3 * u * u * eased * first.y + 3 * u * eased * eased * second.y + eased * eased * eased * b.y)
        // Landing: its weight carries it down past its place by a little, and it comes
        // back up, setting off and coming to rest gently.
        let landing = min(max((t - Self.arrival) / (1 - Self.arrival), 0), 1)
        centre.y += min(lift * 0.1, 9) * pow(sin(.pi * landing), 2)

        // Nearer the eye in the middle of the way.
        let swell = 1 + 0.16 * much * sin(.pi * t)
        let width = (from.rect.width + (to.rect.width - from.rect.width) * eased) * swell
        let height = (from.rect.height + (to.rect.height - from.rect.height) * eased) * swell

        // Turned toward where it is going, leaning into it, its chin up as it leaves and
        // down as it arrives.
        let way: Float = across >= 0 ? 1 : -1
        let middle = Float(much * sin(.pi * t))
        let lean = Stage.turn(Float(much) * -9 * Float(sin(2 * .pi * t)), way * 24 * middle, way * -14 * middle)

        // It lies down flat late in the way, and stands up from flat early: for most of
        // the way it is a solid thing.
        let flattening = to.flat >= from.flat ? min(max((t - 0.5) / 0.5, 0), 1) : min(t / 0.5, 1)

        return Place(
            rect: CGRect(x: centre.x - width / 2, y: centre.y - height / 2, width: width, height: height),
            flat: from.flat + (to.flat - from.flat) * Self.eased(flattening),
            tilt: simd_slerp(from.tilt, to.tilt, Float(eased)) * lean)
    }

    /// Which screen is in view at a moment, counted from the left: a whole number at rest,
    /// and between two on the way from one to the other. The row of screens, their stages
    /// and the face are all placed by this, so they keep step.
    func page(at date: Date) -> Double {
        guard let flight else { return Double(step.index) }
        return flight.fromPage + (Double(flight.to.index) - flight.fromPage) * flight.along(at: date)
    }

    /// What it does on the screen it starts on, when that is not the first.
    func begin() {
        guard script == nil, step == .hosting else { return }
        script = Task { await self.speak() }
    }

    /// Sends it to another screen's place, to be there in `seconds`, as the screens slide.
    func go(to next: Onboarding.Step, over seconds: Double) {
        script?.cancel()
        let now = Date.now
        let page = page(at: now)
        // With nowhere known to come from it starts where it is going, and only the screens move.
        let from = place(at: now) ?? rest(on: next) ?? Place(rect: .zero, flat: 0, tilt: Self.tilt(on: next))
        flight = Flight(from: from, fromPage: page, to: next, start: now, seconds: seconds)
        pose = .smile
        landed = false
        settled = false
        if next == .hosting { typed = 0 }
        script = Task {
            // Until the face is down, which is after the screens have stopped.
            try? await Task.sleep(for: .seconds(seconds * Flight.linger))
            guard !Task.isCancelled else { return }
            step = next
            flight = nil
            switch next {
            case .welcome: break
            case .hosting: await speak()
            case .connect:
                landed = true
                try? await Task.sleep(for: .milliseconds(260))
                if !Task.isCancelled { settled = true }
            }
        }
    }

    /// All of it at once, for someone who would sooner read than be read to.
    func skip() {
        guard step == .hosting, flight == nil, typed < Self.speech.count else { return }
        script?.cancel()
        typed = Self.speech.count
        script = Task { await self.wait() }
    }

    /// Says what it has to, a letter at a time, its mouth going as the letters come and
    /// shut at every pause, as a line of speech is given in a game.
    private func speak() async {
        let letters = Array(Self.speech)
        if reduceMotion { typed = letters.count }
        try? await Task.sleep(for: .milliseconds(reduceMotion ? 0 : 280))
        var open = false
        while typed < letters.count, !Task.isCancelled {
            let letter = letters[typed]
            typed += 1
            let pause = Self.pause(after: letter)
            if pause > 100 {
                pose = .talk(open: false)
            } else if letter.isLetter, typed.isMultiple(of: 3) {
                open.toggle()
                pose = .talk(open: open)
            }
            try? await Task.sleep(for: .milliseconds(pause))
        }
        guard !Task.isCancelled else { return }
        await wait()
    }

    /// How long it stops after a letter: the length of a breath at the end of a sentence,
    /// half of one at a comma, and otherwise only as long as a letter takes.
    nonisolated static func pause(after letter: Character) -> Int {
        switch letter {
        case ".", "!", "?": 320
        case ",", ";", ":": 150
        default: 22
        }
    }

    /// Having said it: it waits, and blinks the way anyone does, every few seconds, not
    /// to a beat, and now and then twice.
    private func wait() async {
        pose = .idle(blinking: false)
        while !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(Int.random(in: 2200...5600)))
            for _ in 0..<(Int.random(in: 0..<5) == 0 ? 2 : 1) {
                guard !Task.isCancelled else { return }
                pose = .idle(blinking: true)
                try? await Task.sleep(for: .milliseconds(130))
                guard !Task.isCancelled else { return }
                pose = .idle(blinking: false)
                try? await Task.sleep(for: .milliseconds(170))
            }
        }
    }
}

/// The face as a picture, going where the face would go: for a first run shown without
/// its 3D (`Onboarding.spent`).
private struct FlatGuyLayer: View {
    var guide: OnboardingGuide

    var body: some View {
        SwiftUI.TimelineView(.animation(paused: guide.flight == nil)) { timeline in
            if let place = guide.place(at: timeline.date), !guide.landed {
                GuyMark(pose: guide.shown)
                    .frame(width: place.rect.width, height: place.rect.height)
                    .position(x: place.rect.midX, y: place.rect.midY)
            }
        }
        .allowsHitTesting(false)
    }
}

// MARK: Screens

/// Hello: the mark in pieces to play with, and one way on.
struct WelcomeView: View {
    @Environment(\.theme) private var theme
    var start: () -> Void

    var body: some View {
        OnboardingPage {
            StageSlot(step: .welcome)
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

/// What an instance is, where it can live, and the two things to do about it — said by the
/// face, which stands in the corner over its own speech bubble.
struct HostingView: View {
    @Environment(\.openURL) private var openURL
    @Environment(OnboardingGuide.self) private var guide: OnboardingGuide?
    @Environment(\.stageRig) private var rig
    var haveOne: () -> Void

    /// How wide the face is here, and how far in from the bubble's edge it stands.
    private static let guyWidth: CGFloat = 62
    private static let guyInset: CGFloat = 20
    /// How far above the bubble's point the face stands.
    private static let guyClearance: CGFloat = 14

    var body: some View {
        // The bubble sees to its own fitting: it stays whole, and its words scroll inside it.
        OnboardingPage(wordsScroll: false) {
            ZStack {
                // The drawing is behind the models: a model pulled across an arrow covers it.
                HostingDiagram()
                StageSlot(step: .hosting)
            }
        } words: {
            // The face stands clear of the bubble: its glow reaches some way past it, and
            // right up against the bubble the two ran together.
            VStack(alignment: .leading, spacing: Self.guyClearance) {
                // Room for the face, which is not in this screen but over it.
                Color.clear
                    .frame(width: Self.guyWidth, height: Self.guyWidth * Guy.size.height / Guy.size.width)
                    .onGeometryChange(for: CGRect.self, of: { $0.frame(in: .named(Onboarding.pageSpace)) }) {
                        guide?.anchors[.hosting] = $0
                    }
                    .touchesStage(rig, in: Onboarding.pageSpace)
                    .padding(.leading, Self.guyInset)
                SpeechBubble(text: OnboardingGuide.speech, typed: guide?.typed ?? OnboardingGuide.speech.count,
                             tail: Self.guyInset + Self.guyWidth / 2)
                    // A tap on it is asking for the rest now.
                    .onTapGesture { guide?.skip() }
            }
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

/// Words in a speech bubble, as many of them as have been said so far. The bubble is the
/// size of all of them from the start, so nothing moves as they come.
///
/// Where there isn't the height for all of them (a phone on its side) the bubble is still
/// whole, and it is the words that scroll inside it, kept at the one being said.
private struct SpeechBubble: View {
    var text: String
    var typed: Int
    /// How far along the bubble's top its tail is.
    var tail: CGFloat
    @State private var position = ScrollPosition(edge: .top)
    /// How tall all the words are, and how much of that there is room to show.
    @State private var wordsHeight: CGFloat = 0
    @State private var roomHeight: CGFloat = 0

    private var said: AttributedString {
        var words = AttributedString(text)
        if typed < text.count {
            let unsaid = words.index(words.startIndex, offsetByCharacters: max(typed, 0))
            words[unsaid...].foregroundColor = .clear
        }
        return words
    }

    private var words: some View {
        Text(said)
            .font(Platform.isMac ? .body : .callout)
            .multilineTextAlignment(.leading)
            .frame(maxWidth: .infinity, alignment: .leading)
            .fixedSize(horizontal: false, vertical: true)
    }

    var body: some View {
        ViewThatFits(in: .vertical) {
            words
            ScrollView {
                words.onGeometryChange(for: CGFloat.self, of: \.size.height) { wordsHeight = $0 }
            }
            .scrollBounceBehavior(.basedOnSize)
            .scrollPosition($position)
            .onGeometryChange(for: CGFloat.self, of: \.size.height) { roomHeight = $0 }
            .onChange(of: typed) { follow() }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 13)
        .padding(.top, BubbleShape.tailHeight)
        .glassEffect(.regular, in: BubbleShape(tail: tail))
        .contentShape(BubbleShape(tail: tail))
        .accessibilityElement()
        .accessibilityLabel(text)
    }

    /// Keeps the letter being said in sight: as far down the words as the saying has got,
    /// and a little further, so that its line is clear of the bubble's foot. Once it has
    /// all been said the words are the reader's to scroll.
    private func follow() {
        guard typed < text.count, wordsHeight > roomHeight, roomHeight > 0 else { return }
        let along = min(1, CGFloat(typed) / CGFloat(max(text.count, 1)) * 1.15)
        position.scrollTo(y: along * (wordsHeight - roomHeight))
    }
}

/// A rounded box with a point on its top edge, toward whoever is speaking.
nonisolated private struct BubbleShape: Shape {
    var tail: CGFloat
    static let tailHeight: CGFloat = 9

    func path(in rect: CGRect) -> Path {
        let box = CGRect(x: rect.minX, y: rect.minY + Self.tailHeight, width: rect.width, height: rect.height - Self.tailHeight)
        var path = Path(roundedRect: box, cornerRadius: min(20, box.height / 2), style: .continuous)
        let tip = min(max(rect.minX + tail, rect.minX + 30), rect.maxX - 30)
        path.move(to: CGPoint(x: tip - 9, y: box.minY + 1))
        path.addLine(to: CGPoint(x: tip, y: rect.minY))
        path.addLine(to: CGPoint(x: tip + 9, y: box.minY + 1))
        path.closeSubpath()
        return path
    }
}

// MARK: Parts

/// The room a screen leaves for its part of the stage. Nothing 3D is in it: that is over
/// the screens (`OnboardingPages`), and is told here where its room is. A touch in the
/// room is passed on to the stage, which takes none itself.
private struct StageSlot: View {
    var step: Onboarding.Step
    @Environment(OnboardingGuide.self) private var guide: OnboardingGuide?
    @Environment(\.stageRig) private var rig
    @Environment(\.onboardingIsFlat) private var flat
    @State private var room = CGRect.zero

    var body: some View {
        Color.clear
            // Shown without its 3D, the first screen has the app's icon where the mark in
            // pieces would be. The second has its drawing, which stands in for its models.
            .overlay { if flat, step == .welcome { AppIconImage(size: min(max(min(room.width, room.height) * 0.45, 96), 220)) } }
            .onGeometryChange(for: CGRect.self, of: { $0.frame(in: .named(Onboarding.pageSpace)) }) { room = $0 }
            .onChange(of: room, initial: true) { tell() }
            .touchesStage(rig, in: Onboarding.pageSpace)
    }

    private func tell() {
        guard let guide, room.width > 0, room.height > 0 else { return }
        guide.slots[step] = room
        // The face's place by the mark is a place in the mark's room.
        if step == .welcome { guide.anchors[.welcome] = SoloLogo.guyRect(in: room) }
    }
}

/// A first-run screen: something to look at, something to read, and what to press, top to
/// bottom — or, where there is no height for that (a phone on its side), the picture
/// beside the rest.
private struct OnboardingPage<Stage: View, Words: View, Actions: View>: View {
    /// Whether words too tall for the screen are put in a scroll view here. False for
    /// words that see to that themselves.
    var wordsScroll = true
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
                        // The words are given the height they ask for first, and the stage
                        // what is left, down to the least its models can be made out in:
                        // shared evenly, a short screen cut the words off with room to spare.
                        stage
                            .frame(maxWidth: Self.widest, minHeight: Self.shortest, maxHeight: .infinity)
                        reading(spacing: 24)
                            .frame(maxWidth: 460)
                            .layoutPriority(1)
                    }
                }
            }
            .padding(.horizontal, Self.margin)
            .padding(.top, Self.top)
            .padding(.bottom, 20)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private static var margin: CGFloat { 24 }
    private static var top: CGFloat { 12 }
    /// The widest the stage's room is, however wide the screen.
    private static var widest: CGFloat { 720 }
    /// The least height the stage's room is given before the words are.
    private static var shortest: CGFloat { 180 }

    /// The words give way before the buttons do: on a small screen it is the text that scrolls.
    private func reading(spacing: CGFloat) -> some View {
        VStack(spacing: spacing) {
            if wordsScroll {
                ViewThatFits(in: .vertical) {
                    words
                    ScrollView { words }
                        .scrollBounceBehavior(.basedOnSize)
                }
            } else {
                words
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
