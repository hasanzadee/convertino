// Drag a file with synthetic input the way a person would: press, drag, tap Shift, page the rings with the wheel and
// Tab (both round trips, so it ends on Convert again), drop on the first petal.
// CI uses it to exercise Convertino's drag + Shift gesture on a macOS runner (needs Accessibility for the caller).
//   swiftc tools/mac-drag-test.swift -o dragtest && ./dragtest /path/to/file.png [screenshot.png]
// The drag source is this script's own window, so the geometry is exact; the drop target is the running Convertino.
import AppKit

let args = CommandLine.arguments
guard args.count >= 2 else { print("usage: dragtest <file> [screenshot.png]"); exit(2) }
let file = URL(fileURLWithPath: args[1])
let shot = args.count > 2 ? args[2] : nil

final class DragView: NSView, NSDraggingSource {
    var started = false
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) { NSColor.systemPink.setFill(); bounds.fill() }
    override func mouseDragged(with event: NSEvent) {
        guard !started else { return }
        started = true
        print("drag session starts")
        let item = NSDraggingItem(pasteboardWriter: file as NSURL)
        item.setDraggingFrame(NSRect(x: 28, y: 28, width: 64, height: 64), contents: NSWorkspace.shared.icon(forFile: file.path))
        beginDraggingSession(with: [item], event: event, source: self)
    }
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        print("drag ended, operation=\(operation.rawValue) (1 = copy)")
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let screen = NSScreen.main!.frame
let win = NSWindow(contentRect: NSRect(x: screen.midX - 380, y: screen.midY - 60, width: 120, height: 120),
                   styleMask: [.titled], backing: .buffered, defer: false)
win.title = "drag source"
let view = DragView(frame: NSRect(x: 0, y: 0, width: 120, height: 120))
win.contentView = view
win.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)

/// The view's centre in CoreGraphics (top-left origin) coordinates.
func cgCenter() -> CGPoint {
    let r = win.convertToScreen(view.convert(view.bounds, to: nil))
    return CGPoint(x: r.midX, y: screen.height - r.midY)
}

let source = CGEventSource(stateID: .hidSystemState)
func mouse(_ type: CGEventType, _ p: CGPoint) {
    CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: .left)!.post(tap: .cghidEventTap)
}
func shift(down: Bool) {
    let e = CGEvent(keyboardEventSource: source, virtualKey: 56, keyDown: down)!
    e.flags = down ? .maskShift : []
    e.post(tap: .cghidEventTap)
}
func scroll(_ lines: Int32) {
    CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 1, wheel1: lines, wheel2: 0, wheel3: 0)!.post(tap: .cghidEventTap)
}
func tab() {
    CGEvent(keyboardEventSource: source, virtualKey: 48, keyDown: true)!.post(tap: .cghidEventTap); usleep(90_000)
    CGEvent(keyboardEventSource: source, virtualKey: 48, keyDown: false)!.post(tap: .cghidEventTap)
}
func drag(from a: CGPoint, to b: CGPoint, steps: Int) {
    for i in 1...steps {
        let t = CGFloat(i) / CGFloat(steps)
        mouse(.leftMouseDragged, CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t))
        usleep(16_000)
    }
}

let start = cgCenter()
DispatchQueue.global().async {
    sleep(2)
    let held = CGPoint(x: start.x + 300, y: start.y)   // where Shift is tapped: the flower opens centred here
    print("press at \(start), Shift at \(held)")
    mouse(.mouseMoved, start); usleep(200_000)
    mouse(.leftMouseDown, start); usleep(150_000)
    drag(from: start, to: held, steps: 30)
    usleep(400_000)
    shift(down: true); usleep(120_000)
    shift(down: false); usleep(500_000)
    // move a little so the drag enters the flower and it blooms (a seed that sees no drag closes after 1.6 s)
    let near = CGPoint(x: held.x, y: held.y - 30)
    drag(from: held, to: near, steps: 6); usleep(500_000)
    // Convertino logs "ring 2/4 Tools (wheel)" etc. with CONVERTINO_DEBUG=1; nothing here depends on it
    scroll(-3); usleep(500_000)          // next ring
    scroll(3); usleep(500_000)           // back to Convert
    for _ in 0..<4 { tab(); usleep(400_000) } // an image has four rings: four Tabs come back round (Tab needs Input Monitoring)
    print("paged with the wheel and Tab")
    // the first Convert petal sits straight above the centre, halfway across the ring (R1 64 … R2 170)
    let petal = CGPoint(x: held.x, y: held.y - 117)
    drag(from: near, to: petal, steps: 20)
    usleep(800_000)
    if let shot {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-x", shot]
        try? p.run()
        p.waitUntilExit()
    }
    mouse(.leftMouseUp, petal)
    print("dropped at \(petal)")
    sleep(3)
    DispatchQueue.main.async { app.terminate(nil) }
}
app.run()
