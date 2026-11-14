import AppIntents
import SwiftUI
import WidgetKit

// Three widgets, one of each size, showing the same thing the Capacitor app's
// do: how much is assigned to you, the same as a small board, and what is due
// this week. They are drawn the way the rest of this app is — the system's
// type and materials, with the instance's accent as the only colour of its own.

@main
struct SoloWidgets: WidgetBundle {
    var body: some Widget {
        AssignedCountWidget()
        MiniBoardWidget()
        UpcomingWidget()
    }
}

struct AssignedCountWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "solo.assigned", intent: SoloWidgetIntent.self, provider: WorkProvider()) {
            AssignedCountView(entry: $0)
        }
        .configurationDisplayName("Assigned")
        .description("How many of your tasks are new or in progress.")
        .supportedFamilies([.systemSmall])
    }
}

struct MiniBoardWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "solo.board", intent: SoloWidgetIntent.self, provider: WorkProvider()) {
            MiniBoardView(entry: $0)
        }
        .configurationDisplayName("Assigned Work")
        .description("Your work as a board: new, in progress and done.")
        .supportedFamilies([.systemMedium])
    }
}

struct UpcomingWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "solo.upcoming", intent: SoloWidgetIntent.self, provider: WorkProvider()) {
            UpcomingView(entry: $0)
        }
        .configurationDisplayName("Upcoming Due")
        .description("Your tasks due in the next 7 days.")
        .supportedFamilies([.systemLarge])
    }
}

/// Nothing to choose: the app has one instance at a time, and the widgets show it. The
/// configuration exists so the provider can be asynchronous.
struct SoloWidgetIntent: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "Solo"
    static let description = IntentDescription("Shows the instance Solo is signed in to.")
}

/// The refresh button: asks the instance again now.
struct RefreshIntent: AppIntent {
    static let title: LocalizedStringResource = "Refresh"
    static let isDiscoverable = false

    func perform() async throws -> some IntentResult {
        WidgetCenter.shared.reloadAllTimelines()
        return .result()
    }
}
