import SwiftUI

@main
struct PraxisApp: App {
    @StateObject private var model = WorkbenchModel()

    var body: some Scene {
        Window("Praxis", id: "main") {
            PraxisWindow()
                .environmentObject(model)
                .settingsOpenerBridge()
                .frame(minWidth: 760, minHeight: 520)
        }
        .defaultSize(width: 1180, height: 760)
        .commands { PraxisCommands(model: model) }

        Settings {
            SettingsView()
                .environmentObject(model)
        }
    }
}
