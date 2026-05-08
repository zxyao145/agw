import UIKit
import React
import React_RCTAppDelegate
import ReactAppDependencyProvider

final class ReactViewController: UIViewController {
    private let page: ReactNativePage
    private var reactNativeDelegate: ReactNativeDelegate?
    private var reactNativeFactory: RCTReactNativeFactory?

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

        let delegate = ReactNativeDelegate()
        delegate.dependencyProvider = RCTAppDependencyProvider()

        let factory = RCTReactNativeFactory(delegate: delegate)
        reactNativeDelegate = delegate
        reactNativeFactory = factory

        view = factory.rootViewFactory.view(
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
