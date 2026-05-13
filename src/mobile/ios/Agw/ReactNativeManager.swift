import React
import React_RCTAppDelegate
import ReactAppDependencyProvider

final class ReactNativeManager {
    static let shared = ReactNativeManager()

    let delegate: ReactNativeDelegate
    let factory: RCTReactNativeFactory

    private init() {
        #if DEBUG
        print("RCTReactNativeFactory created")
        #endif

        let delegate = ReactNativeDelegate()
        delegate.dependencyProvider = RCTAppDependencyProvider()

        self.delegate = delegate
        self.factory = RCTReactNativeFactory(delegate: delegate)
    }
}
