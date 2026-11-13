import Foundation

/// How a widget opens the app at a page: `solo-widget://open?instance=…&path=…`.
/// The app opens it only for an instance in its list, and only a path inside
/// the instance's own app (`SoloViewController.open(widgetLink:)`).
enum WidgetLink {
    static let scheme = "solo-widget"

    static func url(instance: URL, path: String) -> URL {
        var components = URLComponents()
        components.scheme = scheme
        components.host = "open"
        components.queryItems = [
            URLQueryItem(name: "instance", value: instance.absoluteString),
            URLQueryItem(name: "path", value: path),
        ]
        return components.url!
    }

    /// The instance and a safe relative path, or nil for anything else.
    static func parse(_ url: URL) -> (instance: URL, path: String)? {
        guard url.scheme == scheme, url.host == "open",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let instance = items.first(where: { $0.name == "instance" })?.value.flatMap(URL.init(string:)),
              let path = items.first(where: { $0.name == "path" })?.value
        else { return nil }
        // An app route: relative, no scheme, no host, no climbing out.
        guard !path.contains("://"), !path.hasPrefix("/"), !path.hasPrefix("\\"), !path.contains("..") else { return nil }
        return (instance, path)
    }
}
