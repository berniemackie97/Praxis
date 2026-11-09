import SwiftUI

@main
struct PraxisApp: App {
    @StateObject private var model = WorkbenchModel()
    var body: some Scene {
        Window("Praxis", id: "main") {
            PraxisWindow()
                .environmentObject(model)
                .frame(minWidth: 880, minHeight: 600)
        }
    }
}
