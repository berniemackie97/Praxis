import SwiftUI

/// The latest error or confirmation for a workspace.
struct FeedbackLine: View {
    let error: String?
    let notice: String?

    var body: some View {
        if let error {
            Text(error).font(.caption).foregroundColor(.orange).textSelection(.enabled)
        } else if let notice {
            Text(notice).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
        }
    }
}
