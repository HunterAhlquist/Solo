import Foundation
import Metal
import RealityKit
import simd
import SwiftUI
import Testing
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif
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

@Suite struct CalendarPickerTests {
    private func calendar(firstWeekday: Int) -> Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        calendar.firstWeekday = firstWeekday
        return calendar
    }

    private func days(_ week: [Date?], _ calendar: Calendar) -> [Int?] {
        week.map { $0.map { calendar.component(.day, from: $0) } }
    }

    @Test func aMonthIsRowsOfSevenFromTheDayTheWeekStarts() throws {
        // November 2026 begins on a Sunday.
        let sunday = calendar(firstWeekday: 1)
        let month = try #require(sunday.date(from: DateComponents(year: 2026, month: 11, day: 1)))
        let weeks = CalendarPicker.weeks(of: month, in: sunday)
        #expect(weeks.count == 5)
        #expect(weeks.allSatisfy { $0.count == 7 })
        #expect(days(weeks[0], sunday) == [1, 2, 3, 4, 5, 6, 7])
        #expect(days(weeks[4], sunday) == [29, 30, nil, nil, nil, nil, nil])
    }

    @Test func aWeekThatStartsOnMondayPutsSundayLast() throws {
        let monday = calendar(firstWeekday: 2)
        let month = try #require(monday.date(from: DateComponents(year: 2026, month: 11, day: 1)))
        let weeks = CalendarPicker.weeks(of: month, in: monday)
        #expect(weeks.count == 6)
        #expect(days(weeks[0], monday) == [nil, nil, nil, nil, nil, nil, 1])
        #expect(days(weeks[5], monday) == [30, nil, nil, nil, nil, nil, nil])
    }

    @Test func februaryInALeapYearHasItsTwentyNinth() throws {
        let sunday = calendar(firstWeekday: 1)
        let month = try #require(sunday.date(from: DateComponents(year: 2028, month: 2, day: 1)))
        let all = CalendarPicker.weeks(of: month, in: sunday).flatMap { $0 }.compactMap { $0 }
        #expect(all.count == 29)
    }
}

@Suite struct NotificationLedgerTests {
    private func entry(_ id: Int, key: String? = nil, raised: Double = 1_000) -> NotificationItem {
        NotificationItem(id: UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", id))!,
                         kind: key == nil ? .assigned : .dueThisWeek, title: "Entry \(id)", body: nil, projectId: nil,
                         boardId: nil, taskId: nil, epicId: nil, createdAt: Date(timeIntervalSince1970: raised), readAt: nil,
                         key: key)
    }

    private func news(_ unread: [NotificationItem], seen: [NotificationItem]?, reminded: [String: Double] = [:],
                      pending: Set<String> = [], now: Double = 2_000) -> (news: Set<UUID>, superseded: [String]) {
        NotificationLedger.news(in: unread, seen: seen.map { Set($0.map(NotificationLedger.key)) }, reminded: reminded,
                                pending: pending, now: now)
    }

    @Test func theFirstLookAtAnInstanceAnnouncesNothing() {
        // An inbox full of old entries isn't news; it is only what is there.
        #expect(news([entry(1), entry(2)], seen: nil).news.isEmpty)
    }

    @Test func onlyWhatHasNotBeenSeenIsNews() {
        let verdict = news([entry(1), entry(2), entry(3)], seen: [entry(1), entry(2)])
        #expect(verdict.news == [entry(3).id])
        #expect(verdict.superseded.isEmpty)
    }

    @Test func anEntryRaisedAgainUnderTheSameIdIsNewsAgain() {
        // Cleared and raised again: the same id, a later time.
        #expect(news([entry(1, raised: 5_000)], seen: [entry(1)]).news == [entry(1).id])
    }

    @Test func aDeadlineEntryWhoseReminderThisDeviceShowedIsNotSaidTwice() {
        let key = "DueThisWeek:task:2026-11-26"
        #expect(news([entry(1, key: key)], seen: [], reminded: [key: 1_500]).news.isEmpty)
        // One the device never scheduled — the deadline was set after its last look — is news.
        #expect(news([entry(1, key: key)], seen: []).news == [entry(1).id])
        // And so is one whose reminder is on record for a time still to come and is not
        // pending: something took it down before it could say anything.
        #expect(news([entry(1, key: key)], seen: [], reminded: [key: 9_000]).news == [entry(1).id])
    }

    @Test func anEntryThatBeatsItsReminderTakesTheReminderDown() {
        let key = "DueThisWeek:task:2026-11-26"
        let verdict = news([entry(1, key: key)], seen: [], reminded: [key: 9_000], pending: [key])
        #expect(verdict.news == [entry(1).id])
        #expect(verdict.superseded == [key])
    }

