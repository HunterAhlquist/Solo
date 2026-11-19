import Foundation

/// The rules of the game behind the About window's icon: a tiny falling-blocks game, the one
/// the desktop shell hides in its About window (`Solo.Desktop/about/about.js`), rule for rule.
/// No hold, no sound.
///
/// It keeps no clock of its own: whoever shows it says what time it is with each key and each
/// frame, in seconds from wherever it likes. That is what lets the rules be played through in
/// a test, against boards the desktop's game was played to.
nonisolated struct FallingBlocks {
    static let columns = 10
    static let rows = 16

    enum Kind: CaseIterable, Sendable {
        case i, o, t, s, z, j, l

        /// The piece in its box, as it appears: the box is what it turns in.
        var cells: [[Bool]] {
            switch self {
            case .i: grid("....", "####", "....", "....")
            case .o: grid("##", "##")
            case .t: grid(".#.", "###", "...")
            case .s: grid(".##", "##.", "...")
            case .z: grid("##.", ".##", "...")
            case .j: grid("#..", "###", "...")
            case .l: grid("..#", "###", "...")
            }
        }

        private func grid(_ rows: String...) -> [[Bool]] {
            rows.map { $0.map { $0 == "#" } }
        }
    }

    /// Whether the icon or the game is showing is the window's to say; these are the game's.
    enum Phase: Sendable { case ready, playing, paused, over }

    /// The keys the game knows, and `other` for the rest: any key at all starts it.
    enum Key: Sendable { case left, right, down, up, space, enter, turnClockwise, turnCounterclockwise, pause, other }

    struct Cell: Hashable, Sendable {
        var x: Int
        var y: Int
    }

    struct Piece: Sendable {
        var kind: Kind
        var cells: [[Bool]]
        var x: Int
        var y: Int

        /// Where its cells are on the board. Those above it have a negative row.
        var filled: [Cell] { Self.filled(cells, x: x, y: y) }

        static func filled(_ cells: [[Bool]], x: Int = 0, y: Int = 0) -> [Cell] {
            cells.enumerated().flatMap { row, line in
                line.enumerated().compactMap { column, on in on ? Cell(x: x + column, y: y + row) : nil }
            }
        }
    }

    /// The cells of the piece that last locked, where they ended up, and when.
    struct Flash: Sendable {
        var cells: [Cell]
        var at: TimeInterval
    }

    // Holding ↓ drops a row this often, after a short wait so a tap is one row. The game
    // keeps its own repeat because the system's waits half a second and then runs at
    // whatever rate the Mac is set to.
    private static let softDrop: TimeInterval = 0.035
    private static let softDropDelay: TimeInterval = 0.110
    /// A landed piece locks after this many gravity ticks, however it got there — time to
    /// slide it or turn it. A hard drop locks at once, and so does a fresh ↓ press on a piece
    /// that has already landed.
    private static let lockTicks = 1.0
    /// How long a piece that has just locked flashes.
    static let flashTime: TimeInterval = 0.2
    private static let retryDelay: TimeInterval = 0.6
    /// Tried in order after a turn that doesn't fit where it is.
    private static let kicks = [(0, 0), (-1, 0), (1, 0), (0, -1), (-2, 0), (2, 0)]
    private static let linePoints = [0, 100, 300, 500, 800]

    private(set) var board: [[Kind?]] = []
    private(set) var piece = Piece(kind: .o, cells: [], x: 0, y: 0)
    private(set) var next = Kind.o
    private(set) var score = 0
    private(set) var lines = 0
    private(set) var phase = Phase.ready
    private(set) var flash: Flash?

    /// Seven pieces in the order they are to come, dealt from the end.
    private let deal: () -> [Kind]
    private var bag: [Kind] = []
    private var dropTimer: TimeInterval = 0
    private var softDropping = false
    private var softTimer: TimeInterval = 0
    private var lockTimer: TimeInterval = 0
    private var overAt: TimeInterval = 0
    private var lastTick: TimeInterval = 0

    /// A 7-bag unless told otherwise: every piece once per seven, in a shuffled order.
    init(deal: @escaping () -> [Kind] = { Kind.allCases.shuffled() }) {
        self.deal = deal
        reset()
    }

    var level: Int { lines / 10 }

    private var dropInterval: TimeInterval { max(0.07, 0.8 * pow(0.85, Double(level))) }

    /// The row the piece would come to rest on, for its shadow.
    var landingRow: Int {
        var y = piece.y
        while !collides(piece.cells, piece.x, y + 1) { y += 1 }
        return y
    }

    /// How brightly the last locked piece still flashes: one as it locks, none once it is over.
    func flashStrength(at now: TimeInterval) -> Double {
        guard let flash else { return 0 }
        return min(1, max(0, 1 - (now - flash.at) / Self.flashTime))
    }

    // MARK: Keys and time

    /// A key going down. Any key starts or resumes; once playing they move the piece.
    mutating func press(_ key: Key, repeated: Bool = false, at now: TimeInterval) {
        switch phase {
        case .ready, .paused:
            set(.playing, at: now)
        case .over:
            // Whoever just topped out was probably mid-press — Space turns — so it has to be
            // a fresh press, a moment later, to retry.
            if key == .space || key == .enter, !repeated, now - overAt > Self.retryDelay {
                reset()
                set(.playing, at: now)
            }
        case .playing:
            switch key {
            case .left: tryMove(-1, 0)
            case .right: tryMove(1, 0)
            case .down:
                // The keyboard's repeats are ignored: time drops it while the key is held.
                // Pressing again once landed means "here", so it skips the wait.
                guard !repeated else { break }
                if grounded {
                    lock(at: now)
                } else if dropOne() {
                    softDropping = true
                    softTimer = -Self.softDropDelay
                }
            case .up: hardDrop(at: now)
            case .space, .turnClockwise: turn(clockwise: true)
            case .turnCounterclockwise: turn(clockwise: false)
            case .pause: set(.paused, at: now)
            case .enter, .other: break
            }
        }
    }

    /// ↓ coming up: the piece stops being hurried.
    mutating func release(_ key: Key) {
        if key == .down { softDropping = false }
    }

    /// Nobody is playing a window they've clicked away from.
    mutating func pause(at now: TimeInterval) {
        if phase == .playing { set(.paused, at: now) }
    }

    /// A frame: what the time since the last one does to the piece.
    mutating func tick(at now: TimeInterval) {
        guard phase == .playing else { return }
        // Capped, so a stalled frame never drops a piece several rows at once.
        let elapsed = min(max(0, now - lastTick), 0.25)
        lastTick = now

        if softDropping {
            softTimer += elapsed
            while softDropping, softTimer >= Self.softDrop {
                softTimer -= Self.softDrop
                dropOne()
            }
        }

        dropTimer += elapsed
        if dropTimer >= dropInterval {
            dropTimer = 0
            tryMove(0, 1)
        }

        // Counts only while resting: a piece slid off a ledge falls again and gets the full
        // delay where it next lands.
        if grounded {
            lockTimer += elapsed
            if lockTimer >= Self.lockTicks * dropInterval { lock(at: now) }
        } else {
            lockTimer = 0
        }
    }

    // MARK: Rules

    private mutating func reset() {
        board = Array(repeating: Array(repeating: nil, count: Self.columns), count: Self.rows)
        bag = []
        next = draw()
        score = 0
        lines = 0
        dropTimer = 0
        flash = nil
        spawn()
    }

    private mutating func set(_ phase: Phase, at now: TimeInterval) {
        self.phase = phase
        softDropping = false
        if phase == .over { overAt = now }
        if phase == .playing { lastTick = now }
    }

    private mutating func draw() -> Kind {
        if bag.isEmpty { bag = deal() }
        return bag.popLast() ?? .o
    }

    private mutating func spawn() {
        // A held ↓ stops with the piece it was dropping; the next needs a new press.
        softDropping = false
        lockTimer = 0
        let kind = next
        next = draw()
        let cells = kind.cells
        // The I's cells sit on the second row of its box; up one puts them on top.
        piece = Piece(kind: kind, cells: cells, x: (Self.columns - cells.count) / 2, y: kind == .i ? -1 : 0)
        // A stack in the way only pushes the piece up into the open air above the board,
        // where it can still be moved; the game is lost when a piece locks with any of it up
        // there (`lock`), not because it had nowhere to appear.
        while collides(cells, piece.x, piece.y) { piece.y -= 1 }
    }

    /// Rows above the board are open air, so a piece can turn at the top.
    private func collides(_ cells: [[Bool]], _ x: Int, _ y: Int) -> Bool {
        Piece.filled(cells, x: x, y: y).contains { cell in
            cell.x < 0 || cell.x >= Self.columns || cell.y >= Self.rows || (cell.y >= 0 && board[cell.y][cell.x] != nil)
        }
    }

    private var grounded: Bool { collides(piece.cells, piece.x, piece.y + 1) }

    @discardableResult
    private mutating func tryMove(_ dx: Int, _ dy: Int) -> Bool {
        guard !collides(piece.cells, piece.x + dx, piece.y + dy) else { return false }
        piece.x += dx
        piece.y += dy
        return true
    }

    private mutating func turn(clockwise: Bool) {
        let n = piece.cells.count
        let turned = (0..<n).map { y in
            (0..<n).map { x in clockwise ? piece.cells[n - 1 - x][y] : piece.cells[x][n - 1 - y] }
        }
        for (dx, dy) in Self.kicks where !collides(turned, piece.x + dx, piece.y + dy) {
            piece.cells = turned
            piece.x += dx
            piece.y += dy
            return
        }
    }

    /// A row down by the player's hand, which is a point. False once the piece is resting on
    /// something; the lock delay takes it from there, held ↓ or not.
    @discardableResult
    private mutating func dropOne() -> Bool {
        guard tryMove(0, 1) else { return false }
        score += 1
        dropTimer = 0
        return true
    }

    private mutating func hardDrop(at now: TimeInterval) {
        var fell = 0
        while tryMove(0, 1) { fell += 1 }
        score += fell * 2
        lock(at: now)
    }

    private mutating func lock(at now: TimeInterval) {
        let placed = piece.filled
        // Landed partly above the board: topped out.
        guard placed.allSatisfy({ $0.y >= 0 }) else {
            set(.over, at: now)
            return
        }
        for cell in placed { board[cell.y][cell.x] = piece.kind }

        let full = board.indices.filter { y in board[y].allSatisfy { $0 != nil } }
        // Where the piece's cells are once the full rows are gone: a cell in a cleared row is
        // gone with it, and the rest move down one per cleared row beneath them.
        flash = Flash(
            cells: placed.filter { !full.contains($0.y) }
                .map { cell in Cell(x: cell.x, y: cell.y + full.count(where: { $0 > cell.y })) },
            at: now)

        if !full.isEmpty {
            score += Self.linePoints[full.count] * (level + 1)
            lines += full.count
            let kept = board.indices.filter { !full.contains($0) }.map { board[$0] }
            board = Array(repeating: Array(repeating: nil, count: Self.columns), count: full.count) + kept
        }

        dropTimer = 0
        spawn()
    }
}
