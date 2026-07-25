import SwiftUI

@main
struct CodexAccountBarApp: App {
    @StateObject private var store = AccountStore()

    var body: some Scene {
        MenuBarExtra {
            ContentView()
                .environmentObject(store)
        } label: {
            Image(systemName: store.activeProviderID == nil ? "person.crop.circle" : "server.rack")
        }
        .menuBarExtraStyle(.window)
    }
}
