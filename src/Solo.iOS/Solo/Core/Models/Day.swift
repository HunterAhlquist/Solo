import Foundation

/// A calendar day with no time and no zone: .NET's `DateOnly`, which the API
/// writes as `yyyy-MM-dd`. Deadlines are days, not instants, so they must never
/// pass through a `Date` on the way in or out, or a time zone moves them.
nonisolated struct Day: Hashable, Comparable, Sendable, Codable, CustomStringConvertible {
    var year: Int
    var month: Int
    var day: Int

    init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    init?(_ text: String) {
        let parts = text.split(separator: "-")
        guard parts.count == 3, let y = Int(parts[0]), let m = Int(parts[1]), let d = Int(parts[2].prefix(2)),
              (1...12).contains(m), (1...31).contains(d) else { return nil }
        self.init(year: y, month: m, day: d)
    }

    /// The day `date` falls on in the device's own calendar.
    init(_ date: Date, calendar: Calendar = .current) {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        self.init(year: parts.year ?? 1970, month: parts.month ?? 1, day: parts.day ?? 1)
    }

    static var today: Day { Day(Date()) }

    init(from decoder: Decoder) throws {
        let text = try decoder.singleValueContainer().decode(String.self)
        guard let day = Day(text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Not a day: \(text)"))
        }
        self = day
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(description)
    }

    var description: String {
        String(format: "%04d-%02d-%02d", year, month, day)
    }

    static func < (a: Day, b: Day) -> Bool {
        (a.year, a.month, a.day) < (b.year, b.month, b.day)
    }

    /// Local midnight at the start of this day.
    func date(calendar: Calendar = .current) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day)) ?? .distantPast
    }

    /// Days since a fixed epoch, so two days subtract without a calendar.
    var dayNumber: Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .gmt
        let date = calendar.date(from: DateComponents(year: year, month: month, day: day)) ?? .distantPast
        return Int((date.timeIntervalSince1970 / 86_400).rounded(.down))
    }

    init(dayNumber: Int) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .gmt
        let date = Date(timeIntervalSince1970: TimeInterval(dayNumber) * 86_400)
        self.init(date, calendar: calendar)
    }

    func adding(days: Int) -> Day { Day(dayNumber: dayNumber + days) }

    func days(until other: Day) -> Int { other.dayNumber - dayNumber }

    /// 1 = Sunday … 7 = Saturday.
    var weekday: Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .gmt
        return calendar.component(.weekday, from: Date(timeIntervalSince1970: TimeInterval(dayNumber) * 86_400))
    }

    var isWeekend: Bool { weekday == 1 || weekday == 7 }

    /// "13 Nov 2026", the form the web app writes a deadline in.
    var formatted: String {
        date().formatted(.dateTime.day().month(.abbreviated).year())
    }

    var shortFormatted: String {
        date().formatted(.dateTime.day().month(.abbreviated))
    }
}
