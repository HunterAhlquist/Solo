// Prints the bundle id of an App Store Connect app, given its Apple ID:
// `swift build/asc-app-bundle-id.swift <key.p8> <app id>`, with ASC_KEY_ID and
// ASC_ISSUER_ID set. The TestFlight jobs build a version tag under whatever
// this prints.
//
// Why: a version tag's build goes to the app named by SOLO_ASC_APP_ID, and that
// variable is the only place the app is named. An upload is filed by bundle id
// — `xcodebuild -exportArchive` is given nothing else, and altool takes the
// Apple ID beside it and refuses a pair that disagrees — so the build has to be
// made under the bundle id that app has. Asked of App Store Connect here, it
// can't drift from the variable: point the variable at another app and the
// next tag is built for that one. Written into the workflow as well, the two
// could disagree, and the build would go to whichever app the bundle id named.
//
// Prints the bundle id alone on stdout, for `$(…)`. Anything else — a bad key,
// an id the key can't see — is an error annotation on stderr and exit 1: a
// build under a guessed bundle id is an upload to the wrong app.
//
// Swift, not a shell script, for the reason asc-revoke-run-certificates.swift
// is: every Xcode runner has it, and CryptoKit signs the token with no other
// tools.

import CryptoKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("::error title=App Store Connect app::\(message)\n".utf8))
    exit(1)
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

/// The bundle id in the answer to `GET /v1/apps/{id}`, if it is one.
func bundleID(in data: Data) -> String? {
    guard let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let app = body["data"] as? [String: Any],
          let attributes = app["attributes"] as? [String: Any],
          let bundle = attributes["bundleId"] as? String, !bundle.isEmpty
    else { return nil }
    return bundle
}

let environment = ProcessInfo.processInfo.environment
guard CommandLine.arguments.count == 3,
      let keyID = environment["ASC_KEY_ID"], !keyID.isEmpty,
      let issuer = environment["ASC_ISSUER_ID"], !issuer.isEmpty
else {
    fail("Usage: ASC_KEY_ID=… ASC_ISSUER_ID=… swift \(CommandLine.arguments[0]) <key.p8> <app id>")
}

// An Apple ID is digits. Anything else would be put into the address asked of.
let appID = CommandLine.arguments[2].trimmingCharacters(in: .whitespacesAndNewlines)
guard !appID.isEmpty, appID.allSatisfy(\.isASCII), appID.allSatisfy(\.isNumber) else {
    fail("The app id isn't a number. It is the app's Apple ID, under App Information in App Store Connect.")
}

do {
    let keyPEM = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
    let jwt = try token(keyPEM: keyPEM, keyID: keyID, issuer: issuer)

    var components = URLComponents(string: "https://api.appstoreconnect.apple.com/v1/apps/\(appID)")!
    components.queryItems = [URLQueryItem(name: "fields[apps]", value: "bundleId,name")]
    var request = URLRequest(url: components.url!)
    request.setValue("Bearer \(jwt)", forHTTPHeaderField: "Authorization")
    let (data, response) = try await URLSession.shared.data(for: request)
    let status = (response as? HTTPURLResponse)?.statusCode ?? 0

    guard status == 200 else {
        fail(status == 404
            ? "App Store Connect has no app \(appID) that this key can see. Check the variable against the app's Apple ID."
            : "Couldn't read app \(appID) from App Store Connect (HTTP \(status)).")
    }
    guard let bundle = bundleID(in: data) else {
        fail("App Store Connect's answer for app \(appID) had no bundle id in it.")
    }
    print(bundle)
} catch {
    fail("Couldn't read app \(appID) from App Store Connect: \(error.localizedDescription)")
}
