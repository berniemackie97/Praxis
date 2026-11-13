import AppKit
import SwiftUI

/// Praxis design tokens. Every workspace uses these instead of ad-hoc numbers so spacing,
/// type and surfaces stay consistent and native in light and dark mode.
enum Spacing {
    static let xxs: CGFloat = 2
    static let xs: CGFloat = 4
    static let s: CGFloat = 8
    static let m: CGFloat = 12
    static let l: CGFloat = 16
    static let xl: CGFloat = 24
    static let xxl: CGFloat = 32
}

enum Radius {
    static let s: CGFloat = 6
    static let m: CGFloat = 10
    static let l: CGFloat = 14
}

enum Surface {
    /// Cards and grouped content on the window background.
    static let card = Color(nsColor: .controlBackgroundColor)
    /// Subtle fills for chips, wells and secondary rows.
    static let well = Color.primary.opacity(0.045)
    static let stroke = Color(nsColor: .separatorColor)
}

extension Font {
    static let pageTitle = Font.title2.weight(.semibold)
    static let sectionTitle = Font.headline
    static let rowTitle = Font.body.weight(.medium)
    static let meta = Font.caption
}

/// Semantic tones for status, banners and badges.
enum Tone {
    case neutral, accent, positive, warning, critical

    var color: Color {
        switch self {
        case .neutral: .secondary
        case .accent: .accentColor
        case .positive: .green
        case .warning: .orange
        case .critical: .red
        }
    }
}

/// The title row of a workspace: title, optional subtitle, trailing actions.
struct PageHeader<Actions: View>: View {
    let title: String
    var subtitle: String?
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.m) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(.pageTitle)
                if let subtitle {
                    Text(subtitle).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Spacing.l)
            HStack(spacing: Spacing.s) { actions }
        }
    }
}

extension PageHeader where Actions == EmptyView {
    init(title: String, subtitle: String? = nil) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}

/// A grouped surface with an optional titled header.
struct Panel<Content: View, Accessory: View>: View {
    var title: String?
    var systemImage: String?
    var caption: String?
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            if title != nil || caption != nil {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: Spacing.xxs) {
                        if let title {
                            if let systemImage {
                                Label(title, systemImage: systemImage).font(.sectionTitle)
                            } else {
                                Text(title).font(.sectionTitle)
                            }
                        }
                        if let caption {
                            Text(caption).font(.meta).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    Spacer()
                    accessory
                }
            }
            content
        }
        .padding(Spacing.l)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous).strokeBorder(Surface.stroke.opacity(0.6)))
    }
}

extension Panel where Accessory == EmptyView {
    init(title: String? = nil, systemImage: String? = nil, caption: String? = nil, @ViewBuilder content: () -> Content) {
        self.init(title: title, systemImage: systemImage, caption: caption, accessory: { EmptyView() }, content: content)
    }
}

/// Centered guidance shown when a list or pane has nothing to show.
struct EmptyState: View {
    let systemImage: String
    let title: String
    var message: String?
    var actionTitle: String?
    var action: (() -> Void)?

    var body: some View {
        VStack(spacing: Spacing.m) {
            Image(systemName: systemImage)
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(.tertiary)
            Text(title).font(.title3.weight(.semibold))
            if let message {
                Text(message)
                    .font(.callout).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 380)
            }
            if let actionTitle, let action {
                Button(actionTitle, action: action).buttonStyle(.borderedProminent).controlSize(.large)
            }
        }
        .padding(Spacing.xxl)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// A compact capsule describing state ("Watching", "3 queued", "Remote").
struct StatusPill: View {
    let text: String
    var tone: Tone = .neutral
    var systemImage: String?

    var body: some View {
        HStack(spacing: Spacing.xs) {
            if let systemImage {
                Image(systemName: systemImage).imageScale(.small)
            } else {
                Circle().fill(tone.color).frame(width: 6, height: 6)
            }
            Text(text).lineLimit(1)
        }
        .font(.caption.weight(.medium))
        .padding(.horizontal, Spacing.s)
        .padding(.vertical, Spacing.xxs + 1)
        .foregroundStyle(tone == .neutral ? Color.secondary : tone.color)
        .background(tone.color.opacity(0.12), in: Capsule())
    }
}

/// The latest error or confirmation for a workspace, shown as a dismissible inline banner.
struct FeedbackLine: View {
    let error: String?
    let notice: String?