    @Test func aReminderThatFiredIsRememberedForAMonthAndOnePendingIsNot() {
        let now = 100 * 86_400.0
        let kept = NotificationLedger.fired(["fired": now - 86_400, "old": now - 31 * 86_400, "pending": now + 60], now: now)
        #expect(kept == ["fired": now - 86_400])
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

@MainActor @Suite struct OfflineNavigationTests {
    private let doc = Route.designDocs(project: "DMD", vaultId: UUID())
    private let board = Route.board(project: "DMD", board: "PW26")

    /// A navigator that has been somewhere, with one design doc on the device.
    private func navigator() -> (Navigator, refused: () -> [Route]) {
        let navigator = Navigator()
        var refused: [Route] = []
        navigator.worksOffline = { $0 == doc || $0 == .designDocs(project: "DMD", vaultId: doc.vault, noteId: doc.vault) }
        navigator.refused = { refused.append($0) }
        return (navigator, { refused })
    }

    @Test func outOfReachOnlyWhatIsOnTheDeviceOpens() {
        let (navigator, refused) = navigator()
        navigator.open(board)
        navigator.offline = true
        // Where it was is kept, to come back to.
        #expect(navigator.tab == .projects)

        navigator.open(.board(project: "DMD", board: "OTHER"))
        navigator.open(.admin)
        navigator.push(.project(alias: "DMD"))
        #expect(refused().count == 3)
        #expect(navigator[.projects] == [.project(alias: "DMD"), board])

        // On its own under the list of what there is, not under a project's page.
        navigator.open(doc)
        #expect(navigator.tab == .offline)
        #expect(navigator[.offline] == [doc])
        // A section's first page, or a project's, is that list.
        navigator.popToRoot(.work)
        #expect(navigator.tab == .offline)
        #expect(navigator[.offline].isEmpty)
    }

    @Test func backInReachItIsWhereItWas() {
        let (navigator, _) = navigator()
        navigator.open(board)
        navigator.offline = true
        navigator.popToRoot(.projects)
        #expect(navigator.tab == .offline)
        navigator.offline = false
        #expect(navigator.tab == .projects)
        #expect(navigator.current == board)
    }

    @Test func aDesignDocOpenedOutOfReachStaysOpenUntilItIsLeft() {
        let (navigator, _) = navigator()
        navigator.popToRoot(.inbox)
        navigator.offline = true
        navigator.open(doc)
        navigator.offline = false
        // Still the page it was: someone may be writing in it.
        #expect(navigator.tab == .offline)
        #expect(navigator.current == doc)
        // Back from it is no longer to a list of what works without the instance.
        navigator[.offline] = []
        #expect(navigator.tab == .inbox)
    }

    @Test func aDesignDocOpenWhenTheConnectionGoesIsNotMoved() {
        let (navigator, _) = navigator()
        navigator.open(doc)
        navigator.offline = true
        #expect(navigator.tab == .projects)
        #expect(navigator[.projects] == [.project(alias: "DMD"), doc])
    }
}

private extension Route {
    var vault: UUID {
        if case .designDocs(_, let id, _) = self { return id }
        return UUID()
    }
}

@Suite struct SavedInstanceTests {
    @Test func anAddressIsTheHostAndAPortThatIsNotTheUsualOne() throws {
        let hosted = SavedInstance(url: try #require(URL(string: "https://solo.example.com")), name: "Studio")
        let local = SavedInstance(url: try #require(URL(string: "http://localhost:5155")), name: "Studio")
        #expect(hosted.address == "solo.example.com")
        #expect(local.address == "localhost:5155")
        // Two addresses of one machine are two instances: each keeps its own session.
        #expect(hosted.id != local.id)
    }

    @Test func theListReadsBackAsItWasKept() throws {
        let url = try #require(URL(string: "https://solo.example.com"))
        let kept = [SavedInstance(url: url, name: "Studio", signedInAs: "Ada"), SavedInstance(url: url, name: "Studio")]
        let read = try JSONDecoder().decode([SavedInstance].self, from: JSONEncoder().encode(kept))
        #expect(read == kept)
        #expect(read[1].signedInAs == nil)
    }

    @Test func whatIsTypedBecomesTheSameAddressHoweverItIsTyped() {
        #expect(AppModel.normalize("solo.example.com/") == AppModel.normalize("https://solo.example.com"))
        #expect(AppModel.normalize("localhost:5155")?.absoluteString == "http://localhost:5155")
    }
}

@Suite struct IconTests {
    /// A name the system doesn't have draws nothing at all, silently.
    @Test func everyMappedSymbolExists() {
        for icon in SoloIcons.all {
            #expect(symbolExists(icon.symbol), "\(icon.name) maps to \(icon.symbol), which isn't a system symbol")
        }
    }
}

@Suite @MainActor struct CardDragWatchTests {
    private let card = UUID(), epic = UUID(), group = UUID()
    private let stood = CGRect(x: 20, y: 300, width: 340, height: 70)

    /// A watch that knows three names: a card's, a lane's and a group's.
    private func watch() -> CardDragWatch {
        let watch = CardDragWatch()
        watch.resolve = { [card, epic, group, stood] name in
            switch name {
            case "CARD": .card(card, frame: stood)
            case "EPIC": .epic(epic, frame: stood)
            case "GROUP": .group(group, frame: stood)
            default: nil
            }
        }
        return watch
    }

    /// Waits for the second step of picking something up or giving it back: its place closing
    /// behind it, or opening again. Given time enough, however busy the tests beside this are.
    private func settled(_ watch: CardDragWatch, closed: Bool) async throws {
        for _ in 0..<60 where (watch.left != nil) != closed { try await Task.sleep(for: .milliseconds(50)) }
        #expect((watch.left != nil) == closed)
    }

    /// The edges of a phone's board only take touches while a card is in hand.
    @Test func aDragIsOnFromTheFirstTargetToTheDrop() {
        let watch = CardDragWatch()
        #expect(!watch.active)
        watch.hover(true)
        #expect(watch.active)
        // Leaving one target for the next is not the end of the drag.
        watch.hover(false)
        #expect(watch.active)
        watch.hover(true)
        watch.ended()
        #expect(!watch.active)
    }

    @Test func whatIsInHandIsKnownByTheNameItArrivesWith() {
        let watch = watch()
        watch.offered("CARD")
        watch.hover(true)
        #expect(watch.card == card)
        #expect(watch.payload == "CARD")
        #expect(watch.carriesCard)
        // The same thing goes on being reported by every place it passes over.
        watch.offered("EPIC")
        #expect(watch.card == card)
        watch.ended()
        #expect(watch.lifted == nil)
        #expect(watch.payload == nil)
    }

    @Test func itIsInHandAtOnceAndItsPlaceClosesAMomentAfter() async throws {
        let watch = watch()
        watch.offered("CARD")
        // In hand, and its place still open: the two are not one change.
        #expect(watch.card == card)
        #expect(watch.left == nil)
        try await settled(watch, closed: true)
        #expect(watch.left == card)
        // Given back the same way round: out of hand at once, and then its place opens.
        watch.ended()
        #expect(watch.lifted == nil)
        #expect(watch.left == card)
        try await settled(watch, closed: false)
    }

    /// Let go somewhere that will not have it, a thing is carried back by the system, which
    /// goes on saying what it is over on the way.
    @Test func whatTheSystemSaysJustAfterALetGoPicksNothingUp() async throws {
        let watch = watch()
        watch.offered("CARD")
        watch.hover(true)
        watch.ended()
        #expect(watch.justLetGo)
        watch.offered("CARD")
        watch.hover(true)
        watch.aim(CardDragWatch.Slot(epicId: nil, status: .unstarted, index: 0), true)
        #expect(watch.lifted == nil)
        #expect(!watch.active)
        #expect(watch.slot == nil)
        // And a moment later a thing can be picked up again.
        try await Task.sleep(for: .milliseconds(450))
        #expect(!watch.justLetGo)
        watch.offered("CARD")
        #expect(watch.card == card)
    }

    /// Let go within a few points of the screen's edge, a thing is not said to have been.
    @Test func aThingTheSystemStopsSpeakingOfHasBeenLetGo() async throws {
        let watch = watch()
        watch.offered("CARD")
        watch.hover(true)
        var letGo = false
        // Said many times a second, the same place each time: it is being carried, and held still.
        // For half a second, and not a few times only: the tests run inside the app, which on a
        // Mac is drawing a board as it starts, and a few sayings in a row can each arrive late
        // enough not to count as the same thing said twice.
        for _ in 0..<25 {
            watch.alive(at: CGPoint(x: 10, y: 20)) { letGo = true }
            try await Task.sleep(for: .milliseconds(20))
        }
        #expect(!letGo)
        for _ in 0..<60 where !letGo { try await Task.sleep(for: .milliseconds(50)) }
        #expect(letGo)
    }

    @Test func oneThatLeavesAPlaceIsNotLetGoThere() async throws {
        let watch = watch()
        watch.offered("CARD")
        watch.hover(true)
        var letGo = false
        for _ in 0..<6 {
            watch.alive(at: CGPoint(x: 10, y: 20)) { letGo = true }
            try await Task.sleep(for: .milliseconds(20))
        }
        watch.quiet()
        try await Task.sleep(for: .milliseconds(900))
        #expect(!letGo)
        #expect(watch.card == card)
    }

    @Test func silenceIsNotTakenForAnythingUntilTheSystemIsHeardToRepeatItself() async throws {
        let watch = watch()
        watch.offered("CARD")
        watch.hover(true)
        var letGo = false
        // Each time somewhere new: nothing says it would speak of a thing held still.
        for step in 0..<6 {
            watch.alive(at: CGPoint(x: 10 + CGFloat(step), y: 20)) { letGo = true }
            try await Task.sleep(for: .milliseconds(20))
        }
        try await Task.sleep(for: .milliseconds(900))
        #expect(!letGo)
        #expect(watch.card == card)
    }

    @Test func oneGivenBackIsBackWhenTheNextIsPickedUp() async throws {
        let watch = watch()
        watch.offered("CARD")
        try await settled(watch, closed: true)
        watch.ended()
        // A press long enough to pick something up.
        try await Task.sleep(for: .milliseconds(450))
        watch.offered("EPIC")
        // The card's place is not left closed behind the lane.
        #expect(watch.left == nil)
        try await settled(watch, closed: true)
        #expect(watch.left == epic)
    }

    @Test func somethingFromElsewhereIsCarriedButIsNothingOfTheBoards() {
        let watch = watch()
        watch.offered("a line of text")
        watch.offered(nil)
        watch.hover(true)
        #expect(watch.active)
        #expect(watch.lifted == nil)
        // Taken for a card, since a card is what could be let go here.
        #expect(watch.carriesCard)
    }

    @Test func aCardStaysUnderTheFingerWhereItWasTakenHoldOf() {
        let watch = watch()
        watch.offered("CARD")
        watch.move(to: CGPoint(x: 120, y: 320))
        #expect(watch.grab == CGSize(width: 100, height: 20))
        watch.move(to: CGPoint(x: 150, y: 500))
        #expect(watch.grab == CGSize(width: 100, height: 20))
        #expect(watch.spot == CGPoint(x: 150, y: 500))
    }

    @Test func somewhereOffThePageSaysWhereTheFingerIsOnTheScreen() {
        let watch = watch()
        watch.pageOrigin = CGPoint(x: 0, y: -400)
        watch.offered("CARD")
        watch.move(toScreen: CGPoint(x: 120, y: 60))
        #expect(watch.spot == CGPoint(x: 120, y: 460))
    }

    @Test func roomIsMadeOnlyWhereTheCardInHandWouldLand() async throws {
        let watch = watch()
        watch.offered("CARD")
        let place = CardDragWatch.Slot(epicId: epic, status: .inProgress, index: 2)
        watch.aim(place, true)
        // Not until its own place closes: the two are one movement.
        #expect(watch.gap(epicId: epic, status: .inProgress, index: 2) == 0)
        try await settled(watch, closed: true)
        #expect(watch.gap(epicId: epic, status: .inProgress, index: 2) == 70)
        #expect(watch.gap(epicId: epic, status: .inProgress, index: 3) == 0)
        #expect(watch.gap(epicId: nil, status: .inProgress, index: 2) == 0)
        #expect(watch.aimed)
    }

    @Test func aLaneInHandFoldsEveryLaneAndAGroupEveryGroupToo() async throws {
        let lane = watch()
        lane.offered("EPIC")
        lane.hover(true)
        // The arrows at the page's edges are for a card.
        #expect(!lane.carriesCard)
        try await settled(lane, closed: true)
        #expect(lane.foldsLanes)
        #expect(!lane.foldsGroups)
        lane.ended()
        try await settled(lane, closed: false)
        #expect(!lane.foldsLanes)

        let groups = watch()
        groups.offered("GROUP")
        try await settled(groups, closed: true)
        #expect(groups.foldsLanes)
        #expect(groups.foldsGroups)
    }

    @Test func roomForALaneIsAsTallAsItIsDrawnInHand() async throws {
        let watch = watch()
        watch.offered("EPIC")
        watch.holds(height: 66)
        let other = UUID()
        watch.steer(to: .before(other))
        try await settled(watch, closed: true)
        #expect(watch.room(for: .before(other)) == 66)
        #expect(watch.room(for: .inside(other)) == 0)
        #expect(watch.room(for: .last) == 0)
        // A card has no berth.
        let cards = self.watch()
        cards.offered("CARD")
        cards.steer(to: .last)
        #expect(cards.berth == nil)
    }

    @Test func aLaneLetGoWaitsInItsRoomAndThePageOpensAfterIt() async throws {
        let watch = watch()
        watch.offered("EPIC")
        watch.hover(true)
        watch.steer(to: .last)
        try await settled(watch, closed: true)
        watch.settle()
        #expect(watch.settling)
        #expect(!watch.active)
        // Nothing moves it now.
        watch.steer(to: .before(UUID()))
        #expect(watch.berth == .last)
        watch.landed()
        #expect(watch.lifted == nil)
        #expect(watch.left == nil)
        #expect(watch.berth == nil)
        // Still folded for a moment: it is put down among the names it was carried over.
        #expect(watch.foldsLanes)
        // And then not: given time enough, however busy the tests beside this one are.
        for _ in 0..<60 where watch.foldsLanes { try await Task.sleep(for: .milliseconds(50)) }
        #expect(!watch.foldsLanes)
    }
}

@Suite struct LaneBerthTests {
    private let g1 = UUID(), g2 = UUID(), a = UUID(), b = UUID(), loose = UUID()

    /// A group's title over its two lanes, then a lane in no group, then a group that is folded.
    private var rows: [LaneBerths.Row] {
        [.init(id: g1, group: g1), .init(id: a, group: g1), .init(id: b, group: g1),
         .init(id: loose, group: nil), .init(id: g2, group: g2)]
    }

    private var frames: [UUID: CGRect] {
        [g1: CGRect(x: 0, y: 0, width: 300, height: 40), a: CGRect(x: 0, y: 50, width: 300, height: 60),
         b: CGRect(x: 0, y: 120, width: 300, height: 60), loose: CGRect(x: 0, y: 200, width: 300, height: 60),
         g2: CGRect(x: 0, y: 270, width: 300, height: 40)]
    }

    private func berth(_ y: CGFloat, joins: Bool = true) -> CardDragWatch.Berth {
        LaneBerths.berth(at: y, rows: rows, frames: frames, joins: joins)
    }

    @Test func aLaneGoesBeforeTheFirstRowWhoseMiddleIsBelowTheFinger() {
        #expect(berth(10) == .before(g1))
        // Past the title's middle it is in the group, first.
        #expect(berth(30) == .before(a))
        #expect(berth(100) == .before(b))
    }

    @Test func levelWithAGroupsLastRowIsTheEndOfTheGroupAndPastItIsOutside() {
        #expect(berth(170) == .inside(g1))
        #expect(berth(190) == .before(loose))
        // A folded group is its title, and the same goes for that.
        #expect(berth(300) == .inside(g2))
        #expect(berth(320) == .last)
    }

    @Test func betweenTwoLanesInNoGroupThereIsNoGroupToJoin() {
        #expect(berth(240) == .before(g2))
    }

    @Test func aGroupGoesAmongTheGroupsAndIntoNone() {
        #expect(berth(170, joins: false) == .before(loose))
        #expect(berth(300, joins: false) == .last)
    }

    @Test func aRowThatIsNotBuiltIsPassedOver() {
        var frames = frames
        frames[b] = nil
        // The end of the group is then judged by the last of its rows that stands somewhere.
        #expect(LaneBerths.berth(at: 100, rows: rows, frames: frames) == .inside(g1))
        #expect(LaneBerths.berth(at: 150, rows: rows, frames: frames) == .before(loose))
    }
}

@Suite struct ThemeContrastTests {
    private let yellow = CSSColor("#fff600")!
    private let navy = CSSColor("#102a6b")!

    @Test func aLabelOnAnAccentIsWhicheverOfBlackAndWhiteReads() {
        #expect(yellow.readable == .black)
        #expect(navy.readable == .white)
    }

    /// A switch's knob and a swipe action's icon are white whatever is under them.
    @Test func aFillTheSystemPutsWhiteOnIsDarkEnoughToCarryIt() {
        let fill = yellow.carryingWhite
        #expect(fill.contrast(with: .white) >= 3)
        // Only as much darker as it has to be, and still the same hue: no blue has crept in.
        #expect(fill.contrast(with: .white) < 3.2)
        #expect(fill.blue == 0 && fill.red > fill.blue && abs(fill.red / fill.green - yellow.red / yellow.green) < 0.001)
        // A colour that already carries white is left alone.
        #expect(navy.carryingWhite == navy)
    }

    /// Before an instance's theme is known the accent is the app's own, and that one is a
    /// bright yellow in the dark: Connect was white on it.
    @Test func theAppsOwnAccentCarriesALabelThatReads() {
        #expect(Theme.fallback(dark: true).onPrimary == .black)
        for dark in [true, false] {
            let accent: CSSColor = dark ? .appAccentDark : .appAccentLight
            let theme = Theme.fallback(dark: dark)
            #expect(theme.primary == accent.color)
            #expect(accent.contrast(with: theme.onPrimary == .black ? .black : .white) >= 4.5)
            #expect(accent.carryingWhite.contrast(with: .white) >= 3)
        }
    }

    /// The numbers the app works from are the catalogue's colour, which is what a control
    /// with no tint of its own is drawn in.
    @Test func theAppsOwnAccentIsTheCataloguesColour() {
        for (dark, accent) in [(true, CSSColor.appAccentDark), (false, CSSColor.appAccentLight)] {
            var environment = EnvironmentValues()
            environment.colorScheme = dark ? .dark : .light
            let drawn = Color("AccentColor", bundle: .main).resolve(in: environment)
            #expect(abs(Double(drawn.red) - accent.red) < 0.005)
            #expect(abs(Double(drawn.green) - accent.green) < 0.005)
            #expect(abs(Double(drawn.blue) - accent.blue) < 0.005)
        }
    }
}

@Suite struct OnboardingTests {
    @Test func aFirstLaunchStartsAtTheWelcome() {
        #expect(Onboarding.start(seen: false, knowsAnInstance: false) == .welcome)
    }

    /// Someone who has been through it, or who had an instance once, is asked for the address.
    @Test func anyoneWhoKnowsWhatAnInstanceIsGoesStraightToConnecting() {
        #expect(Onboarding.start(seen: true, knowsAnInstance: false) == .connect)
        #expect(Onboarding.start(seen: false, knowsAnInstance: true) == .connect)
    }

    @Test func aPieceIsPulledSlightlyAndNoFurther() {
        #expect(PieceSpring.pull(for: .zero) == .zero)
        let near = PieceSpring.pull(for: [0.02, 0])
        let far = PieceSpring.pull(for: [5, 0])
        let farther = PieceSpring.pull(for: [50, 0])
        // It follows a short drag nearly all the way, and a long one hardly at all.
        #expect(near.x > 0.018 && near.x < 0.02)
        #expect(far.x <= PieceSpring.reach && farther.x <= PieceSpring.reach)
        #expect(farther.x - far.x < 0.001)
        // The way it was dragged, and toward the eye.
        let up = PieceSpring.pull(for: [0, 0.3])
        #expect(up.x == 0 && up.y > 0 && up.z > 0)
    }

    /// Let go anywhere, at any speed, it is home and still within a few seconds: pushed the
    /// way the spring says for as long as that takes.
    @Test func aPieceLetGoComesHome() {
        var position = SIMD3<Float>(0.2, -0.15, 0.06), velocity = SIMD3<Float>(1, 0, -2)
        let step: Float = 1.0 / 60
        for _ in 0..<(60 * 4) {
            velocity += PieceSpring.push(toward: .zero, from: position, velocity: velocity, held: false) * step
            position += velocity * step
        }
        #expect(simd_length(position) < 0.001)
        #expect(simd_length(velocity) < 0.01)
    }

    /// In the hand it goes where it is held without passing it, even at a slow frame rate.
    @Test func aHeldPieceFollowsWithoutOvershooting() {
        let target = SIMD3<Float>(0.18, 0, 0)
        var position = SIMD3<Float>.zero, velocity = SIMD3<Float>.zero
        let step: Float = 1.0 / 30
        var farthest: Float = 0
        for _ in 0..<60 {
            velocity += PieceSpring.push(toward: target, from: position, velocity: velocity, held: true) * step
            position += velocity * step
            farthest = max(farthest, position.x)
        }
        #expect(abs(position.x - target.x) < 0.002)
        #expect(farthest < target.x * 1.05)
    }

    @Test func aPieceTurnsBackTheShortWayRound() {
        let home = simd_quatf(angle: 0, axis: [0, 1, 0])
        let turned = simd_quatf(angle: 0.4, axis: [0, 1, 0])
        // Turned one way about y, it is pushed back the other way about y, and about nothing else.
        let back = PieceSpring.twist(toward: home, from: turned, spin: .zero)
        #expect(back.y < 0 && abs(back.x) < 0.0001 && abs(back.z) < 0.0001)
        // The same facing written the other way round is no turn at all.
        let same = PieceSpring.twist(toward: home, from: simd_quatf(vector: -home.vector), spin: .zero)
        #expect(simd_length(same) < 0.0001)
    }

    /// The stage shows all of what it is asked to, in a view of any shape.
    @Test func aStageFitsWhatItHoldsIntoAnyView() {
        let extent = CGSize(width: 2, height: 1)
        #expect(Stage.visibleHeight(extent: extent, view: CGSize(width: 400, height: 100)) == 1)
        #expect(Stage.visibleHeight(extent: extent, view: CGSize(width: 100, height: 100)) == 2)
        // The middle of the stage is the middle of the view, and up is up.
        let view = CGSize(width: 300, height: 200)
        #expect(Stage.point(.zero, extent: extent, view: view) == CGPoint(x: 150, y: 100))
        #expect(Stage.point(CGPoint(x: 0, y: 0.5), extent: extent, view: view).y < 100)
        #expect(Stage.point(CGPoint(x: 1, y: 0), extent: extent, view: view).x == 300)
    }

    /// The bars are the icon's glass: the one in front is seen through most, and each is a
    /// fade from a lighter colour to a deeper one.
    @Test func theLogosBarsAreSeenThroughAsTheIconsAre() {
        #expect(SoloLogo.bars.map(\.name) == ["BarFront", "BarMiddle", "BarBack"])
        for (near, far) in zip(SoloLogo.bars, SoloLogo.bars.dropFirst()) {
            #expect(near.opacity.dark < far.opacity.dark && near.opacity.light < far.opacity.light)
        }
        #expect(SoloLogo.bars.allSatisfy { $0.opacity.dark > 0 && $0.opacity.dark < 1 })
        for dark in [true, false] {
            let fill = SoloLogo.fill(dark: dark)
            #expect(fill.top != fill.bottom)
        }
    }

    /// The shader reads what it is handed by where things are in it. These are the sizes
    /// its own two structures come to; a field added on one side only would shift the rest.
    @Test func whatTheGlassPassIsHandedIsLaidOutAsItsShaderExpects() {
        #expect(MemoryLayout<LensShape>.stride == 2 * 64 + 5 * 16)
        #expect(MemoryLayout<LensUniforms>.stride == LensUniforms.room * MemoryLayout<LensShape>.stride + 6 * 16)
        #expect(MemoryLayout<BloomUniforms>.stride == 3 * 16)
    }

    /// The pass works on the part of the frame the glass is in and no more, so the box it
    /// works out for a piece has to hold the piece.
    @Test func aPiecesBoxOnScreenHoldsIt() {
        let projection = GlassFrame.projection
        // Straight ahead is the middle of the screen; up is up, and right is right.
        let ahead = Lens.window(of: [[0, 0, -4]], projection: projection)
        #expect(abs(ahead.x - 0.5) < 0.0001 && abs(ahead.y - 0.5) < 0.0001)
        let box = Lens.window(of: [[-0.2, -0.3, -4], [0.2, 0.3, -4]], projection: projection)
        #expect(box.x < 0.5 && box.z > 0.5 && box.y < 0.5 && box.w > 0.5)
        #expect(abs((box.z - box.x) - 0.4) < 0.0001 && abs((box.w - box.y) - 0.6) < 0.0001)
        // Something not in front of the camera can't be placed: all of the screen, to be safe.
        #expect(Lens.window(of: [[0, 0, 1]], projection: projection) == [0, 0, 1, 1])

        // As points of a picture: a point to spare all round, and none of it off the edge.
        let patch = Lens.patch([0.25, 0.5, 0.75, 1.2], width: 200, height: 100)
        #expect(patch?.x == 49 && patch?.y == 49 && patch?.width == 102 && patch?.height == 51)
        #expect(Lens.patch([1.2, 0, 1.5, 1], width: 200, height: 100) == nil)

        // A glow's room is square as the picture measures it, whatever shape the picture is.
        let room = Lens.glowWindow(of: [0.4, 0.45, 0.6, 0.5], width: 200, height: 400)
        #expect(abs((room.z - room.x) * 200 - (room.w - room.y) * 400) < 0.001)
        #expect(room.x < 0.4 && room.z > 0.6 && room.y < 0.45 && room.w > 0.5)
    }

    /// The pass itself, on a frame of the test's own: a grey frame with one piece of red
    /// glass in the middle of it. This is also what says the shader still compiles — it is
    /// text until the app runs, and the first that was written didn't.
    @MainActor @Test func theGlassPassTintsWhereTheGlassIsAndLeavesTheRest() throws {
        let frame = try #require(GlassFrame(width: 200, height: 200))
        frame.fill { _, _ in (128, 128, 128, 255) }
        let glass = GlassFrame.lens(half: [0.2, 0.3], top: [1, 0, 0, 0.5], bottom: [1, 0, 0, 1])
        let after = try #require(frame.run(lenses: [glass], glow: nil))

        // Outside the glass the frame is as it was, to the last bit.
        #expect(after(5, 5) == (128, 128, 128, 255))
        #expect(after(100, 20) == (128, 128, 128, 255))
        // Through the middle of it: half red, half what was there.
        let middle = after(100, 100)
        #expect(middle.red > 180 && middle.green < 110 && middle.blue < 110 && middle.alpha == 255)
    }

    /// What is behind the glass is seen larger, from nearer the glass's middle: a line that
    /// runs behind it comes out further from the middle than it went in.
    @MainActor @Test func theGlassPassBendsWhatIsBehindIt() throws {
        let frame = try #require(GlassFrame(width: 200, height: 200))
        // White to the right of a line twelve points right of the middle.
        frame.fill { x, _ in x >= 112 ? (255, 255, 255, 255) : (0, 0, 0, 255) }
        let glass = GlassFrame.lens(half: [0.2, 0.3], top: [0, 0, 0, 0.001], bottom: [0, 0, 0, 1])
        let after = try #require(frame.run(lenses: [glass], glow: nil))

        // Above the glass the line is where it was drawn.
        #expect(after(110, 20).red < 40 && after(114, 20).red > 215)
        // Behind it, the point just right of the line shows what is nearer the middle: black.
        #expect(after(114, 100).red < 40)
        #expect(after(125, 100).red > 215)
    }

    /// A piece that gives light has it laid round it, and only round it.
    @MainActor @Test func theGlassPassLaysAGlowRoundWhatGivesLight() throws {
        let frame = try #require(GlassFrame(width: 200, height: 200))
        // A white square in the middle of nothing at all, sixty points across.
        frame.fill { x, y in (70..<130).contains(x) && (70..<130).contains(y) ? (255, 255, 255, 255) : (0, 0, 0, 0) }
        let corners = (0..<8).map { corner -> SIMD3<Float> in
            [corner & 1 == 0 ? -0.15 : 0.15, corner & 2 == 0 ? -0.15 : 0.15, corner & 4 == 0 ? -4.01 : -3.99]
        }
        let after = try #require(frame.run(lenses: [], glow: GlowSource(corners: corners, colour: [1, 1, 1], strength: 1)))

        // Just outside the square there is light where there was nothing; far from it, still nothing.
        #expect(after(132, 100).red > 20 && after(132, 100).alpha > 20)
        #expect(after(100, 67).red > 20)
        #expect(after(10, 10) == (0, 0, 0, 0))
        #expect(after(190, 100) == (0, 0, 0, 0))
        // Nearer is brighter.
        #expect(after(131, 100).red > after(137, 100).red)
    }

    /// A slab is looked through its thinnest side, wherever its file put that.
    @Test func aSlabsLensLiesAcrossItsThinnestSide() {
        // As a bar comes from Blender: thin from front to back, which there is y.
        let lens = Lens.lensFromLocal(center: [0.1, 0, -0.2], extents: [0.36, 0.06, 0.6])
        #expect(Set([lens.half.x, lens.half.y]) == Set([Float(0.18), 0.3]))
        // Its middle is the lens's origin, and a step through it is a step along the lens's z.
        let middle = lens.matrix * SIMD4<Float>(0.1, 0, -0.2, 1)
        #expect(simd_length(simd_make_float3(middle)) < 0.0001)
        let through = lens.matrix * SIMD4<Float>(0.1, 0.03, -0.2, 1)
        #expect(abs(abs(through.z) - 0.03) < 0.0001 && abs(through.x) < 0.0001 && abs(through.y) < 0.0001)
    }

    /// Docker asks for half its symbol's width left clear all round it. The models stand
    /// outside that, and every arrow starts outside it and goes away from it.
    @Test func dockersSymbolIsLeftItsClearSpace() {
        let clear = HostingModels.symbolClearSpace
        for arrow in HostingModels.arrows {
            #expect(!clear.contains(arrow.from) && !clear.contains(arrow.bend) && !clear.contains(arrow.to))
        }
        for model in [HostingModels.server, HostingModels.cloud, HostingModels.laptop] {
            #expect(!clear.contains(model))
        }
        // As it is drawn: 1252 by 757.78.
        #expect(abs(HostingModels.symbol.width / HostingModels.symbol.height - 1252 / 757.78) < 0.001)
    }

    #if os(iOS)
    /// The connect screen shows a picture of the app's icon, and there is one for each icon
    /// a build can wear.
    @MainActor @Test func theAppCarriesAPictureOfEachOfItsIcons() {
        #expect(AppIconImage.artwork == "AppIconArtwork")
        for name in ["AppIconArtwork", "AppIconArtworkPreview"] {
            #expect((UIImage(named: name)?.size.width ?? 0) >= 256, "no picture called \(name)")
        }
    }
    #endif

    /// The face is read from its Aseprite file by the app: the frames it has, and the tags
    /// that say which is which.
    @Test func theFacesDrawingIsReadAsItWasDrawn() throws {
        let sheet = try #require(Guy.sheet)
        #expect(sheet.width == 16 && sheet.height == 16)
        #expect(sheet.tags["Smile"]?.count == 1 && sheet.tags["Idle"]?.count == 2 && sheet.tags["Talk"]?.count == 2)
        #expect(sheet.frames.count == 5 && sheet.frames.allSatisfy { $0.filled.count == 256 && $0.filled.contains(true) })
        // Each thing it does is a frame of its own, and the second of a pair is not the first.
        let poses: [GuyPose] = [.smile, .idle(blinking: false), .idle(blinking: true), .talk(open: false), .talk(open: true)]
        let frames = poses.map { Guy.frame(for: $0, in: sheet) }
        #expect(Set(frames).count == 5)
        #expect(sheet.frames[frames[1]].filled != sheet.frames[frames[2]].filled)
        // What isn't such a file is nothing, and is not read past its end.
        #expect(AsepriteSheet(data: Data([1, 2, 3])) == nil)
        #expect(AsepriteSheet(data: Data(repeating: 0, count: 400)) == nil)
    }

    /// A drawing made of square points is cut out along closed lines, one round each part
    /// of it, and one more round each hole.
    @Test func aDrawingsEdgeGoesRoundEachOfItsParts() {
        func loops(_ rows: [String]) -> [[SIMD2<Int>]] {
            PixelOutline.loops(filled: rows.joined().map { $0 == "#" }, width: rows[0].count, height: rows.count)
        }
        // A bar two points long is one box, and only its corners are kept.
        #expect(loops(["##"]) == [[[0, 0], [2, 0], [2, 1], [0, 1]]])
        // A ring is its outside and its hole, going opposite ways round.
        let ring = loops(["###", "#.#", "###"])
        #expect(ring.count == 2 && ring.allSatisfy { $0.count == 4 })
        func turning(_ loop: [SIMD2<Int>]) -> Int {
            zip(loop, loop.dropFirst() + [loop[0]]).reduce(0) { $0 + $1.0.x * $1.1.y - $1.1.x * $1.0.y }
        }
        #expect(turning(ring[0]) * turning(ring[1]) < 0)
        // Two points that meet only at a corner are two parts, not one with a waist.
        let corner = loops(["#.", ".#"])
        #expect(corner.count == 2 && corner.allSatisfy { $0.count == 4 })
        // An L is one part with six corners.
        #expect(loops(["#.", "##"]).map(\.count) == [6])
    }

    /// The face is where its screen has it, and on the way between two screens it is
    /// between their places, as the row of screens is between the two.
    @MainActor @Test func theFaceGoesFromOneScreensPlaceToTheNext() {
        let guide = OnboardingGuide(start: .welcome)
        guide.anchors = [.welcome: CGRect(x: 200, y: 400, width: 100, height: 80),
                         .hosting: CGRect(x: 20, y: 500, width: 60, height: 48),
                         .connect: CGRect(x: 180, y: 800, width: 40, height: 32)]
        let now = Date.now
        #expect(guide.place(at: now)?.rect == guide.anchors[.welcome])
        #expect(guide.page(at: now) == 0)
        #expect(guide.landed == false && guide.settled == false)

        guide.go(to: .hosting, over: 10)
        let start = try? #require(guide.flight?.start)
        let half = (start ?? now).addingTimeInterval(5)
        #expect(abs(guide.page(at: half) - 0.5) < 0.0001)
        let middle = guide.place(at: half)
        #expect(abs((middle?.rect.midX ?? 0) - 150) < 0.01 && abs((middle?.rect.width ?? 0) - 80) < 0.01)
        // It is there, and no further, however long after.
        let after = (start ?? now).addingTimeInterval(60)
        #expect(guide.page(at: after) == 1)
        #expect(guide.place(at: after)?.rect == guide.anchors[.hosting])
        // Standing up all the way: it is only on the last screen that it lies flat.
        #expect(guide.place(at: after)?.flat == 0)

        // Sent on before it has arrived, it sets off from where it has got to.
        guide.go(to: .connect, over: 10)
        #expect(abs((guide.flight?.fromPage ?? -1) - guide.page(at: .now)) < 0.01)
        let end = (guide.flight?.start ?? now).addingTimeInterval(60)
        #expect(guide.place(at: end)?.flat == 1 && guide.page(at: end) == 2)
    }

    /// It stops for breath where a sentence does, and for less at a comma.
    @Test func theFaceStopsWhereTheWordsDo() {
        #expect(OnboardingGuide.pause(after: ".") > OnboardingGuide.pause(after: ","))
        #expect(OnboardingGuide.pause(after: ",") > OnboardingGuide.pause(after: "a"))
        #expect(OnboardingGuide.pause(after: "a") == OnboardingGuide.pause(after: " "))
        // What it says is what was asked for, down to the provider.
        #expect(OnboardingGuide.speech.contains("OIDC") && !OnboardingGuide.speech.contains("OAuth"))
    }

    /// The app finds its models' parts by the names they have in Blender. An export that
    /// renamed one would leave it unpainted and unable to be picked up, and say nothing.
    @MainActor @Test func theModelsCarryThePartsTheAppLooksFor() async {
        let logo = await Stage.load("SoloLogo")
        for name in ["Tile", "BarFront", "BarMiddle", "BarBack"] {
            #expect(logo?.findEntity(named: name) != nil, "SoloLogo has no \(name)")
        }
        let hosting = await Stage.load("Hosting")
        for name in ["Server", "ServerLamps", "Cloud", "Laptop", "LaptopType"] {
            #expect(hosting?.findEntity(named: name) != nil, "Hosting has no \(name)")
        }
    }

    /// As their file leaves them the logo's pieces are apart, each at a depth of its own:
    /// they are solid, and two that began inside one another would be thrown apart.
    @MainActor @Test func theLogosPiecesStartAtDepthsOfTheirOwn() async throws {
        let model = try #require(await SoloLogo.stage.load())
        let depths = ["Tile", "BarBack", "BarMiddle", "BarFront"].compactMap {
            model.findEntity(named: $0)?.components[SpringBack.self]?.home.z
        }
        #expect(depths.count == 4)
        // Back to front, and no two within a slab's thickness of each other.
        for (behind, before) in zip(depths, depths.dropFirst()) {
            #expect(before - behind > 0.07)
        }
    }
}

/// A frame for the glass pass to work on, and a way to read what it made of it.
@MainActor struct GlassFrame {
    typealias Pixel = (red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8)

    /// A camera whose screen is one unit across and one high at a distance of four.
    static let projection: simd_float4x4 = {
        var matrix = matrix_identity_float4x4
        matrix.columns.0.x = 8
        matrix.columns.1.y = 8
        // What a depth of nearly nothing is turned back into: a long way off.
        matrix.columns.2.z = 0
        matrix.columns.3.z = 1
        return matrix
    }()

    /// A piece of glass facing the camera, four units off, in the middle of its view.
    static func lens(half: SIMD2<Float>, top: SIMD4<Float>, bottom: SIMD4<Float>) -> LensShape {
        var viewFromLens = matrix_identity_float4x4
        viewFromLens.columns.3 = [0, 0, -4, 1]
        return LensShape(lensFromView: viewFromLens.inverse, viewFromLens: viewFromLens, shape: [half.x, half.y, 0.05, 1],
                         top: top, bottom: bottom, fade: [0, 1, half.y * 2, 0])
    }

    let device: any MTLDevice
    let queue: any MTLCommandQueue
    let pass: GlassPass
    let source: any MTLTexture
    let target: any MTLTexture
    let depth: any MTLTexture

    init?(width: Int, height: Int) {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue(), let pass = GlassPass(device: device) else { return nil }
        // Unbent numbers, so that what is written is what is read back.
        let colour = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        colour.usage = [.shaderRead, .shaderWrite]
        colour.storageMode = .shared
        let far = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .depth32Float, width: width, height: height, mipmapped: false)
        far.usage = [.shaderRead, .renderTarget]
        far.storageMode = .private
        guard let source = device.makeTexture(descriptor: colour), let target = device.makeTexture(descriptor: colour),
              let depth = device.makeTexture(descriptor: far) else { return nil }
        (self.device, self.queue, self.pass, self.source, self.target, self.depth) = (device, queue, pass, source, target, depth)
    }

    func fill(_ pixel: (Int, Int) -> Pixel) {
        var bytes = [UInt8](repeating: 0, count: source.width * source.height * 4)
        for y in 0..<source.height {
            for x in 0..<source.width {
                let value = pixel(x, y), at = (y * source.width + x) * 4
                (bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]) = (value.blue, value.green, value.red, value.alpha)
            }
        }
        source.replace(region: MTLRegionMake2D(0, 0, source.width, source.height), mipmapLevel: 0, withBytes: bytes, bytesPerRow: source.width * 4)
    }

    /// Runs the pass, and gives back a way to read a point of what it made.
    func run(lenses: [LensShape], glow: GlowSource?, times: Int = 1) -> ((Int, Int) -> Pixel)? {
        guard let buffer = queue.makeCommandBuffer() else { return nil }
        // Nothing solid anywhere: the depth of every point is that of a long way off.
        let clear = MTLRenderPassDescriptor()
        clear.depthAttachment.texture = depth
        clear.depthAttachment.loadAction = .clear
        clear.depthAttachment.storeAction = .store
        clear.depthAttachment.clearDepth = 0.0001
        buffer.makeRenderCommandEncoder(descriptor: clear)?.endEncoding()
        for _ in 0..<times {
            pass.encode(into: buffer, source: source, depth: depth, target: target, projection: Self.projection, lenses: lenses, glow: glow)
        }
        buffer.commit()
        buffer.waitUntilCompleted()
        guard buffer.status == .completed else { return nil }

        var bytes = [UInt8](repeating: 0, count: target.width * target.height * 4)
        target.getBytes(&bytes, bytesPerRow: target.width * 4, from: MTLRegionMake2D(0, 0, target.width, target.height), mipmapLevel: 0)
        let width = target.width
        return { x, y in
            let at = (y * width + x) * 4
            return (bytes[at + 2], bytes[at + 1], bytes[at], bytes[at + 3])
        }
    }
}

@Suite struct WidgetSummaryTests {
    private func source(_ serial: String, _ status: String, _ deadline: String? = nil) -> WorkSummary.Source {
        WorkSummary.Source(id: UUID(), serial: serial, name: serial, status: status, deadline: deadline)
    }

    @Test func aSummaryCountsColumnsAndListsOpenWorkSoonestFirst() {
        let summary = WorkSummary(tasks: [
            source("A-B-3", "Unstarted"), source("A-B-1", "InProgress", "2026-12-01"), source("A-B-2", "Unstarted", "2026-11-20"),
            source("A-B-4", "Finished", "2026-11-01"), source("A-B-10", "Unstarted"),
        ])
        #expect(summary.unstarted == 3 && summary.inProgress == 1 && summary.finished == 1)
        // Dated before undated, sooner first; undated in serial order, 3 before 10.
        #expect(summary.open.map(\.serial) == ["A-B-2", "A-B-1", "A-B-3", "A-B-10"])
        #expect(summary.open.map(\.started) == [false, true, false, false])
    }

    @Test func aSummaryIsReadFromTheAssignedTasksPayload() throws {
        let project = UUID(), board = UUID()
        let json = """
        {"projects":[{"id":"\(project)","name":"Demo","alias":"DMD"}],
         "boards":[{"id":"\(board)","projectId":"\(project)","name":"Work","alias":"PW26"}],
         "labels":[],"members":[],
         "tasks":[{"id":"\(UUID())","boardId":"\(board)","number":7,"name":"Ship it","status":"InProgress","deadlineAt":"2026-11-15"},
                  {"id":"\(UUID())","boardId":"\(UUID())","number":8,"name":"Orphan","status":"Unstarted","deadlineAt":null}]}
        """
        let summary = try WorkSummary(assignedTasks: Data(json.utf8))
        // A task whose board isn't in the payload has no serial to open it by, and is left out.
        #expect(summary.open.map(\.serial) == ["DMD-PW26-7"])
        #expect(summary.inProgress == 1 && summary.unstarted == 0)
    }

    @Test func daysLeftCountsWholeDaysInTheDevicesCalendar() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try #require(TimeZone(identifier: "America/Los_Angeles"))
        let today = try #require(calendar.date(from: DateComponents(year: 2026, month: 11, day: 13, hour: 23, minute: 30)))
        func item(_ deadline: String?) -> WorkSummary.Item {
            WorkSummary.Item(id: UUID(), serial: "A-B-1", name: "x", started: false, deadline: deadline)
        }
        #expect(item("2026-11-13").daysLeft(from: today, calendar: calendar) == 0)
        #expect(item("2026-11-14").daysLeft(from: today, calendar: calendar) == 1)
        #expect(item("2026-11-10").daysLeft(from: today, calendar: calendar) == -3)
        #expect(item(nil).daysLeft(from: today, calendar: calendar) == nil)
        #expect(item("soon").daysLeft(from: today, calendar: calendar) == nil)
    }

    @Test func aWidgetLinkNamesAPageAndNothingElse() {
        #expect(WidgetLink.parse(WidgetLink.yourTasks) == .yourTasks)
        #expect(WidgetLink.parse(WidgetLink.task("DMD-PW26-7")) == .task("DMD-PW26-7"))
        #expect(WidgetLink.parse(URL(string: "https://example.com/task/DMD-PW26-7")!) == nil)
        #expect(WidgetLink.parse(URL(string: "\(WidgetLink.scheme)://task/..%2F..%2Fadmin")!) == nil)
        #expect(WidgetLink.parse(URL(string: "\(WidgetLink.scheme)://admin")!) == nil)
    }
}

@Suite @MainActor struct DocDragTests {
    @Test func aDraggedNoteCarriesALinkToItself() {
        let id = UUID()
        let node = DocNode(id: id, isFolder: false, parentId: nil, name: "Bosses [draft]", version: 1)
        let link = DocDrag.link(to: node)
        #expect(link == "[Bosses \\[draft\\]](\(NoteAddress.url(id)))")
        // Parsed back, it is a link to that note with the name as its text.
        let spans = LiveMarkdown(link).spans(selection: nil)
        #expect(spans.contains { $0.kind == .destination(NoteAddress.url(id)) })
    }
}

// A big board is drawn a lane at a time, from tasks sorted out once.

@Suite struct BoardLayoutTests {
    private let board = UUID()

