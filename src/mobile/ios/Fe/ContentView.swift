import SwiftUI

struct ContentView: View {
    private let pages = ReactNativePage.samples

    var body: some View {
        TabView {
            ForEach(pages) { page in
                NavigationStack {
                    ReactNativeView(page: page)
                }
                .tabItem {
                    Label(page.title, systemImage: page.systemImage)
                }
                .modifier(TabBarBackgroundFix())
            }
        }
        
    }
}

struct TabBarBackgroundFix: ViewModifier {
    func body(content: Content) -> some View {
        if #unavailable(iOS 26.0) {
            content
                .toolbarBackground(.white, for: .tabBar)
                .toolbarBackground(.visible, for: .tabBar)
        } else {
            content
        }
    }
}

#Preview {
    ContentView()
}
