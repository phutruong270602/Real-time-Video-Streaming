import SwiftUI

@main
struct iPhoneVideoSenderApp: App {
    init() {
        AppLogger.info("Application launched", category: "App")
    }

    var body: some Scene {
        WindowGroup {
            RTSPSenderView()
        }
    }
}
