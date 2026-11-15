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

@Suite @MainActor struct CardDragWatchTests {
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