    private func task(_ name: String, epic: UUID? = nil, _ status: TaskState, rank: Double) -> TaskItem {
        TaskItem(id: UUID(), boardId: board, epicId: epic, number: Int(rank), name: name, labelId: nil, description: nil,
                 status: status, createdAt: Date(timeIntervalSince1970: 0), deadlineAt: nil, rank: rank, version: 1,
                 assigneeIds: [], subTasks: [], startAt: nil)
    }

    private func epic(_ name: String, group: UUID? = nil, rank: Double) -> Epic {
        Epic(id: UUID(), boardId: board, name: name, description: nil, rank: rank, version: 1, assigneeIds: [], groupId: group)
    }

    @Test func aZoneHoldsItsOwnTasksInRankOrder() {
        let lane = UUID()
        let index = BoardIndex([
            task("c", epic: lane, .unstarted, rank: 30),
            task("a", epic: lane, .unstarted, rank: 10),
            task("done", epic: lane, .finished, rank: 20),
            task("loose", .unstarted, rank: 5),
            task("b", epic: lane, .unstarted, rank: 20),
        ])
        #expect(index.tasks(epicId: lane, status: .unstarted).map(\.name) == ["a", "b", "c"])
        #expect(index.tasks(epicId: lane, status: .finished).map(\.name) == ["done"])
        #expect(index.tasks(epicId: lane, status: .inProgress).isEmpty)
        // No epic is a lane like any other.
        #expect(index.tasks(epicId: nil, status: .unstarted).map(\.name) == ["loose"])
        #expect(index.tasks(epicId: UUID(), status: .unstarted).isEmpty)
    }

