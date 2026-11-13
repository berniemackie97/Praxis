import SwiftUI

/// The workspaces in the main window's sidebar, grouped by what the owner is doing.
enum AppSection: String, CaseIterable, Identifiable {
    case chat, approvals, library, quickNote, search, knowledge, collections, verification

    var id: String { rawValue }

    var title: String {
        switch self {
        case .chat: "Ask Praxis"
        case .approvals: "Approvals"
        case .library: "Library"
        case .quickNote: "Quick Note"
        case .search: "Search"
        case .knowledge: "Knowledge"
        case .collections: "Collections"
        case .verification: "Verification"
        }
    }

    var subtitle: String {
        switch self {
        case .chat: "Answers grounded in your sources"
        case .approvals: "Decisions only you can make"
        case .library: "Everything captured from your watched folders"
        case .quickNote: "Capture a thought into your Inbox"
        case .search: "Keyword and semantic search across sources"
        case .knowledge: "People, projects and ideas found in your sources"
        case .collections: "Your own groupings of sources"
        case .verification: "Check local results with a stronger model"
        }
    }

    var systemImage: String {
        switch self {
        case .chat: "bubble.left.and.text.bubble.right"
        case .approvals: "checkmark.seal"
        case .library: "books.vertical"
        case .quickNote: "square.and.pencil"
        case .search: "magnifyingglass"
        case .knowledge: "point.3.connected.trianglepath.dotted"
        case .collections: "folder"
        case .verification: "checkmark.shield"
        }
    }

    var shortcut: KeyEquivalent {
        KeyEquivalent(Character(String((Self.allCases.firstIndex(of: self) ?? 0) + 1)))
    }

    struct Group: Identifiable {
        let title: String
        let sections: [AppSection]
        var id: String { title }
    }

    static let groups: [Group] = [
        Group(title: "Ask", sections: [.chat]),
        Group(title: "Capture", sections: [.library, .quickNote]),
        Group(title: "Explore", sections: [.search, .knowledge, .collections]),
        Group(title: "Review", sections: [.approvals, .verification])
    ]
}

private struct WorkspaceKey: FocusedValueKey {
    typealias Value = Binding<AppSection>
}

extension FocusedValues {
    var workspace: Binding<AppSection>? {
        get { self[WorkspaceKey.self] }
        set { self[WorkspaceKey.self] = newValue }
    }
}

/// Menu commands: a Go menu with ⌘1–⌘8 and New Conversation on ⌘N.
struct PraxisCommands: Commands {
    @ObservedObject var model: WorkbenchModel
    @FocusedBinding(\.workspace) private var workspace

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Conversation") {
                model.chat.newConversation()
                workspace = .chat
            }
            .keyboardShortcut("n")
            .disabled(!model.running)
            Button("New Quick Note") { workspace = .quickNote }
                .keyboardShortcut("n", modifiers: [.command, .shift])
                .disabled(!model.running)
        }
        CommandMenu("Go") {
            ForEach(AppSection.allCases) { section in
                Button(section.title) { workspace = section }
                    .keyboardShortcut(section.shortcut)
                    .disabled(!model.running)
            }
        }
    }
}
