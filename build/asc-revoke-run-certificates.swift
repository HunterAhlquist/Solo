// Revokes the Apple Development certificate a TestFlight run created, and no
// other: `swift build/asc-revoke-run-certificates.swift <key.p8>`, with
// ASC_KEY_ID and ASC_ISSUER_ID set, as the last step of release-ios.yml.
//
// Why: `xcodebuild archive` with automatic signing signs with a development
// certificate whose private key has to be in the keychain. A fresh runner's
// keychain is empty, so with -allowProvisioningUpdates every run makes a new
// "Apple Development: Created via API" certificate, whose key is gone with the
// runner. They pile up until Apple refuses another ("Your account has reached
// the maximum number of certificates"), which stopped preview-v1.21.0-mobile.15.
// Archiving unsigned would avoid making one, but an unsigned archive carries
// no entitlements, so the app's and the widgets' App Group would depend on
// the export step adding it back. The run keeps signing as it does, which is
// known to carry them, and cleans up after itself instead.
//
// Only certificates in this runner's own keychain are matched, by serial
// number, so a certificate on someone's Mac or from another run is never
// touched. Nothing here fails the job: a successful upload stays successful,
// and a revoke that didn't happen shows as a warning.
//
// Swift, not a shell script, because every Xcode runner has it, and CryptoKit
// signs the App Store Connect token (ES256) with no other tools.

import CryptoKit
import Foundation
import Security

func warn(_ message: String) {
    print("::warning title=Development certificate::\(message)")
}

/// Serial numbers, as App Store Connect writes them (upper-case hex, no
/// leading zeros), of the API-made development certificates in this keychain.
func runCertificateSerials() -> [String] {
    let query: [String: Any] = [
        kSecClass as String: kSecClassCertificate,
        kSecMatchLimit as String: kSecMatchLimitAll,
        kSecReturnRef as String: true,
    ]
    var result: AnyObject?
    guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
          let certificates = result as? [SecCertificate]
    else { return [] }
    return certificates.compactMap { certificate in
        guard let summary = SecCertificateCopySubjectSummary(certificate) as String?,
              summary.hasPrefix("Apple Development: Created via API"),
              let serial = SecCertificateCopySerialNumberData(certificate, nil) as Data?
        else { return nil }
        return normalized(serial.map { String(format: "%02X", $0) }.joined())
    }
}

func normalized(_ serial: String) -> String {
    let trimmed = serial.uppercased().drop { $0 == "0" }
    return trimmed.isEmpty ? "0" : String(trimmed)
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

func request(_ method: String, _ url: URL, token: String) async throws -> (Data, Int) {
    var request = URLRequest(url: url)
    request.httpMethod = method
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    let (data, response) = try await URLSession.shared.data(for: request)
    return (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
}

func main() async {
    let environment = ProcessInfo.processInfo.environment
    guard CommandLine.arguments.count == 2,
          let keyID = environment["ASC_KEY_ID"], !keyID.isEmpty,
          let issuer = environment["ASC_ISSUER_ID"], !issuer.isEmpty
    else {
        warn("Usage: ASC_KEY_ID=… ASC_ISSUER_ID=… swift \(CommandLine.arguments[0]) <key.p8>")
        return
    }

    let serials = runCertificateSerials()
    guard !serials.isEmpty else {
        print("This run made no development certificate; nothing to revoke.")
        return
    }

    do {
        let keyPEM = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
        let jwt = try token(keyPEM: keyPEM, keyID: keyID, issuer: issuer)

        var components = URLComponents(string: "https://api.appstoreconnect.apple.com/v1/certificates")!
        components.queryItems = [
            URLQueryItem(name: "filter[serialNumber]", value: serials.joined(separator: ",")),
            URLQueryItem(name: "fields[certificates]", value: "serialNumber,name,certificateType"),
            URLQueryItem(name: "limit", value: "200"),
        ]
        let (data, status) = try await request("GET", components.url!, token: jwt)
        guard status == 200,
              let body = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let found = body["data"] as? [[String: Any]]
        else {
            warn("Couldn't list certificates (HTTP \(status)); revoke this run's \"Created via API\" certificate in the developer portal.")
            return
        }

        for certificate in found {
            guard let id = certificate["id"] as? String,
                  let attributes = certificate["attributes"] as? [String: Any],
                  let serial = attributes["serialNumber"] as? String,
                  serials.contains(normalized(serial))
            else { continue }
            let url = URL(string: "https://api.appstoreconnect.apple.com/v1/certificates/\(id)")!
            let (_, status) = try await request("DELETE", url, token: jwt)
            if status == 204 {
                print("Revoked \(attributes["name"] as? String ?? "certificate") \(serial), made by this run.")
            } else {
                warn("Couldn't revoke certificate \(serial) (HTTP \(status)).")
            }
        }
    } catch {
        warn("Couldn't revoke this run's certificate: \(error.localizedDescription)")
    }
}

await main()