    @Test func countsAreByLaneAndByColumn() {
        let lane = UUID()
        let index = BoardIndex([
            task("a", epic: lane, .unstarted, rank: 1), task("b", epic: lane, .finished, rank: 2),
            task("c", .finished, rank: 3),
        ])
        #expect(index.count(epicId: lane) == 2)
        #expect(index.count(epicId: nil) == 1)
        #expect(index.count(.finished) == 2)
        #expect(index.count(.unstarted) == 1)
        #expect(index.count(.inProgress) == 0)
        #expect(BoardIndex().count(.finished) == 0)
    }

    @Test func aGroupIsItsTitleAndThenEachOfItsEpicsAsARowOfItsOwn() {
        let group = EpicGroup(id: UUID(), boardId: board, name: "Milestone", rank: 1, version: 1)
        let first = epic("One", group: group.id, rank: 1), second = epic("Two", group: group.id, rank: 2)
        let third = epic("Three", group: group.id, rank: 3), loose = epic("Loose", rank: 9)
        let rows = BoardRow.rows([.group(group), .epic(loose)], epics: { _ in [first, second, third] }, collapsed: { _ in false })

        #expect(rows.map(\.id) == [group.id, first.id, second.id, third.id, loose.id])
        // The outline opens under the title, runs down the sides, and closes under the last epic.
        guard case .group(_, .top) = rows[0], case .lane(_, .middle?) = rows[1], case .lane(_, .middle?) = rows[2],
              case .lane(_, .bottom?) = rows[3], case .lane(_, nil) = rows[4] else {
            Issue.record("The group's outline is not in the pieces expected.")
            return
        }
    }

