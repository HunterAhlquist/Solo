// Expires all but the newest builds of the preview app on TestFlight:
// `swift build/asc-expire-builds.swift <key.p8> <IOS|MAC_OS> [keep]`, with
// ASC_KEY_ID and ASC_ISSUER_ID set. `keep` is how many stay, 3 unless said.
// `--check` in place of the arguments tries the choosing against made-up
// builds and touches nothing.
//
// Why: every preview tag that changes the app uploads a build, and each one
// stays installable for ninety days. Testers are offered the whole list, and
// only the newest few are ever the ones meant. The TestFlight jobs run this
// after an upload, so the list is trimmed as it grows.
//
// The preview app and no other. Its bundle id is written here, not taken as an
// argument: expiring a build can't be undone, and the store app's builds are
// the ones that may be sent on to a review. A build of another platform is
// left alone too — the iPhone and iPad's and the Mac's are counted apart, each
// by the job that uploads it.
//
// The build a run has just uploaded is usually still being taken in, and may
// not be listed yet. Then the newest listed stay and there is one more than
// `keep` until the next run; never one fewer.
//
// Never fails the job: the upload it follows has succeeded, and a list left
// long is not a reason to call the release broken. What it couldn't do is a
// warning. Swift for the reason asc-revoke-run-certificates.swift is.

import CryptoKit
import Foundation

let previewBundleID = "com.hunterahlquist.solo.preview"

func warn(_ message: String) {
    FileHandle.standardError.write(Data("::warning title=TestFlight builds::\(message)\n".utf8))
}

func base64URL(_ data: Data) -> String {
    data.base64EncodedString()
        .replacingOccurrences(of: "+", with: "-")
        .replacingOccurrences(of: "/", with: "_")
        .replacingOccurrences(of: "=", with: "")
}

/// A 10-minute App Store Connect API token.
func token(keyPEM: String, keyID: String, issuer: String) throws -> String {
    let key = try P256.Signing.PrivateKey(pemRepresentation: keyPEM)
    let now = Int(Date().timeIntervalSince1970)
    let header = try JSONSerialization.data(withJSONObject: ["alg": "ES256", "kid": keyID, "typ": "JWT"])
    let claims = try JSONSerialization.data(withJSONObject: [
        "iss": issuer, "iat": now, "exp": now + 600, "aud": "appstoreconnect-v1",
    ])
    let input = base64URL(header) + "." + base64URL(claims)
    // rawRepresentation is r ‖ s, the form a JWT wants (not DER).
    let signature = try key.signature(for: Data(input.utf8)).rawRepresentation
    return input + "." + base64URL(signature)
}

struct Build: Equatable {
    var id: String
    /// The build number, which for this app is the minute it was built.
    var number: String
    var uploaded: String
}

/// The builds in an answer to `GET /v1/builds`, in the order it gave them.
func builds(in data: Data) -> [Build]? {
    guard let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let rows = body["data"] as? [[String: Any]] else { return nil }
    return rows.compactMap { row in
        guard let id = row["id"] as? String, let attributes = row["attributes"] as? [String: Any] else { return nil }
        return Build(id: id, number: attributes["version"] as? String ?? "?", uploaded: attributes["uploadedDate"] as? String ?? "")
    }
}

/// Which of them go: all but the `keep` uploaded last. The dates are ISO 8601 in one zone,
/// so they sort as text; a build with no date sorts as the oldest and is not kept for it.
func expiring(_ builds: [Build], keep: Int) -> [Build] {
    Array(builds.sorted { $0.uploaded > $1.uploaded }.dropFirst(max(keep, 1)))
}

