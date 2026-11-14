import Foundation
import Testing
import UIKit
@testable import Solo

// The rules the app shares with the server and the web client. Each of these
// has an answer both ends must agree on, so each is pinned here.

@Suite struct DayTests {
    @Test func parsesTheWireFormat() {
        #expect(Day("2026-11-13") == Day(year: 2026, month: 11, day: 13))
        #expect(Day("2026-11-13T00:00:00")?.day == 13)
        #expect(Day("2026-13-01") == nil)
        #expect(Day("yesterday") == nil)
    }

    @Test func arithmeticCrossesMonthsYearsAndLeapDays() {
        #expect(Day(year: 2026, month: 12, day: 31).adding(days: 1) == Day(year: 2027, month: 1, day: 1))
        #expect(Day(year: 2024, month: 2, day: 28).adding(days: 1) == Day(year: 2024, month: 2, day: 29))
        #expect(Day(year: 2026, month: 3, day: 1).adding(days: -1) == Day(year: 2026, month: 2, day: 28))
        #expect(Day(year: 2026, month: 11, day: 13).days(until: Day(year: 2026, month: 12, day: 13)) == 30)
    }

    @Test func dayNumbersRoundTrip() {
        for number in stride(from: -1000, to: 40000, by: 137) {
            #expect(Day(dayNumber: number).dayNumber == number)
        }
    }

    @Test func knowsItsWeekday() {
        let friday = Day(year: 2026, month: 11, day: 13)
        #expect(friday.weekday == 6)
        #expect(!friday.isWeekend)
        #expect(friday.adding(days: 1).isWeekend)
        #expect(friday.adding(days: 2).isWeekend)
        #expect(!friday.adding(days: 3).isWeekend)
    }

    @Test func ordersByDate() {
        #expect(Day(year: 2026, month: 2, day: 28) < Day(year: 2026, month: 3, day: 1))
        #expect(Day(year: 2025, month: 12, day: 31) < Day(year: 2026, month: 1, day: 1))
    }
}

@Suite struct DateParsingTests {
    @Test func readsEveryShapeTheServerWrites() throws {
        let whole = try #require(SoloJSON.parseDate("2026-11-14T00:25:45Z"))
        let fraction = try #require(SoloJSON.parseDate("2026-11-14T00:25:45.5+00:00"))
        let micro = try #require(SoloJSON.parseDate("2026-11-14T00:25:45.174999+00:00"))
        #expect(abs(fraction.timeIntervalSince(whole) - 0.5) < 0.001)
        #expect(abs(micro.timeIntervalSince(whole) - 0.174999) < 0.001)
        #expect(SoloJSON.parseDate("not a date") == nil)
    }

    @Test func respectsTheOffset() throws {
        let utc = try #require(SoloJSON.parseDate("2026-11-14T02:00:00+00:00"))
        let ahead = try #require(SoloJSON.parseDate("2026-11-14T04:00:00+02:00"))
        #expect(utc == ahead)
    }
}

@Suite struct SerialNumberTests {
    @Test func parsesAndNormalizes() throws {
        let reference = try #require(TaskReference(serial: "dmd-pw26-2"))
        #expect(reference.projectAlias == "DMD")
        #expect(reference.boardAlias == "PW26")
        #expect(reference.number == 2)
        #expect(reference.serial == "DMD-PW26-2")
        #expect(reference.markdownToken == "||DMD-PW26-2||")
    }

    @Test func rejectsWhatIsNotASerial() {
        #expect(TaskReference(serial: "DMD-PW26") == nil)
        #expect(TaskReference(serial: "DMD-PW26-0") == nil)
        #expect(TaskReference(serial: "DMD-PW-26-2") == nil)
        #expect(TaskReference(serial: "DMD-PW26-two") == nil)
        #expect(TaskReference(serial: "TOOLONGALIAS-PW26-2") == nil)
    }

    @Test func findsTokensInText() {
        let text = "See ||DMD-PW26-2|| and ||X-Y-31||, but not |DMD-PW26-2| or ||DMD-PW26||."
        let found = text.matches(of: TaskReference.token).map { "\($0.1)-\($0.2)-\($0.3)" }
        #expect(found == ["DMD-PW26-2", "X-Y-31"])
    }
}

@Suite struct RankNeighborTests {
    private let ids = (0..<4).map { _ in UUID() }

    @Test func emptyZoneHasNoNeighbours() {
        let pair = RankNeighbors.compute([], index: 0)
        #expect(pair.preceding == nil && pair.following == nil)
    }

    @Test func firstMiddleAndLastSlots() {
        var pair = RankNeighbors.compute(ids, index: 0)
        #expect(pair.preceding == nil && pair.following == ids[0])
        pair = RankNeighbors.compute(ids, index: 2)
        #expect(pair.preceding == ids[1] && pair.following == ids[2])
        pair = RankNeighbors.compute(ids, index: 4)
        #expect(pair.preceding == ids[3] && pair.following == nil)
    }

    @Test func clampsAnIndexOutOfRange() {
        var pair = RankNeighbors.compute(ids, index: 99)
        #expect(pair.preceding == ids[3] && pair.following == nil)
        pair = RankNeighbors.compute(ids, index: -5)
        #expect(pair.preceding == nil && pair.following == ids[0])
    }
}

@Suite struct CSSColorTests {
    @Test func readsHex() throws {
        let short = try #require(CSSColor("#fff"))
        #expect(short == CSSColor(red: 1, green: 1, blue: 1))
        let long = try #require(CSSColor("#B52C32"))
        #expect(abs(long.red - 181.0 / 255) < 0.0001)
        #expect(abs(long.green - 44.0 / 255) < 0.0001)
        #expect(abs(long.blue - 50.0 / 255) < 0.0001)
        let translucent = try #require(CSSColor("#00000080"))
        #expect(abs(translucent.alpha - 128.0 / 255) < 0.0001)
    }

    @Test func readsFunctionsAndNames() throws {
        let red = try #require(CSSColor("rgb(255, 0, 0)"))
        #expect(red == CSSColor(red: 1, green: 0, blue: 0))
        let faint = try #require(CSSColor("rgba(0, 0, 0, 0.5)"))
        #expect(faint.alpha == 0.5)
        #expect(CSSColor("white") == CSSColor(red: 1, green: 1, blue: 1))
    }

    @Test func refusesNonsense() {
        #expect(CSSColor("#12") == nil)
        #expect(CSSColor("#ggg") == nil)
        #expect(CSSColor("rgb(1)") == nil)
    }
}

@Suite struct IconTests {
    /// A name the system doesn't have draws nothing at all, silently.
    @Test func everyMappedSymbolExists() {
        for icon in SoloIcons.all {
            #expect(UIImage(systemName: icon.symbol) != nil, "\(icon.name) maps to \(icon.symbol), which isn't a system symbol")
        }
    }
}