    @Test func aFoldedOrEmptyGroupIsItsTitleAlone() {
        let group = EpicGroup(id: UUID(), boardId: board, name: "Milestone", rank: 1, version: 1)
        let inside = epic("One", group: group.id, rank: 1)

        let folded = BoardRow.rows([.group(group)], epics: { _ in [inside] }, collapsed: { _ in true })
        let empty = BoardRow.rows([.group(group)], epics: { _ in [] }, collapsed: { _ in false })
        for rows in [folded, empty] {
            #expect(rows.count == 1)
            guard case .group(_, .whole) = rows[0] else {
                Issue.record("A group with nothing under it should close its own outline.")
                return
            }
        }
    }

    @Test func aGroupOfOneEpicClosesUnderIt() {
        let group = EpicGroup(id: UUID(), boardId: board, name: "Milestone", rank: 1, version: 1)
        let only = epic("One", group: group.id, rank: 1)
        let rows = BoardRow.rows([.group(group)], epics: { _ in [only] }, collapsed: { _ in false })
        guard rows.count == 2, case .group(_, .top) = rows[0], case .lane(_, .bottom?) = rows[1] else {
            Issue.record("One epic is both the first and the last of its group.")
            return
        }
    }
}

/// How a Finished column is held to the height of the work beside it, as the
/// web holds it (KanbanZone.razor, js/finished-column.js).
@Suite struct FinishedColumnTests {
    @Test func it_may_stand_as_tall_as_the_work_beside_it_and_no_taller() {
        #expect(!FinishedColumn.overflows(cards: 600, beside: 600))
        #expect(FinishedColumn.overflows(cards: 602, beside: 600))
    }

