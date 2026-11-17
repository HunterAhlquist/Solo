import CoreHaptics
#if canImport(UIKit)
import UIKit
#endif

/// The app's few haptics, the same three the Capacitor app plays: a tap as a
/// drag takes hold of a card, a bump as it lands, and an explosion when a
/// sketch is cleared. A device without haptics — every iPad — plays nothing.
final class Haptics {
    static let shared = Haptics()

    enum Kind { case pickUp, drop, explosion }

    private let supported = CHHapticEngine.capabilitiesForHardware().supportsHaptics
    #if os(iOS)
    private let pickUp = UIImpactFeedbackGenerator(style: .light)
    private let drop = UIImpactFeedbackGenerator(style: .medium)
    #endif
    private var engine: CHHapticEngine?

    func play(_ kind: Kind) {
        guard supported else { return }
        switch kind {
        #if os(iOS)
        case .pickUp:
            pickUp.impactOccurred()
            // The drop usually follows within seconds; ready, it plays without the engine's spin-up.
            drop.prepare()
        case .drop:
            drop.impactOccurred(intensity: 1)
        #else
        // A Mac's trackpad has a tap of its own for things that snap into place, and the
        // system plays it; the app's are the phone's.
        case .pickUp, .drop:
            break
        #endif
        case .explosion:
            explode()
        }
    }

    /// A heavy bang, a rumble that dies away over about three quarters of a second, and a few
    /// smaller aftershocks — in time with the sketch's explosion.
    private func explode() {
        do {
            let engine = try startedEngine()
            try engine.makePlayer(with: Self.explosion).start(atTime: CHHapticTimeImmediate)
        } catch {
            // The sketch clears either way.
        }
    }

    private func startedEngine() throws -> CHHapticEngine {
        if let engine { return engine }
        let engine = try CHHapticEngine()
        engine.playsHapticsOnly = true
        engine.isAutoShutdownEnabled = true
        // A reset or a stop leaves the engine unusable: make another the next time one is wanted.
        engine.resetHandler = { [weak self] in Task { @MainActor in self?.engine = nil } }
        engine.stoppedHandler = { [weak self] _ in Task { @MainActor in self?.engine = nil } }
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
        let decay = CHHapticParameterCurve(parameterID: .hapticIntensityControl, controlPoints: [
            .init(relativeTime: 0, value: 1),
            .init(relativeTime: 0.12, value: 0.75),
            .init(relativeTime: 0.35, value: 0.4),
            .init(relativeTime: 0.6, value: 0.12),
            .init(relativeTime: 0.75, value: 0),
        ], relativeTime: 0)
        let events = [
            transient(0, intensity: 1, sharpness: 0.7), rumble,
            transient(0.2, intensity: 0.65, sharpness: 0.4),
            transient(0.36, intensity: 0.45, sharpness: 0.3),
            transient(0.52, intensity: 0.25, sharpness: 0.2),
        ]
        // Fixed values, so this cannot fail.
        return try! CHHapticPattern(events: events, parameterCurves: [decay])
    }()
}