    var body: some View {
        if let error {
            Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
        } else if let notice {
            Banner(text: notice, tone: .accent, systemImage: "checkmark.circle.fill")
        }
    }
}

struct Banner: View {
    let text: String
    var tone: Tone = .accent
    var systemImage = "info.circle.fill"

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Image(systemName: systemImage).foregroundStyle(tone.color)
            Text(text).font(.callout).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, Spacing.m)
        .padding(.vertical, Spacing.s)
        .background(tone.color.opacity(0.10), in: RoundedRectangle(cornerRadius: Radius.m, style: .continuous))
        .transition(.opacity.combined(with: .move(edge: .bottom)))
    }
}

/// A two-line list row with a leading symbol, title, subtitle and optional trailing status.
struct ItemRow<Trailing: View>: View {
    let systemImage: String
    let title: String
    var subtitle: String?
    var tint: Color = .secondary
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: Spacing.m) {
            Image(systemName: systemImage)
                .font(.system(size: 15))
                .foregroundStyle(tint)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(.rowTitle).lineLimit(1).truncationMode(.middle)
                if let subtitle {
                    Text(subtitle).font(.meta).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: Spacing.s)
            trailing
        }
        .padding(.vertical, Spacing.xs)
        .contentShape(Rectangle())
    }
}

extension ItemRow where Trailing == EmptyView {
    init(systemImage: String, title: String, subtitle: String? = nil, tint: Color = .secondary) {
        self.init(systemImage: systemImage, title: title, subtitle: subtitle, tint: tint) { EmptyView() }
    }
}