    @Test func beside_little_or_nothing_it_still_has_the_webs_fixed_room() {
        #expect(!FinishedColumn.overflows(cards: 340, beside: 0))
        #expect(!FinishedColumn.overflows(cards: 340, beside: 120))
        #expect(FinishedColumn.overflows(cards: 342, beside: 120))
    }

    @Test func one_that_overflows_gives_its_button_room_out_of_its_own() {
        #expect(FinishedColumn.room(beside: 600) == 600 - FinishedColumn.toggle)
        // But is never given less than the fixed room.
        #expect(FinishedColumn.room(beside: 120) == FinishedColumn.floor)
    }

    @Test func a_cards_height_is_guessed_from_the_columns_beside_it() {
        // Three cards of 70 with gaps of 6 between them, in a zone padded by 6 all round; and an empty zone.
        let guess = FinishedColumn.guess(from: [(height: 234, cards: 3), (height: 48, cards: 0)], extra: 12, spacing: 6)
        #expect(guess == 76)
        // With nothing to go by, a card is taken to be of the usual size.
        #expect(FinishedColumn.guess(from: [(height: 48, cards: 0)], extra: 12, spacing: 6) == FinishedColumn.guess)
        #expect(FinishedColumn.guess(from: [], extra: 12, spacing: 6) == FinishedColumn.guess)
    }
}

/// Whether the system has a symbol of this name, on whichever kit the tests are running.
private func symbolExists(_ name: String) -> Bool {
    #if canImport(UIKit)
    UIImage(systemName: name) != nil
    #else
    NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil
    #endif
}

@Suite struct DeviceNotificationAddressTests {
    @Test func anInstanceIsOneInstanceWithOrWithoutItsSlash() throws {
        let kept = try #require(URL(string: "https://solo.example.com"))
        let client = try #require(URL(string: "https://solo.example.com/"))
        #expect(DeviceNotifications.address(of: kept) == DeviceNotifications.address(of: client))
        let under = try #require(URL(string: "https://example.com/solo/"))
        #expect(DeviceNotifications.address(of: under) == "https://example.com/solo")
        #expect(DeviceNotifications.address(of: kept) != DeviceNotifications.address(of: under))
    }
}

/// The game in the Mac's About window is the desktop shell's, rule for rule: each of these
/// is a game played headless on that one (`Solo.Desktop/about/about.js`, with the same deal
/// and the same keys), and what it came to.
@Suite struct FallingBlocksTests {
    private static let letters: [(Character, FallingBlocks.Kind)] =
        [("I", .i), ("O", .o), ("T", .t), ("S", .s), ("Z", .z), ("J", .j), ("L", .l)]
    private static let keys: [Character: FallingBlocks.Key] = [
        "L": .left, "R": .right, "D": .down, "U": .up, "S": .space,
        "Z": .turnCounterclockwise, "X": .turnClockwise, "P": .pause, "E": .enter,
    ]

