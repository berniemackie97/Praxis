import SwiftUI

/// Tabs of the Settings window (⌘,).
enum SettingsTab: String {
    case general, models, privacy

    static let storageKey = "settingsTab"
}

/// Everything that configures Praxis rather than being used day to day.
struct SettingsView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @AppStorage(SettingsTab.storageKey) private var tab = SettingsTab.general
    @AppStorage("dataRoot") private var dataRoot = ""
    @AppStorage("rootApprovals") private var rootApprovals = "{}"

    var body: some View {
        TabView(selection: $tab) {
            page { ConfigurationView(dataRoot: dataRoot, rootApprovals: $rootApprovals) }
                .tabItem { Label("General", systemImage: "gearshape") }
                .tag(SettingsTab.general)
            page { ModelsSettingsView(setup: model.modelSetup, intelligence: model.intelligence, privacy: model.privacy) }
                .tabItem { Label("Models", systemImage: "cpu") }
                .tag(SettingsTab.models)
            page { PrivacySettingsView(model: model.privacy, intelligence: model.intelligence) }
                .tabItem { Label("Privacy", systemImage: "hand.raised") }
                .tag(SettingsTab.privacy)
        }
        .frame(width: 760, height: 680)
    }

    @ViewBuilder
    private func page<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        if model.running {
            content()
        } else {
            EmptyState(systemImage: "power", title: "Praxis isn't running",
                       message: "Finish setup in the main window, then come back here to change settings.")
        }
    }
}