/// A label/value pair for detail panes. Identifiers truncate in the middle; the full value stays
/// selectable and appears on hover.
struct KeyValueRow: View {
    let key: String
    let value: String
    var identifier = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.m) {
            Text(key).font(.callout).foregroundStyle(.secondary).frame(width: 96, alignment: .leading)
            Text(value)
                .font(identifier ? .system(.callout, design: .monospaced) : .callout)
                .lineLimit(identifier ? 1 : nil)
                .truncationMode(.middle)
                .textSelection(.enabled)
                .help(identifier ? value : "")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// One choice in a toolbar filter menu.
struct FilterOption<Value: Hashable>: Identifiable {
    let value: Value
    let title: String
    var systemImage: String?
    var id: Value { value }
}

/// A toolbar menu that names the current filter, with the first option (usually "All") set apart.
struct FilterMenu<Value: Hashable>: View {
    let help: String
    let options: [FilterOption<Value>]
    @Binding var selection: Value

    private var current: String { options.first { $0.value == selection }?.title ?? options.first?.title ?? "" }

    var body: some View {
        Menu {
            ForEach(Array(options.enumerated()), id: \.element.id) { index, option in
                Button {
                    selection = option.value
                } label: {
                    if option.value == selection {
                        Label(option.title, systemImage: "checkmark")
                    } else if let symbol = option.systemImage {
                        Label(option.title, systemImage: symbol)
                    } else {
                        Text(option.title)
                    }
                }
                if index == 0 && options.count > 1 { Divider() }
            }
        } label: {
            HStack(spacing: Spacing.xs) {
                Image(systemName: "line.3.horizontal.decrease.circle")
                Text(current).lineLimit(1)
            }
        }
        .fixedSize()
        .help(help)
        .accessibilityLabel("\(help): \(current)")
    }
}

/// A list-detail layout. The list column starts at its ideal width, can be dragged between its
/// bounds, and remembers the width per workspace; the detail takes the rest.
struct MasterDetail<List: View, Detail: View>: View {
    var listWidth: (min: CGFloat, ideal: CGFloat, max: CGFloat) = (260, 320, 440)
    var detailMinimum: CGFloat = 380
    @ViewBuilder var list: List
    @ViewBuilder var detail: Detail
    @State private var width: CGFloat?
    @State private var dragStart: CGFloat?

    var body: some View {
        GeometryReader { proxy in
            // When the window is narrow the list gives way first, down to a readable floor.
            let floor = min(listWidth.min, 220)
            let upper = max(floor, min(listWidth.max, proxy.size.width - detailMinimum))
            let current = min(max(width ?? listWidth.ideal, floor), upper)
            HStack(spacing: 0) {
                list.frame(width: current)
                Rectangle()
                    .fill(Surface.stroke)
                    .frame(width: 1)
                    .overlay {
                        Color.clear
                            .frame(width: 9)
                            .contentShape(Rectangle())
                            .onHover { inside in
                                if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() }
                            }
                            .gesture(DragGesture(minimumDistance: 1)
                                .onChanged { drag in
                                    let start = dragStart ?? current
                                    dragStart = start
                                    width = min(max(start + drag.translation.width, floor), upper)
                                }
                                .onEnded { _ in dragStart = nil })
                    }
                    .accessibilityHidden(true)
                detail.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }
}

/// File-type symbols for sources.
enum SourceSymbol {
    static func of(_ locator: String) -> String {
        switch (locator as NSString).pathExtension.lowercased() {
        case "md", "markdown", "txt": "doc.text"
        case "pdf", "docx", "docm", "rtf": "doc.richtext"
        case "xlsx", "xlsm": "tablecells"
        case "eml", "emlx": "envelope"
        case "html", "htm": "globe"
        case "csv", "tsv": "tablecells"
        case "ofx", "qfx", "qbo", "qif": "banknote"
        case "png", "jpg", "jpeg", "heic", "gif": "photo"
        case "mp4", "mov", "m4v": "film"
        case "mp3", "m4a", "wav": "waveform"
        case "vtt", "srt": "captions.bubble"
        case "cs", "swift", "c", "h", "cpp", "hpp", "asm", "s", "py", "js", "ts": "chevron.left.forwardslash.chevron.right"
        default: "doc"
        }
    }
}

/// Opens the app's Settings window at a tab on macOS 13 and later.
enum SettingsOpener {
    @MainActor
    static func open(_ tab: SettingsTab = .general) {
        UserDefaults.standard.set(tab.rawValue, forKey: SettingsTab.storageKey)
        if !NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil) {
            NSApp.sendAction(Selector(("showPreferencesWindow:")), to: nil, from: nil)
        }
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// Lays children out in rows, wrapping to the next row when the width runs out.
struct FlowLayout: Layout {
    var spacing: CGFloat = Spacing.s

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        let width = rows.map(\.width).max() ?? 0
        let height = rows.map(\.height).reduce(0, +) + spacing * CGFloat(max(rows.count - 1, 0))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [Row] {
        var rows: [Row] = [Row()]
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            let needed = rows[rows.count - 1].indices.isEmpty ? size.width : rows[rows.count - 1].width + spacing + size.width
            if needed > width && !rows[rows.count - 1].indices.isEmpty {
                rows.append(Row())
            }
            var row = rows[rows.count - 1]
            row.width = row.indices.isEmpty ? size.width : row.width + spacing + size.width
            row.height = max(row.height, size.height)
            row.indices.append(index)
            rows[rows.count - 1] = row
        }
        return rows
    }
}

/// A small rounded tag, optionally removable.
struct Chip: View {
    let text: String
    var systemImage: String?
    var onRemove: (() -> Void)?

    var body: some View {
        HStack(spacing: Spacing.xs) {
            if let systemImage { Image(systemName: systemImage).imageScale(.small).foregroundStyle(.secondary) }
            Text(text).lineLimit(1)
            if let onRemove {
                Button(action: onRemove) { Image(systemName: "xmark").imageScale(.small) }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Remove \(text)")
            }
        }
        .font(.caption)
        .padding(.horizontal, Spacing.s)
        .padding(.vertical, Spacing.xs)
        .background(Surface.well, in: Capsule())
    }
}

/// Relative and absolute renderings of the worker's ISO 8601 timestamps.
enum DateText {
    static func date(_ iso: String) -> Date? {
        let parser = ISO8601DateFormatter()
        parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return parser.date(from: iso) ?? ISO8601DateFormatter().date(from: iso)
    }

    static func short(_ iso: String) -> String {
        guard let date = date(iso) else { return iso }
        return date.formatted(date: .abbreviated, time: .shortened)
    }

    static func relative(_ iso: String, now: Date = Date()) -> String {
        guard let date = date(iso) else { return iso }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter.localizedString(for: date, relativeTo: now)
    }
}
