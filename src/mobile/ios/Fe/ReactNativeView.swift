import SwiftUI

struct ReactNativeView: UIViewControllerRepresentable {
    let page: ReactNativePage

    func makeUIViewController(context: Context) -> ReactViewController {
        ReactViewController(page: page)
    }

    func updateUIViewController(_ uiViewController: ReactViewController, context: Context) {
    }
}
