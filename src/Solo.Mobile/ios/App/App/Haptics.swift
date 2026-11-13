import CoreHaptics
import UIKit

/// Plays the page's haptics (ISoloHaptics in the contract, `solo.haptics` in
/// the client): a tap as a drag takes hold of a card, a bump as it lands, and
/// an explosion when a sketch is cleared.
///
/// The page sends only a name, and only these names play. Every installed
/// extension's script can reach `window.soloApp`, so nothing it sends describes
/// a pattern, a duration or an intensity; what each name feels like is decided
/// here. That keeps the bridge to a few fixed feedback events rather than a
/// native API handed to code that loads at runtime (App Review 4.7.2).
///
/// A device without haptics, which is every iPad, plays nothing: UIKit's
/// feedback generators already do nothing there, and the Core Haptics engine
/// isn't created.
///
/// Android: the same three names would map to VibrationEffect compositions
/// (PRIMITIVE_TICK, PRIMITIVE_CLICK, and a THUD followed by fading
/// LOW_TICKs), behind the same `window.soloApp.haptic(name)`.
@MainActor
final class Haptics {
    static let names: Set<String> = ["pick-up", "drop", "explosion"]

    private let supported = CHHapticEngine.capabilitiesForHardware().supportsHaptics
    private let pickUp = UIImpactFeedbackGenerator(style: .light)
    private let drop = UIImpactFeedbackGenerator(style: .medium)
    private var engine: CHHapticEngine?

    /// Readies the generators for a haptic that may follow (a press that may
    /// become a drag), so the first one plays without the Taptic Engine's
    /// spin-up delay. They stay ready for a few seconds.
    func prepare() {
        guard supported else { return }
        pickUp.prepare()
        drop.prepare()
    }

    func play(_ name: String) {
        guard supported, Self.names.contains(name) else { return }
        switch name {
        case "pick-up":
            pickUp.impactOccurred()
            // The drop usually follows within seconds.
            drop.prepare()
        case "drop":
            drop.impactOccurred(intensity: 1)
        case "explosion":
            explode()
        default:
            break
        }
    }

    /// A heavy bang, a rumble that dies away over about 0.7 s, and a few
    /// smaller aftershocks, in time with the sketch's explosion, which erases
    /// at 0.6 s and is gone by 1.2 s.
    private func explode() {
        do {
            let engine = try startedEngine()
            let player = try engine.makePlayer(with: Self.explosion)
            try player.start(atTime: CHHapticTimeImmediate)
        } catch {
            // Not worth more than a log line: the sketch clears either way.
            NSLog("Solo: explosion haptic didn't play: \(error)")
        }
    }

    private func startedEngine() throws -> CHHapticEngine {
        if let engine { return engine }
        let engine = try CHHapticEngine()
        engine.playsHapticsOnly = true
        engine.isAutoShutdownEnabled = true
        // A reset (a media services restart) stops the engine; start again on
        // the next explosion rather than right away.
        engine.resetHandler = { [weak self] in
            Task { @MainActor in self?.engine = nil }
        }
        engine.stoppedHandler = { [weak self] _ in
            Task { @MainActor in self?.engine = nil }
        }
        try engine.start()
        self.engine = engine
        return engine
    }

    private static let explosion: CHHapticPattern = {
        func transient(_ time: TimeInterval, intensity: Float, sharpness: Float) -> CHHapticEvent {
            CHHapticEvent(eventType: .hapticTransient, parameters: [
                CHHapticEventParameter(parameterID: .hapticIntensity, value: intensity),
                CHHapticEventParameter(parameterID: .hapticSharpness, value: sharpness),
            ], relativeTime: time)
        }
        let rumble = CHHapticEvent(eventType: .hapticContinuous, parameters: [
            CHHapticEventParameter(parameterID: .hapticIntensity, value: 1),
            CHHapticEventParameter(parameterID: .hapticSharpness, value: 0.15),
        ], relativeTime: 0, duration: 0.75)
        // The rumble's intensity falls off steeply, then trails away.
        let decay = CHHapticParameterCurve(parameterID: .hapticIntensityControl, controlPoints: [
            .init(relativeTime: 0, value: 1),
            .init(relativeTime: 0.12, value: 0.75),
            .init(relativeTime: 0.35, value: 0.4),
            .init(relativeTime: 0.6, value: 0.12),
            .init(relativeTime: 0.75, value: 0),
        ], relativeTime: 0)
        let events = [
            transient(0, intensity: 1, sharpness: 0.7),
            rumble,
            transient(0.2, intensity: 0.65, sharpness: 0.4),
            transient(0.36, intensity: 0.45, sharpness: 0.3),
            transient(0.52, intensity: 0.25, sharpness: 0.2),
        ]
        // Fixed values, so this can't fail at run time.
        return try! CHHapticPattern(events: events, parameterCurves: [decay])
    }()
}
