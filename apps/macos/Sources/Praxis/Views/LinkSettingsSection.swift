import SwiftUI

/// Settings → Privacy: whether Praxis fetches the links the owner saves, and hosts it never contacts.
struct LinkSettingsSection: View {
    @EnvironmentObject private var workbench: WorkbenchModel
    @State private var mode = "saved_links"
    @State private var blocked = ""
    @State private var studyMedia = false
    @State private var sampleFrames = false
    @State private var visionModel = ""
    @State private var maximumMediaMinutes = 60

    private var model: LinkSettingsModel { workbench.links }

    var body: some View {
        Section {
            Picker("Fetch links", selection: $mode) {
                Text("Links I save").tag("saved_links")
                Text("Never").tag("off")
            }
            .pickerStyle(.radioGroup)
            Toggle("Download saved videos for local study", isOn: $studyMedia)
            if studyMedia {
                Text("Captionless videos can use on-device speech recognition. Media downloads are bounded and temporary. No cookies, account access or cloud transcription.")
                    .font(.meta).foregroundStyle(.secondary)
                Stepper("Up to \(maximumMediaMinutes) minutes per video", value: $maximumMediaMinutes, in: 1...240)
                Toggle("Sample frames and read on-screen text", isOn: $sampleFrames)
                if sampleFrames {
                    Picker("Local vision model", selection: $visionModel) {
                        Text("OCR only").tag("")
                        ForEach((workbench.modelSetup.machine?.installed ?? []).filter { $0.capabilities?.vision == true }) { Text($0.name).tag($0.name) }
                    }
                    Text("Up to 12 preserved frames. Model descriptions remain unverified; unsampled intervals are unknown. Requires an installed vision-capable model.")
                        .font(.meta).foregroundStyle(.secondary)
                }
            }
            VStack(alignment: .leading, spacing: Spacing.xs) {
                Text("Never contact these hosts")
                TextEditor(text: $blocked)
                    .font(.body.monospaced())
                    .frame(minHeight: 54, maxHeight: 96)
                    .overlay(RoundedRectangle(cornerRadius: Radius.s).strokeBorder(Surface.stroke))
                Text("One per line. Subdomains are blocked too.").font(.meta).foregroundStyle(.secondary)
            }
            HStack {
                if let message = model.error ?? model.notice {
                    Text(message).font(.meta).foregroundStyle(model.error == nil ? Color.secondary : Color.red).lineLimit(2)
                }
                Spacer()
                Button("Save") { model.save(mode: mode, blockedHosts: LinkSettingsModel.hosts(from: blocked), studyMedia: studyMedia,
                                            sampleFrames: sampleFrames, visionModel: visionModel, maximumMediaMinutes: maximumMediaMinutes) }
                    .disabled(model.settings == nil || !changed)
            }
        } header: {
            Text("Saved links").font(.sectionTitle).frame(maxWidth: .infinity, alignment: .leading)
        } footer: {
            Text("Praxis fetches only links you save (.webloc or .url files, Save Link…, or a link you paste in chat and ask to save) — never links inside emails or documents. It connects only to public addresses, keeps no cookies, and saves what it read in “Praxis Web” in your intake folder. YouTube links keep their captions so chat can show clips.")
                .font(.meta).foregroundStyle(.secondary)
        }
        .onAppear { model.refresh(); workbench.modelSetup.refresh() }
        .onReceive(model.$settings.compactMap { $0 }) { settings in
            mode = settings.mode
            blocked = settings.blockedHosts.joined(separator: "\n")
            studyMedia = settings.studyMedia ?? false
            sampleFrames = settings.sampleFrames ?? false
            visionModel = settings.visionModel ?? ""
            maximumMediaMinutes = settings.maximumMediaMinutes ?? 60
        }
    }

    private var changed: Bool {
        guard let settings = model.settings else { return false }
        return settings.mode != mode || settings.blockedHosts != LinkSettingsModel.hosts(from: blocked) ||
            (settings.studyMedia ?? false) != studyMedia || (settings.sampleFrames ?? false) != sampleFrames ||
            (settings.visionModel ?? "") != visionModel || (settings.maximumMediaMinutes ?? 60) != maximumMediaMinutes
    }
}

/// Save Link…: the owner types or pastes an address; Praxis saves it as a link file and reads it.
struct SaveLinkSheet: View {
    @ObservedObject var model: LinkSettingsModel
    @Environment(\.dismiss) private var dismiss
    @State private var url = ""
    @State private var title = ""
    @State private var saving = false
    @FocusState private var focused: Bool

    private var valid: Bool {
        guard let parsed = URL(string: url.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = parsed.scheme?.lowercased(), parsed.host?.isEmpty == false else { return false }
        return scheme == "http" || scheme == "https"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text("Save Link").font(.sectionTitle)
            Text("Praxis fetches the page (or a YouTube video's captions), reads it and makes it searchable and citable in chat.")
                .font(.callout).foregroundStyle(.secondary)
            Form {
                TextField("Address", text: $url, prompt: Text("https://…"))
                    .focused($focused)
                TextField("Title", text: $title, prompt: Text("Optional"))
            }
            if let error = model.error {
                Label(error, systemImage: "exclamationmark.triangle").font(.meta).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    saving = true
                    model.saveLink(url, title: title) { saved in
                        saving = false
                        if saved { dismiss() }
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!valid || saving)
            }
        }
        .padding(Spacing.l)
        .frame(width: 440)
        .onAppear {
            if let pasted = NSPasteboard.general.string(forType: .string), pasted.hasPrefix("http"), pasted.count < 2_048 {
                url = pasted.trimmingCharacters(in: .whitespacesAndNewlines)
            }
            focused = true
        }
    }
}

/// Presents Save Link… whenever the command asks for it.
struct SaveLinkPresenter: ViewModifier {
    @ObservedObject var links: LinkSettingsModel

    func body(content: Content) -> some View {
        content.sheet(isPresented: $links.presentingSave) { SaveLinkSheet(model: links) }
    }
}
