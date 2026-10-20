// Click once at a screen point (points, top-left origin, like CGEventGetLocation) with synthetic input.
// CI uses it to pick a theme swatch in the menu-bar panel (needs Accessibility for the caller).
//   swiftc tools/mac-click.swift -o click && ./click 762 538
import CoreGraphics
import Foundation

let args = CommandLine.arguments
guard args.count == 3, let x = Double(args[1]), let y = Double(args[2]) else { print("usage: click <x> <y>"); exit(2) }
let p = CGPoint(x: x, y: y)
let src = CGEventSource(stateID: .hidSystemState)
func post(_ type: CGEventType) {
    CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
}
post(.mouseMoved)
usleep(150_000)
post(.leftMouseDown)
usleep(120_000) // a person's press: longer than Convertino's input poll
post(.leftMouseUp)
usleep(100_000)
print("clicked \(Int(x)),\(Int(y))")
