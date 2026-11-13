import SwiftUI
import WidgetKit

struct WorkEntry: TimelineEntry {
    enum State {
        case work(WorkSummary)
        case noInstance
        case signedOut
        case unreachable
    }

    let date: Date
    let instance: URL?
    let state: State
}

/// Fetches the chosen instance's summary for all three widgets. Day counts and
/// near-deadline fills change at midnight, so the timeline carries an entry
/// for each of the next two midnights; past those, the next refresh, about
/// half an hour out (as iOS allows), or the app reloading it when it's used.
struct WorkProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> WorkEntry {
        WorkEntry(date: .now, instance: nil, state: .work(.sample))
    }

    func snapshot(for configuration: SelectInstanceIntent, in context: Context) async -> WorkEntry {
        context.isPreview ? placeholder(in: context) : await entry(for: configuration)
    }

    func timeline(for configuration: SelectInstanceIntent, in context: Context) async -> Timeline<WorkEntry> {
        let now = Date()
        let first = await entry(for: configuration, at: now)
        let calendar = Calendar.current
        var entries = [first]
        var midnight = calendar.startOfDay(for: now)
        for _ in 0..<2 {
            midnight = calendar.date(byAdding: .day, value: 1, to: midnight)!
            entries.append(WorkEntry(date: midnight, instance: first.instance, state: first.state))
        }
        return Timeline(entries: entries, policy: .after(now.addingTimeInterval(30 * 60)))
    }

    private func entry(for configuration: SelectInstanceIntent, at date: Date = .now, again: Bool = true) async -> WorkEntry {
        let instances = SharedStore.instances
        let chosen = configuration.instance.flatMap { entity in instances.first { $0.url.absoluteString == entity.id } }
            ?? instances.first { $0.url == SharedStore.current } ?? instances.first
        guard let instance = chosen?.url else {
            return WorkEntry(date: date, instance: nil, state: .noInstance)
        }
        guard let token = SharedStore.token(for: instance) else {
            return WorkEntry(date: date, instance: instance, state: .signedOut)
        }
        let result: Result<WorkSummary, Error>
        do {
            result = .success(try await WorkSummaryClient.fetch(instance, token: token))
        } catch {
            result = .failure(error)
        }
        // The app may have signed out, or in as someone else, while this was
        // in flight. The answer belongs to the session that asked, so it must
        // neither delete a newer session (a late 401) nor cache work again
        // after a sign-out (a late success): start over with what's there now.
        guard SharedStore.token(for: instance) == token else {
            return again
                ? await entry(for: configuration, at: date, again: false)
                : WorkEntry(date: date, instance: instance, state: .unreachable)
        }
        switch result {
        case .success(let summary):
            SharedStore.cache(summary, for: instance)
            return WorkEntry(date: date, instance: instance, state: .work(summary))
        case .failure(WorkSummaryError.signedOut):
            SharedStore.setToken(nil, for: instance)
            return WorkEntry(date: date, instance: instance, state: .signedOut)
        case .failure:
            // Offline or the instance down: the last good summary, if any.
            if let cached = SharedStore.cached(for: instance) {
                return WorkEntry(date: date, instance: instance, state: .work(cached.summary))
            }
            return WorkEntry(date: date, instance: instance, state: .unreachable)
        }
    }
}

extension WorkSummary {
    /// For the widget gallery and placeholders.
    static let sample = WorkSummary(unstarted: 3, inProgress: 4, finished: 2, open: [
        .init(id: UUID(), serial: "DMD-EA1-23", name: "Boss fight intro cutscene", status: "InProgress",
              deadline: dateString(daysFromNow: -1), path: ""),
        .init(id: UUID(), serial: "DMD-EA1-31", name: "Save menu polish", status: "Unstarted",
              deadline: dateString(daysFromNow: 1), path: ""),
        .init(id: UUID(), serial: "DMD-EA1-18", name: "Level 3 lighting pass", status: "InProgress",
              deadline: dateString(daysFromNow: 4), path: ""),
        .init(id: UUID(), serial: "GAME-SP-12", name: "Ship the demo build", status: "InProgress",
              deadline: dateString(daysFromNow: 6), path: ""),
        .init(id: UUID(), serial: "GAME-SP-14", name: "Trailer capture", status: "Unstarted",
              deadline: dateString(daysFromNow: 7), path: ""),
        .init(id: UUID(), serial: "GAME-SP-15", name: "Steam page copy", status: "Unstarted",
              deadline: dateString(daysFromNow: 12), path: ""),
        .init(id: UUID(), serial: "GAME-SP-16", name: "Audio mix", status: "InProgress", deadline: nil, path: ""),
    ])

    static func dateString(daysFromNow days: Int) -> String {
        let date = Calendar.current.date(byAdding: .day, value: days, to: .now)!
        let parts = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year!, parts.month!, parts.day!)
    }
}
