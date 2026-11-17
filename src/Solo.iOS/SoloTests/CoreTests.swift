import Foundation
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
