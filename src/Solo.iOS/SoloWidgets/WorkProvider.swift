import SwiftUI
import WidgetKit

struct WorkEntry: TimelineEntry {
    enum State {
        case work(WorkSummary)
        /// The app has no instance, or nobody is signed in to it.
        case signedOut
        case unreachable
    }

    let date: Date
    let instance: WidgetInstance?
    let state: State
}

/// Fetches the instance's summary for all three widgets. Day counts and the
/// near-deadline marks change at midnight, so the timeline carries an entry
/// for each of the next two; past those, the next refresh — about half an hour
/// out, as iOS allows — or the app reloading it when something changes there.
struct WorkProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> WorkEntry {
        WorkEntry(date: .now, instance: nil, state: .work(.sample))
    }

    func snapshot(for configuration: SoloWidgetIntent, in context: Context) async -> WorkEntry {
        context.isPreview ? placeholder(in: context) : await entry(at: .now)
    }

    func timeline(for configuration: SoloWidgetIntent, in context: Context) async -> Timeline<WorkEntry> {
        let now = Date()
        let first = await entry(at: now)
        var entries = [first]
        var midnight = Calendar.current.startOfDay(for: now)
        for _ in 0..<2 {
            guard let next = Calendar.current.date(byAdding: .day, value: 1, to: midnight) else { break }
            midnight = next
            entries.append(WorkEntry(date: midnight, instance: first.instance, state: first.state))
        }
        return Timeline(entries: entries, policy: .after(now.addingTimeInterval(30 * 60)))
    }

    private func entry(at date: Date) async -> WorkEntry {
        guard let instance = WidgetStore.instance, let token = WidgetStore.token else {
            return WorkEntry(date: date, instance: WidgetStore.instance, state: .signedOut)
        }
        do {
            let summary = try await WorkSummaryClient.fetch(instance.url, token: token)
            // The app may have signed out while this was in flight: an answer belongs to the
            // session that asked, and is neither shown nor kept for another.
            guard WidgetStore.token == token else { return await entry(at: date) }
            WidgetStore.summary = summary
            return WorkEntry(date: date, instance: instance, state: .work(summary))
        } catch WorkSummaryError.signedOut {
            // The instance stopped accepting it. The app finds that out for itself; the
            // widget only stops asking with it.
            if WidgetStore.token == token { WidgetStore.token = nil }
            return WorkEntry(date: date, instance: instance, state: .signedOut)
        } catch {
            // Offline, or the instance down: the last good summary, if there is one.
            return WorkEntry(date: date, instance: instance, state: WidgetStore.summary.map { .work($0) } ?? .unreachable)
        }
    }
}

extension WorkSummary {
    /// For the widget gallery and placeholders.
    static let sample: WorkSummary = {
        func day(_ offset: Int) -> String {
            let date = Calendar.current.date(byAdding: .day, value: offset, to: .now) ?? .now
            let parts = Calendar.current.dateComponents([.year, .month, .day], from: date)
            return String(format: "%04d-%02d-%02d", parts.year ?? 2026, parts.month ?? 1, parts.day ?? 1)
        }
        let tasks: [(String, String, Bool, Int?)] = [
            ("DMD-EA1-23", "Boss fight intro cutscene", true, -1), ("DMD-EA1-27", "Controller remapping", false, 0),
            ("DMD-EA1-31", "Save menu polish", false, 1), ("DMD-EA1-33", "Pause menu art", true, 2),
            ("DMD-EA1-35", "Credits roll", false, 3), ("DMD-EA1-18", "Level 3 lighting pass", true, 4),
            ("GAME-SP-11", "Wishlist button", true, 5), ("GAME-SP-12", "Ship the demo build", true, 6),
            ("GAME-SP-14", "Trailer capture", false, 7), ("GAME-SP-15", "Steam page copy", false, 12),
            ("GAME-SP-16", "Audio mix", true, nil),
        ]
        return WorkSummary(unstarted: 5, inProgress: 6, finished: 2, open: tasks.map { serial, name, started, due in
            Item(id: UUID(), serial: serial, name: name, started: started, deadline: due.map(day))
        })
    }()
}