    private static func letter(_ kind: FallingBlocks.Kind?) -> Character {
        letters.first { $0.1 == kind }?.0 ?? "."
    }

    /// The game as the desktop's is written down, so the two can be compared as text.
    private static func snapshot(_ game: FallingBlocks) -> String {
        let board = game.board.map { String($0.map(letter)) }.joined(separator: "/")
        let state = switch game.phase {
        case .ready: "ready"
        case .playing: "playing"
        case .paused: "paused"
        case .over: "over"
        }
        return #"{"board":"\#(board)","piece":"\#(letter(game.piece.kind)),\#(game.piece.x),\#(game.piece.y)","#
            + #""next":"\#(letter(game.next))","score":\#(game.score),"lines":\#(game.lines),"state":"\#(state)"}"#
    }

    /// A game dealt `deal` over and over, with one key a millisecond. The first only starts it.
    private static func play(_ deal: String, _ pressed: String) -> FallingBlocks {
        let order = deal.compactMap { letter in letters.first { $0.0 == letter }?.1 }
        var game = FallingBlocks(deal: { order.reversed() })
        var now = 0.0
        for key in pressed {
            now += 0.001
            game.press(keys[key] ?? .other, at: now)
        }
        return game
    }

    private static let empty = Array(repeating: "..........", count: 16).joined(separator: "/")

