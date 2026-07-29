import AppKit
import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var store: AccountStore
    @State private var accountToDelete: SavedAccount?

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    accountSection
                    providerSection
                }
                .padding(14)
            }
            Divider()
            footer
        }
        .frame(width: 430, height: 610)
        .alert("Codex Account Bar", isPresented: Binding(
            get: { store.errorMessage != nil },
            set: { if !$0 { store.errorMessage = nil } }
        )) {
            Button("OK", role: .cancel) { store.errorMessage = nil }
        } message: {
            Text(store.errorMessage ?? "")
        }
        .confirmationDialog(
            "Remove saved account?",
            isPresented: Binding(
                get: { accountToDelete != nil },
                set: { if !$0 { accountToDelete = nil } }
            ),
            presenting: accountToDelete
        ) { account in
            Button("Remove \(account.displayName)", role: .destructive) {
                store.deleteAccount(account)
                accountToDelete = nil
            }
            Button("Cancel", role: .cancel) {
                accountToDelete = nil
            }
        } message: { _ in
            Text("This removes the saved account and its stored credential from Codex Account Bar. It does not log out the Codex app.")
        }
    }

    private var header: some View {
        HStack {
            Image(systemName: "switch.2")
                .font(.title2)
                .foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 2) {
                Text("Codex Account Bar").font(.headline)
                Text("Accounts and model providers").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                Task { await store.refreshUsage() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.plain)
            .disabled(store.isBusy || store.isRefreshingUsage)
            .help("Refresh quota")
            if store.isBusy || store.isRefreshingUsage {
                ProgressView().controlSize(.small)
            }
        }
        .padding(14)
    }

    private var accountSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                sectionTitle("ChatGPT accounts", icon: "person.2")
                Spacer()
                if store.isAddingAccount {
                    Button(role: .cancel) {
                        store.cancelAddAccount()
                    } label: {
                        Label("Cancel", systemImage: "xmark")
                    }
                    .tint(.red)
                } else {
                    Button {
                        store.addAccount()
                    } label: {
                        Label("Add", systemImage: "plus")
                    }
                    .disabled(store.isBusy)
                }
            }
            if store.accounts.isEmpty {
                Text("No saved accounts found.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(store.accounts) { account in
                    VStack(spacing: 10) {
                        HStack {
                            statusDot(active: store.activeAccountID == account.id)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(account.displayName).lineLimit(1)
                                Text(account.plan.uppercased())
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(store.activeAccountID == account.id ? "Active" : "Switch") {
                                store.activateAccount(account)
                            }
                            .disabled(store.isBusy || store.activeAccountID == account.id)
                            Menu {
                                Button("Remove Account", role: .destructive) {
                                    accountToDelete = account
                                }
                            } label: {
                                Image(systemName: "ellipsis.circle")
                            }
                            .menuStyle(.borderlessButton)
                        }
                        if let usage = account.usage, let primary = usage.primary {
                            UsageGrid(usage: usage, primary: primary)
                        }
                    }
                    .padding(10)
                    .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
                }
            }
        }
    }

    private var providerSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                sectionTitle("Providers", icon: "server.rack")
                Spacer()
                Button {
                    ProviderWindowController.shared.show(store: store)
                } label: {
                    Label("Add", systemImage: "plus")
                }
            }
            if store.providers.isEmpty {
                Text("Add Claudible, Gemini, Kiro, or a custom provider.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.vertical, 8)
            } else {
                ForEach(store.providers) { profile in
                    VStack(spacing: 10) {
                        HStack {
                            statusDot(active: store.activeProviderID == profile.id)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(profile.name).lineLimit(1)
                                Text("\(profile.providerID) · \(profile.defaultModel)")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                            Spacer()
                            Button(store.activeProviderID == profile.id ? "Reapply" : "Activate") {
                                store.activateProvider(profile)
                            }
                            .disabled(store.isBusy)
                            Menu {
                                Button("Delete", role: .destructive) {
                                    store.deleteProvider(profile)
                                }
                            } label: {
                                Image(systemName: "ellipsis.circle")
                            }
                            .menuStyle(.borderlessButton)
                            .frame(width: 24)
                        }
                        if let usage = store.providerUsage[profile.id], let primary = usage.primary {
                            UsageGrid(usage: usage, primary: primary)
                        }
                    }
                    .padding(10)
                    .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
                }
            }
        }
    }

    private var footer: some View {
        HStack {
            Text(store.statusText)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer()
            if store.isAddingAccount {
                Button("Cancel sign-in", role: .cancel) {
                    store.cancelAddAccount()
                }
                .buttonStyle(.plain)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.red)
            } else {
                Button("Quit") { NSApplication.shared.terminate(nil) }
                    .buttonStyle(.plain)
                    .font(.caption)
            }
        }
        .padding(12)
    }

    private func sectionTitle(_ title: String, icon: String) -> some View {
        Label(title, systemImage: icon)
            .font(.subheadline.weight(.semibold))
    }

    private func statusDot(active: Bool) -> some View {
        Circle()
            .fill(active ? Color.green : Color.secondary.opacity(0.35))
            .frame(width: 8, height: 8)
    }
}

private struct UsageGrid: View {
    let usage: UsageSnapshot
    let primary: UsageWindow

