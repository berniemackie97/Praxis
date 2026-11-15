import AppKit
import SwiftUI

struct SkillsView: View {
    @ObservedObject var model: SkillsWorkbenchModel
    let ask: (String) -> Void
    @State private var query = ""
    @State private var importing = false
    @State private var markdown = ""

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            FeedbackLine(error: model.error, notice: model.notice)
                .padding(.horizontal, Spacing.l)
            List {
                if !model.drafts.isEmpty {
                    Section("Awaiting review") {
                        ForEach(model.drafts) { draft in
                            Button { model.openRevision(draft) } label: {
                                row(draft.name, draft.description)
                            }.buttonStyle(.plain)
                        }
                    }
                }
                Section("Your skills") {
                    if model.installed.allSatisfy(\.bundled) {
                        Text("Teach Praxis a repeatable workflow in a local chat, or import a SKILL.md.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(filtered(bundled: false)) { skill in skillRow(skill) }
                }
                Section("Starter skills") {
                    ForEach(filtered(bundled: true)) { skill in skillRow(skill) }
                }
            }
        }
        .searchable(text: $query, prompt: "Find a skill")
        .toolbar {
            ToolbarItemGroup {
                Button { ask("$skill-authoring Help me teach you a reusable workflow.") }
                    label: { Label("Teach in Chat", systemImage: "bubble.left") }
                Button { markdown = ""; importing = true }
                    label: { Label("Import Markdown", systemImage: "square.and.arrow.down") }
                Button { model.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
        .onAppear { model.refresh() }
        .sheet(item: $model.preview) { skill in previewSheet(skill) }
        .sheet(item: $model.review) { review in reviewSheet(review) }
        .sheet(isPresented: $importing) { importSheet }
    }

    private func filtered(bundled: Bool) -> [SkillListItem] {
        model.installed.filter {
            $0.bundled == bundled && (query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) ||
                $0.description.localizedCaseInsensitiveContains(query))
        }
    }

    private func skillRow(_ skill: SkillListItem) -> some View {
        HStack {
            Button { model.open(skill) } label: { row(skill.name, skill.description) }
                .buttonStyle(.plain)
            Spacer()
            Toggle("Enable \(skill.name)", isOn: Binding(get: { skill.enabled }, set: { model.setEnabled(skill, $0) }))
                .labelsHidden()
                .toggleStyle(.switch)
        }
    }

    private func row(_ title: String, _ subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            Text(title).font(.rowTitle)
            Text(subtitle).font(.meta).foregroundStyle(.secondary)
        }.padding(.vertical, Spacing.xs)
    }

    private func previewSheet(_ skill: SkillPreview) -> some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text(skill.name).font(.title2)
            markdownPreview(skill.markdown)
            if !model.history.isEmpty {
                Menu("Version History") {
                    ForEach(model.history) { item in
                        Button("\(DateText.short(item.createdUtc)) · \(item.state.capitalized) · \(item.digest.prefix(7))") {
                            model.openRevision(item)
                        }
                    }
                }
            }
            HStack {
                Button("Copy SKILL.md") { copy(skill.markdown) }
                Spacer()
                Button("Done") { model.preview = nil }.keyboardShortcut(.cancelAction)
            }
        }.padding(Spacing.xl).frame(width: 640, height: 475)
    }

    private func reviewSheet(_ review: SkillReview) -> some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text(review.summary.pending ? "Review \(review.summary.name)" : review.summary.name).font(.title2)
            Text(review.summary.rationale).foregroundStyle(.secondary)
            FeedbackLine(error: model.error, notice: nil)
            if let previous = model.currentVersion {
                HStack(alignment: .top, spacing: Spacing.m) {
                    VStack(alignment: .leading) {
                        Text("Previous approved version").font(.headline)
                        markdownPreview(previous.markdown)
                    }
                    VStack(alignment: .leading) {
                        Text("Proposed version").font(.headline)
                        markdownPreview(review.markdown)
                    }
                }
            } else {
                markdownPreview(review.markdown)
            }
            HStack {
                Button("Copy SKILL.md") { copy(review.markdown) }
                Spacer()
                Button("Close") { model.review = nil }.keyboardShortcut(.cancelAction)
                if review.summary.pending {
                    Button("Reject") { model.resolve(review, accept: false) }.disabled(model.saving)
                    Button("Approve Skill") { model.resolve(review, accept: true) }
                        .buttonStyle(.borderedProminent)
                        .disabled(model.saving || (review.summary.baseRevisionId != nil && model.currentVersion == nil))
                }
            }
        }.padding(Spacing.xl).frame(minWidth: 640, idealWidth: 770, minHeight: 475)
    }

    private var importSheet: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text("Import a skill draft").font(.title2)
            Text("Paste the contents of SKILL.md. Praxis imports text only and asks you to review it before use.")
                .foregroundStyle(.secondary)
            FeedbackLine(error: model.error, notice: nil)
            TextEditor(text: $markdown).font(.system(.body, design: .monospaced)).accessibilityLabel("SKILL.md text")
            HStack {
                Spacer()
                Button("Cancel") { importing = false }.keyboardShortcut(.cancelAction)
                Button("Import for Review") { model.importDraft(markdown) { importing = false } }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.saving || markdown.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }.padding(Spacing.xl).frame(width: 620, height: 430)
    }

    private func markdownPreview(_ content: String) -> some View {
        ScrollView {
            Text(content).font(.system(.callout, design: .monospaced)).textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }.padding(Spacing.m).background(Surface.well, in: RoundedRectangle(cornerRadius: Radius.m))
    }

    private func copy(_ markdown: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(markdown, forType: .string)
    }
}
