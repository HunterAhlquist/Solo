import Foundation

/// Keeps a request that carries the session from following a redirect off the
/// instance it was made to. Left to itself the system follows any redirect, and
/// what it does then with the request's own headers is its to decide: the
/// session is the instance's and nobody else's, so the request stops there and
/// the redirect is its answer. A request with no session is followed as ever.
nonisolated final class RedirectGuard: NSObject, URLSessionTaskDelegate {
    // The form with a completion handler: the compiler falls over on the async one in a class like this.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        let carriesSession = task.originalRequest?.value(forHTTPHeaderField: "Authorization") != nil
        completionHandler(!carriesSession || Self.staysHome(from: task.originalRequest?.url, to: request.url) ? request : nil)
    }

    /// Whether a redirect leads to the same server: the same scheme, host and port, or
    /// the same host over https, which is a proxy sending plain http on to its secure port.
    static func staysHome(from origin: URL?, to destination: URL?) -> Bool {
        guard let origin, let destination, let from = origin.host()?.lowercased(), let to = destination.host()?.lowercased(),
              from == to, let scheme = destination.scheme?.lowercased() else { return false }
        if scheme == "https", origin.scheme?.lowercased() == "http" { return true }
        return scheme == origin.scheme?.lowercased() && port(of: destination) == port(of: origin)
    }

    private static func port(of url: URL) -> Int {
        url.port ?? (url.scheme?.lowercased() == "https" ? 443 : 80)
    }
}
