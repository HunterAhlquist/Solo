#!/usr/bin/env bash
# Compiles ios/App/App/InstancePorts.swift with the checks below and runs them
# on the Mac. Port choice decides which origin, and so which sign-in and which
# offline edits, an instance opens on: worth checking outside the app.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cat > "$work/main.swift" <<'SWIFT'
import Foundation

var failures = 0
func check(_ name: String, _ ok: Bool) {
    print(ok ? "ok   \(name)" : "FAIL \(name)")
    if !ok { failures += 1 }
}

let suite = "solo.check-instance-ports.\(ProcessInfo.processInfo.processIdentifier)"
let defaults = UserDefaults(suiteName: suite)!
defer { defaults.removePersistentDomain(forName: suite) }

let a = URL(string: "https://a.example.com")!
let b = URL(string: "https://b.example.com")!
struct Busy: Error {}

/// One launch: `busy` ports fail to start. Returns the port it started on.
func launch(_ instance: URL, busy: Set<UInt16> = [], tried: inout [UInt16]) -> UInt16? {
    InstancePorts.start(for: instance, defaults: defaults) { port -> UInt16 in
        tried.append(port)
        if busy.contains(port) { throw Busy() }
        return port
    }
}
func launch(_ instance: URL, busy: Set<UInt16> = []) -> UInt16? {
    var tried: [UInt16] = []
    return launch(instance, busy: busy, tried: &tried)
}

check("the first instance gets 41321", launch(a) == 41321)
check("and keeps it", launch(a) == 41321)

check("a held main port falls back to a new port", launch(a, busy: [41321]) == 41322)
check("the next launch goes back to the main port", launch(a) == 41321)
check("the fallback is the instance's again when the main port is held", launch(a, busy: [41321]) == 41322)

check("another instance never gets a port the first was given", launch(b) == 41323)
check("not even when all of its own are held", launch(b, busy: [41323]) == 41324)
check("the first instance's ports are unchanged", InstancePorts.ports(for: a, defaults: defaults) == [41321, 41322])

// Ten saved ports, every one held: the launch still reaches a free port.
for _ in 0..<5 { _ = launch(a, busy: Set(InstancePorts.ports(for: a, defaults: defaults)).union([41325, 41326, 41327, 41328, 41329, 41330, 41331, 41332])) }
let saved = InstancePorts.ports(for: a, defaults: defaults)
check("ten saved ports (\(saved.count))", saved.count >= 10)
var tried: [UInt16] = []
let started = launch(a, busy: Set(saved), tried: &tried)
check("all ten held, the next new port starts", started != nil && !saved.contains(started!))
check("every saved port was tried first", Array(tried.prefix(saved.count)) == saved)

// Nothing can listen: each launch adds a bounded number of ports, and the
// next launch still tries new ones.
let before = InstancePorts.ports(for: b, defaults: defaults).count
tried = []
check("a launch where nothing starts gives nil", launch(b, busy: Set(UInt16(41321)...UInt16(42000)), tried: &tried) == nil)
let after = InstancePorts.ports(for: b, defaults: defaults).count
check("and adds only \(InstancePorts.newPortsPerLaunch) ports", after - before == InstancePorts.newPortsPerLaunch)
check("a later launch reaches a port once one is free", launch(b) != nil)

let all = InstancePorts.ports(for: a, defaults: defaults) + InstancePorts.ports(for: b, defaults: defaults)
check("no port belongs to two instances", Set(all).count == all.count)

if failures > 0 { print("\(failures) check(s) failed."); exit(1) }
print("All instance port checks passed.")
SWIFT

swiftc -O "$here/../ios/App/App/InstancePorts.swift" "$work/main.swift" -o "$work/check" 2>&1
"$work/check"
