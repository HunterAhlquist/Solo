import SwiftUI
import WidgetKit

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
        AppIntentConfiguration(kind: "solo.assigned", intent: SelectInstanceIntent.self, provider: WorkProvider()) {
            AssignedCountView(entry: $0)
        }
        .configurationDisplayName("Assigned")
        .description("How many of your tasks are new or in progress.")
        .supportedFamilies([.systemSmall])
    }
}

struct MiniBoardWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "solo.board", intent: SelectInstanceIntent.self, provider: WorkProvider()) {
            MiniBoardView(entry: $0)
        }
        .configurationDisplayName("Assigned Work")
        .description("Your work as a board: new, in progress and done.")
        .supportedFamilies([.systemMedium])
    }
}

struct UpcomingWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "solo.upcoming", intent: SelectInstanceIntent.self, provider: WorkProvider()) {
            UpcomingView(entry: $0)
        }
        .configurationDisplayName("Upcoming Due")
        .description("Your tasks due in the next 7 days.")
        .supportedFamilies([.systemLarge])
    }
}
