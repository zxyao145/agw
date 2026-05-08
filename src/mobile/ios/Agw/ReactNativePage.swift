import Foundation

struct ReactNativePage: Identifiable {
    let id: String
    let routeName: String
    let title: String
    let systemImage: String
    let initialProps: [String: Any]

    init(routeName: String, title: String, systemImage: String, initialProps: [String: Any] = [:]) {
        self.id = routeName
        self.routeName = routeName
        self.title = title
        self.systemImage = systemImage
        self.initialProps = initialProps
    }

    var props: [String: Any] {
        var props = initialProps
        props["routeName"] = routeName
        props["title"] = title
        props["source"] = "SwiftUI"
        return props
    }

    static let samples: [ReactNativePage] = [
        ReactNativePage(routeName: "home", title: "Home", systemImage: "house"),
        ReactNativePage(routeName: "settings", title: "Settings", systemImage: "gearshape"),
        ReactNativePage(routeName: "details", title: "Details", systemImage: "doc.text", initialProps: [
            "itemId": "FE-42"
        ]),
    ]
}
