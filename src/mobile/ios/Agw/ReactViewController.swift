import UIKit
import React
import React_RCTAppDelegate

final class ReactViewController: UIViewController {
    private let page: ReactNativePage

    init(page: ReactNativePage) {
        self.page = page
        super.init(nibName: nil, bundle: nil)
        title = page.title
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func viewDidLoad() {
        super.viewDidLoad()

        view = ReactNativeManager.shared.factory.rootViewFactory.view(
            withModuleName: "FeReactNative",
            initialProperties: page.props
        )
    }
}

final class ReactNativeDelegate: RCTDefaultReactNativeFactoryDelegate {
    override func sourceURL(for bridge: RCTBridge) -> URL? {
        bundleURL()
    }

    override func bundleURL() -> URL? {
        #if DEBUG
        RCTBundleURLProvider.sharedSettings().jsBundleURL(forBundleRoot: "index")
        #else
        Bundle.main.url(forResource: "main", withExtension: "jsbundle")
        #endif
    }
}
