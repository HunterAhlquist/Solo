import AppIntents
import WidgetKit

/// An instance in the app's list, for choosing which one a widget shows.
struct InstanceEntity: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "Instance"
    static let defaultQuery = InstanceQuery()

    let id: String
    let name: String

    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(name)") }

    init(_ instance: SharedStore.Instance) {
        id = instance.url.absoluteString
        name = instance.name
    }
}

struct InstanceQuery: EntityQuery {
    func entities(for identifiers: [String]) async throws -> [InstanceEntity] {
        SharedStore.instances.filter { identifiers.contains($0.url.absoluteString) }.map(InstanceEntity.init)
    }

    func suggestedEntities() async throws -> [InstanceEntity] {
        SharedStore.instances.map(InstanceEntity.init)
    }

    /// The instance the app has open.
    func defaultResult() async -> InstanceEntity? {
        let instances = SharedStore.instances
        return (instances.first { $0.url == SharedStore.current } ?? instances.first).map(InstanceEntity.init)
    }
}

struct SelectInstanceIntent: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "Instance"
    static let description = IntentDescription("Which Solo instance the widget shows.")

    @Parameter(title: "Instance")
    var instance: InstanceEntity?
}

/// The smile's action: fetch everyone's work again now.
struct RefreshIntent: AppIntent {
    static let title: LocalizedStringResource = "Refresh"
    static let isDiscoverable = false

    func perform() async throws -> some IntentResult {
        WidgetCenter.shared.reloadAllTimelines()
        return .result()
    }
}