    var body: some View {
        HStack(spacing: 14) {
            UsageRing(window: primary)
            VStack(spacing: 9) {
                UsageLine(window: primary)
                if let secondary = usage.secondary {
                    UsageLine(window: secondary)
                }
                if let balance = usage.creditsBalance, balance != "0" {
                    HStack {
                        Label("Credits", systemImage: "leaf")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                        Spacer()
                        Text(balance).font(.caption2.monospacedDigit())
                    }
                }
            }
        }
    }
}

private struct UsageRing: View {
    let window: UsageWindow

    var body: some View {
        ZStack {
            Circle()
                .stroke(Color.secondary.opacity(0.18), lineWidth: 7)
            Circle()
                .trim(from: 0, to: window.remainingPercent / 100)
                .stroke(
                    quotaColor,
                    style: StrokeStyle(lineWidth: 7, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
            VStack(spacing: -1) {
                Text("\(Int(window.remainingPercent.rounded()))%")
                    .font(.caption.weight(.bold).monospacedDigit())
                Text("left")
                    .font(.system(size: 8))
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: 62, height: 62)
        .accessibilityLabel("\(window.title) quota")
        .accessibilityValue("\(Int(window.remainingPercent)) percent remaining")
    }

    private var quotaColor: Color {
        if window.remainingPercent <= 10 { return .red }
        if window.remainingPercent <= 25 { return .orange }
        return .green
    }
}

private struct UsageLine: View {
    let window: UsageWindow

    var body: some View {
        VStack(spacing: 4) {
            HStack {
                Text(window.title).font(.caption.weight(.medium))
                Spacer()
                Text("\(Int(window.remainingPercent.rounded()))% left")
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            ProgressView(value: window.remainingPercent, total: 100)
                .tint(quotaColor)
            HStack {
                Text(window.resetText)
                Spacer()
            }
            .font(.system(size: 9))
            .foregroundStyle(.tertiary)
        }
    }

    private var quotaColor: Color {
        if window.remainingPercent <= 10 { return .red }
        if window.remainingPercent <= 25 { return .orange }
        return .accentColor
    }
}

@MainActor
private final class ProviderWindowController: NSObject, NSWindowDelegate {
    static let shared = ProviderWindowController()

    private var window: NSWindow?

    func show(store: AccountStore) {
        if let window {
            NSApplication.shared.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            return
        }

        let editor = ProviderEditor { [weak self] in
            self?.window?.close()
        }
        .environmentObject(store)
        let controller = NSHostingController(rootView: editor)
        let window = NSWindow(contentViewController: controller)
        window.title = "Add provider"
        window.styleMask = [.titled, .closable, .miniaturizable]
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        self.window = window

        NSApplication.shared.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        window = nil
    }
}

private struct ProviderEditor: View {
    @EnvironmentObject private var store: AccountStore
    @State private var preset: ProviderPreset = .claudible
    @State private var profile = ProviderProfile.preset(.claudible)
    @State private var apiKey = ""
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add provider").font(.title2.weight(.semibold))

            Picker("Preset", selection: $preset) {
                ForEach(ProviderPreset.allCases) { item in
                    Text(item.title).tag(item)
                }
            }
            .onChange(of: preset) { _, newValue in
                profile = .preset(newValue)
                apiKey = ""
            }

            Form {
                TextField("Display name", text: $profile.name)
                TextField("Provider ID", text: $profile.providerID)
                Picker("Adapter", selection: $profile.adapter) {
                    Text("Anthropic Messages").tag("anthropic")
                    Text("Google Gemini").tag("google")
                    Text("OpenAI Responses").tag("openai-responses")
                    Text("OpenAI Chat").tag("openai-chat")
                    Text("Kiro").tag("kiro")
                }
                TextField("Base URL", text: $profile.baseURL)
                TextField("Default model", text: $profile.defaultModel)
                Picker("Authentication", selection: $profile.authentication) {
                    Text("API key").tag("key")
                    Text("OAuth / imported login").tag("oauth")
                    Text("None").tag("none")
                }
                if profile.requiresAPIKey {
                    SecureField("API key", text: $apiKey)
                }
            }
            .formStyle(.grouped)

            Text("Secrets are saved in macOS Keychain. Activation configures opencodex, syncs models, and restarts Codex.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button("Cancel") { onClose() }
                Button("Save") {
                    profile.providerID = sanitizedProviderID(profile.providerID)
                    store.saveProvider(profile, apiKey: apiKey)
                    onClose()
                }
                .buttonStyle(.borderedProminent)
                .disabled(
                    profile.name.trimmingCharacters(in: .whitespaces).isEmpty ||
                    profile.providerID.trimmingCharacters(in: .whitespaces).isEmpty ||
                    profile.baseURL.trimmingCharacters(in: .whitespaces).isEmpty ||
                    (profile.requiresAPIKey && apiKey.isEmpty)
                )
            }
        }
        .padding(20)
        .frame(width: 520, height: 570)
    }

    private func sanitizedProviderID(_ value: String) -> String {
        value.lowercased()
            .replacingOccurrences(
                of: "[^a-z0-9._-]+",
                with: "-",
                options: .regularExpression
            )
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
    }
}