func send(_ method: String, _ url: URL, token: String, body: Data? = nil) async throws -> (Data, Int) {
    var request = URLRequest(url: url)
    request.httpMethod = method
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    if let body {
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    }
    let (data, response) = try await URLSession.shared.data(for: request)
    return (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
}

func check() -> Bool {
    let five = (1...5).map { Build(id: "b\($0)", number: "\($0)", uploaded: "2026-11-0\($0)T10:00:00-08:00") }
    let cases: [(String, Bool)] = [
        ("the oldest go, newest kept", expiring(five.shuffled(), keep: 3).map(\.id) == ["b2", "b1"]),
        ("nothing goes from a short list", expiring(Array(five.prefix(3)), keep: 3).isEmpty),
        ("one always stays", expiring(five, keep: 0).count == 4),
        ("an undated build is not kept ahead of a dated one",
         expiring(five + [Build(id: "x", number: "?", uploaded: "")], keep: 5).map(\.id) == ["x"]),
        ("an answer is read", builds(in: Data(#"{"data":[{"id":"a","attributes":{"version":"7","uploadedDate":"d"}}]}"#.utf8))
            == [Build(id: "a", number: "7", uploaded: "d")]),
    ]
    for (name, passed) in cases { print("\(passed ? "ok  " : "FAIL") \(name)") }
    return cases.allSatisfy(\.1)
}

func main() async {
    let arguments = Array(CommandLine.arguments.dropFirst())
    if arguments == ["--check"] { exit(check() ? 0 : 1) }

    let environment = ProcessInfo.processInfo.environment
    guard (2...3).contains(arguments.count), ["IOS", "MAC_OS"].contains(arguments[1]),
          let keyID = environment["ASC_KEY_ID"], !keyID.isEmpty,
          let issuer = environment["ASC_ISSUER_ID"], !issuer.isEmpty
    else {
        warn("Usage: ASC_KEY_ID=… ASC_ISSUER_ID=… swift \(CommandLine.arguments[0]) <key.p8> <IOS|MAC_OS> [keep]")
        return
    }
    let platform = arguments[1]
    guard let keep = arguments.count == 3 ? Int(arguments[2]) : 3, keep >= 1 else {
        warn("How many builds to keep has to be a number, one or more.")
        return
    }

    do {
        let keyPEM = try String(contentsOfFile: arguments[0], encoding: .utf8)
        let jwt = try token(keyPEM: keyPEM, keyID: keyID, issuer: issuer)
        let api = "https://api.appstoreconnect.apple.com/v1"

        var apps = URLComponents(string: "\(api)/apps")!
        apps.queryItems = [URLQueryItem(name: "filter[bundleId]", value: previewBundleID),
                           URLQueryItem(name: "fields[apps]", value: "bundleId")]
        let (appData, appStatus) = try await send("GET", apps.url!, token: jwt)
        // The filter matches what begins with the id as well: only the app that is exactly it.
        guard appStatus == 200,
              let body = try? JSONSerialization.jsonObject(with: appData) as? [String: Any],
              let rows = body["data"] as? [[String: Any]],
              let app = rows.first(where: { ($0["attributes"] as? [String: Any])?["bundleId"] as? String == previewBundleID }),
              let appID = app["id"] as? String
        else {
            warn("Couldn't find the preview app in App Store Connect (HTTP \(appStatus)); no builds were expired.")
            return
        }

        var list = URLComponents(string: "\(api)/builds")!
        list.queryItems = [
            URLQueryItem(name: "filter[app]", value: appID),
            URLQueryItem(name: "filter[expired]", value: "false"),
            URLQueryItem(name: "filter[preReleaseVersion.platform]", value: platform),
            URLQueryItem(name: "fields[builds]", value: "version,uploadedDate,expired"),
            URLQueryItem(name: "sort", value: "-uploadedDate"),
            URLQueryItem(name: "limit", value: "200"),
        ]
        let (listData, listStatus) = try await send("GET", list.url!, token: jwt)
        guard listStatus == 200, let listed = builds(in: listData) else {
            warn("Couldn't list the preview app's builds (HTTP \(listStatus)); none were expired.")
            return
        }

        let going = expiring(listed, keep: keep)
        guard !going.isEmpty else {
            print("\(listed.count) \(platform) build(s) of the preview app on TestFlight; \(keep) may stay, so none expire.")
            return
        }
        var expired = 0
        for build in going {
            let patch = try JSONSerialization.data(withJSONObject: [
                "data": ["type": "builds", "id": build.id, "attributes": ["expired": true]],
            ])
            let (_, status) = try await send("PATCH", URL(string: "\(api)/builds/\(build.id)")!, token: jwt, body: patch)
            if status == 200 {
                expired += 1
                print("Expired build \(build.number), uploaded \(build.uploaded).")
            } else {
                // A key that may upload but not manage builds is turned away here (403).
                warn("Couldn't expire build \(build.number) (HTTP \(status)).")
            }
        }
        print("Expired \(expired) of \(going.count) older \(platform) build(s) of the preview app; the newest \(keep) listed stay.")
    } catch {
        warn("Couldn't trim the preview app's builds: \(error.localizedDescription)")
    }
}

await main()
