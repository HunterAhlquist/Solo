import SwiftUI

/// A month to pick a day from, at the size an iPad's is.
///
/// A Mac's own graphical date picker is the calendar of an old preferences pane: a hundred
/// and forty points across whatever room it is given, with days a pointer has to be aimed
/// at. An iPad's is the one this app is designed around, so a Mac gets one drawn like it:
/// the month and year, a step either way, and a grid of days a finger's width apart. The
/// iPad and iPhone keep the system's.
struct CalendarPicker: View {
    @Environment(\.theme) private var theme
    @Binding var selection: Date
    /// The last day that may be picked, where there is one.
    var latest: Date?
    var calendar = Calendar.current

    /// The month on show: the first moment of its first day.
    @State private var month: Date

    init(selection: Binding<Date>, latest: Date? = nil, calendar: Calendar = .current) {
        _selection = selection
        self.latest = latest
        self.calendar = calendar
        _month = State(initialValue: Self.start(of: selection.wrappedValue, in: calendar))
    }

    private static let cell: CGFloat = 38

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 4) {
                Text(month.formatted(.dateTime.month(.wide).year()))
                    .font(.headline)
                    .foregroundStyle(theme.textPrimary)
                Spacer()
                step(-1, symbol: "chevron.left", label: "Previous month")
                step(1, symbol: "chevron.right", label: "Next month")
                    .disabled(!mayShow(monthAfter))
            }
            .padding(.leading, 8)

            Grid(horizontalSpacing: 0, verticalSpacing: 2) {
                GridRow {
                    ForEach(Array(weekdaySymbols.enumerated()), id: \.offset) { _, symbol in
                        Text(symbol)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(theme.textSecondary)
                            .frame(width: Self.cell, height: 22)
                    }
                }
                ForEach(Array(weeks.enumerated()), id: \.offset) { _, week in
                    GridRow {
                        ForEach(Array(week.enumerated()), id: \.offset) { _, day in
                            if let day {
                                dayButton(day)
                            } else {
                                Color.clear.frame(width: Self.cell, height: Self.cell)
                            }
                        }
                    }
                }
            }
        }
        .onChange(of: selection) { _, date in
            // Set from outside — a field opened on another day — the month follows it.
            let shown = Self.start(of: date, in: calendar)
            if shown != month { month = shown }
        }
    }

    private func step(_ months: Int, symbol: String, label: String) -> some View {
        Button {
            if let next = calendar.date(byAdding: .month, value: months, to: month) { month = next }
        } label: {
            Image(systemName: symbol)
                .font(.body.weight(.semibold))
                .frame(width: 30, height: 30)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .foregroundStyle(theme.primary)
        .accessibilityLabel(label)
    }

    private func dayButton(_ day: Date) -> some View {
        let chosen = calendar.isDate(day, inSameDayAs: selection)
        let today = calendar.isDateInToday(day)
        let allowed = latest.map { day <= $0 } ?? true
        return Button {
            selection = day
        } label: {
            Text("\(calendar.component(.day, from: day))")
                .font(.body.weight(chosen || today ? .semibold : .regular).monospacedDigit())
                // The accent tints: a wash under the chosen day, and its colour on today's.
                .foregroundStyle(chosen || today ? theme.primary : theme.textPrimary)
                .frame(width: Self.cell, height: Self.cell)
                .background {
                    if chosen { Circle().fill(theme.accentWash).padding(2) }
                }
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(!allowed)
        .opacity(allowed ? 1 : 0.3)
        .accessibilityLabel(day.formatted(date: .complete, time: .omitted))
        .accessibilityAddTraits(chosen ? .isSelected : [])
    }

    // MARK: The month's shape

    private static func start(of date: Date, in calendar: Calendar) -> Date {
        calendar.dateInterval(of: .month, for: date)?.start ?? calendar.startOfDay(for: date)
    }

    private var monthAfter: Date { calendar.date(byAdding: .month, value: 1, to: month) ?? month }

    /// Whether any of a month may be picked, so the way forward stops at the last that can.
    private func mayShow(_ month: Date) -> Bool {
        latest.map { month <= $0 } ?? true
    }

    /// The days' initials, from the day the calendar's week starts on.
    private var weekdaySymbols: [String] {
        let symbols = calendar.veryShortStandaloneWeekdaySymbols
        let first = calendar.firstWeekday - 1
        return Array(symbols[first...] + symbols[..<first])
    }

    /// The month as rows of seven, nil where a row's day belongs to the month before or after.
    private var weeks: [[Date?]] {
        Self.weeks(of: month, in: calendar)
    }

    nonisolated static func weeks(of month: Date, in calendar: Calendar) -> [[Date?]] {
        guard let days = calendar.range(of: .day, in: .month, for: month) else { return [] }
        let lead = (calendar.component(.weekday, from: month) - calendar.firstWeekday + 7) % 7
        var cells: [Date?] = Array(repeating: nil, count: lead)
        cells += days.map { calendar.date(byAdding: .day, value: $0 - 1, to: month) }
        while cells.count % 7 != 0 { cells.append(nil) }
        return stride(from: 0, to: cells.count, by: 7).map { Array(cells[$0..<$0 + 7]) }
    }
}
