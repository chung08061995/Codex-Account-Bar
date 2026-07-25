import SwiftUI

@main
struct CodexAccountBarApp: App {
    @StateObject private var store = AccountStore()

    var body: some Scene {
        MenuBarExtra {
            ContentView()
                .environmentObject(store)
        } label: {
            TopBarQuotaLabel(
                window: store.activeQuotaWindow,
                providerActive: store.activeProviderID != nil
            )
        }
        .menuBarExtraStyle(.window)
    }
}

private struct TopBarQuotaLabel: View {
    let window: UsageWindow?
    let providerActive: Bool

    var body: some View {
        if let window {
            HStack(spacing: 3) {
                ZStack {
                    Circle()
                        .stroke(.primary.opacity(0.24), lineWidth: 2)
                    Circle()
                        .trim(from: 0, to: window.remainingPercent / 100)
                        .stroke(.primary, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
                .frame(width: 12, height: 12)
                Text("\(Int(window.remainingPercent.rounded()))%")
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
            }
            .accessibilityLabel("Codex quota \(Int(window.remainingPercent.rounded())) percent remaining")
        } else {
            Image(systemName: providerActive ? "server.rack" : "person.crop.circle")
                .accessibilityLabel(providerActive ? "External provider active" : "Codex Account Bar")
        }
    }
}