    @Test func aFullRowGoesAndWhatIsAboveItComesDown() {
        let game = Self.play("IIOT", "SLLLURURRRRU")
        let rows = Array(repeating: "..........", count: 15) + ["........OO"]
        #expect(Self.snapshot(game) == #"{"board":"\#(rows.joined(separator: "/"))","piece":"T,3,0","next":"I","score":188,"lines":1,"state":"playing"}"#)
        // What flashes is what is left of the piece, where it now is.
        #expect(Set(game.flash?.cells ?? []) == [.init(x: 8, y: 15), .init(x: 9, y: 15)])
        #expect(game.flashStrength(at: 0.012) == 1)
        #expect(game.flashStrength(at: 0.112) < 0.6)
        #expect(game.flashStrength(at: 0.3) == 0)
    }

    @Test func rowsClearedTogetherScoreMore() {
        #expect(Self.snapshot(Self.play("OOOOOT", "SLLLLULLURRURRRRUU"))
            == #"{"board":"\#(Self.empty)","piece":"T,3,0","next":"O","score":440,"lines":2,"state":"playing"}"#)
        #expect(Self.snapshot(Self.play("OOOOOOOOIIT", "SLLLLULLLLULLULLUUURRURRUXRRRUXRRRRU"))
            == #"{"board":"\#(Self.empty)","piece":"T,3,0","next":"O","score":1060,"lines":4,"state":"playing"}"#)
    }

    @Test func aTurnThatDoesNotFitIsNudgedOffTheWall() {
        #expect(Self.snapshot(Self.play("IT", "SXRRRRRRZ"))
            == #"{"board":"\#(Self.empty)","piece":"I,6,-1","next":"T","score":0,"lines":0,"state":"playing"}"#)
    }

    @Test func theShadowIsWhereThePieceWouldLand() {
        var game = Self.play("OT", "S")
        #expect(game.landingRow == 14)
        // The next is a T, whose box reaches a row below its stem: it comes to rest on the O.
        game.press(.up, at: 0.01)
        #expect(game.landingRow == 12)
    }

    /// Twelve thousand frames of keys picked by a Park–Miller sequence: gravity, the lock
    /// delay, a held ↓, pausing, topping out and starting again, at the desktop's timings.
    @Test func randomPlayComesToTheBoardsTheDesktopGameDid() {
        var game = FallingBlocks(deal: { [.i, .o, .t, .s, .z, .j, .l] })
        var seed = 20_261_118
        var now = 0.0
        var seen: [[String]] = []
        for step in 1...12000 {
            seed = seed * 48271 % 2_147_483_647
            switch seed % 20 {
            case 0...2: game.press(.left, at: now)
            case 3...5: game.press(.right, at: now)
            case 6: game.press(.down, at: now)
            case 7: game.release(.down)
            case 8: game.press(.up, at: now)
            case 9, 10: game.press(.space, at: now)
            case 11: game.press(.turnCounterclockwise, at: now)
            case 12: game.press(.turnClockwise, at: now)
            case 13: game.press(.pause, at: now)
            case 14: game.press(.enter, at: now)
            default: break
            }
            now = Double(step) * 0.0167
            game.tick(at: now)
            if step.isMultiple(of: 1500) {
                // FNV-1a over the snapshot, as the desktop's was hashed.
                let hash = Self.snapshot(game).unicodeScalars.reduce(UInt32(0x811c_9dc5)) { ($0 ^ $1.value) &* 0x0100_0193 }
                seen.append(["\(step)", "\(game.score)", "\(game.lines)", "\(game.phase)", "\(hash)"])
            }
        }
        #expect(seen == [
            ["1500", "186", "0", "playing", "1980232078"],
            ["3000", "72", "0", "playing", "1051498232"],
            ["4500", "142", "0", "playing", "3477100959"],
            ["6000", "200", "0", "over", "2104179125"],
            ["7500", "81", "0", "playing", "207359871"],
            ["9000", "102", "0", "playing", "2097782460"],
            ["10500", "40", "0", "paused", "1883419320"],
            ["12000", "189", "0", "paused", "2222008863"],
        ])
    }
}
