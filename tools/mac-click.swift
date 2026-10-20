// Click once at a screen point (points, top-left origin, like CGEventGetLocation) with synthetic input.
// CI uses it to pick a theme swatch in the menu-bar panel and to click the menu-bar item (needs Accessibility
// for the caller).
//   swiftc tools/mac-click.swift -o click && ./click 762 538 [right|ctrl]
import CoreGraphics
import Foundation

let args = CommandLine.arguments
guard args.count >= 3, let x = Double(args[1]), let y = Double(args[2]) else { print("usage: click <x> <y> [right|ctrl]"); exit(2) }
let how = args.count > 3 ? args[3] : "left"
let p = CGPoint(x: x, y: y)
let src = CGEventSource(stateID: .hidSystemState)
func post(_ type: CGEventType, _ button: CGMouseButton = .left, _ flags: CGEventFlags = []) {
    guard let e = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: button) else { return }
    e.flags = flags
    e.post(tap: .cghidEventTap)
}
post(.mouseMoved)
usleep(150_000)
let right = how == "right", flags: CGEventFlags = how == "ctrl" ? .maskControl : []
post(right ? .rightMouseDown : .leftMouseDown, right ? .right : .left, flags)
usleep(120_000) // a person's press: longer than Convertino's input poll
post(right ? .rightMouseUp : .leftMouseUp, right ? .right : .left, flags)
usleep(100_000)
print("\(how)-clicked \(Int(x)),\(Int(y))")
