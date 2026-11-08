import ApplicationServices
import Foundation

guard AXIsProcessTrusted() else {
    fatalError("Native mouse tests require Accessibility access.")
}

struct Request: Decodable {
    let type: String
    let x: Double
    let y: Double
}

guard let source = CGEventSource(stateID: .combinedSessionState) else {
    fatalError("The native mouse event source is unavailable.")
}
let types: [String: CGEventType] = [
    "move": .mouseMoved,
    "drag": .leftMouseDragged,
    "down": .leftMouseDown,
    "up": .leftMouseUp,
]
while let line = readLine() {
    let request = try JSONDecoder().decode(Request.self, from: Data(line.utf8))
    if request.type != "position" {
        guard let type = types[request.type],
              let event = CGEvent(mouseEventSource: source, mouseType: type,
                                  mouseCursorPosition: CGPoint(x: request.x, y: request.y), mouseButton: .left) else {
            fatalError("The native mouse event could not be created.")
        }
        event.setIntegerValueField(.mouseEventClickState, value: type == .mouseMoved || type == .leftMouseDragged ? 0 : 1)
        event.post(tap: .cghidEventTap)
    }
    guard let position = CGEvent(source: source)?.location else {
        fatalError("The mouse position is unavailable.")
    }
    let data = try JSONEncoder().encode(["x": position.x, "y": position.y])
    print(String(decoding: data, as: UTF8.self))
    fflush(stdout)
}
