// Drag a picture the way Safari and Chrome do — a file promise (NSFilePromiseProvider) plus the picture's link — with
// synthetic input: press, drag, tap Shift, drop on a petal of the Convert ring. CI uses it to check that Convertino
// receives file promises (NSFilePromiseReceiver) and keeps the result in ~/Downloads (needs Accessibility for the caller).
//   swiftc tools/mac-web-drag.swift -o webdrag && ./webdrag picture.png https://example.com/sunset.png <petal> <petals> [screenshot.png]
// <petal> counts clockwise from the top petal (Save) of a ring of <petals>.
import AppKit
import UniformTypeIdentifiers

let args = CommandLine.arguments
guard args.count >= 5 else { print("usage: webdrag <png> <url> <petal> <petals> [screenshot.png]"); exit(2) }
let picture = URL(fileURLWithPath: args[1])
let link = args[2]
let petal = Double(args[3])!, petals = Double(args[4])!
let shot = args.count > 5 ? args[5] : nil

/// What a browser puts on the drag pasteboard for a picture: the promise of the file, and its address as public.url.
final class PictureProvider: NSFilePromiseProvider {
    override func writableTypes(for pasteboard: NSPasteboard) -> [NSPasteboard.PasteboardType] {
        super.writableTypes(for: pasteboard) + [.URL]
    }
    override func pasteboardPropertyList(forType type: NSPasteboard.PasteboardType) -> Any? {
        type == .URL ? link.data(using: .utf8) : super.pasteboardPropertyList(forType: type)
    }
}

final class Promise: NSObject, NSFilePromiseProviderDelegate {
    func filePromiseProvider(_ p: NSFilePromiseProvider, fileNameForType fileType: String) -> String { picture.lastPathComponent }
    func filePromiseProvider(_ p: NSFilePromiseProvider, writePromiseTo url: URL, completionHandler: @escaping (Error?) -> Void) {
        print("promise asked for: \(url.path)")
        do { try FileManager.default.copyItem(at: picture, to: url); completionHandler(nil) } catch { completionHandler(error) }
    }
}
let promise = Promise()

final class DragView: NSView, NSDraggingSource {
    var started = false
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) { NSColor.systemOrange.setFill(); bounds.fill() }
    override func mouseDragged(with event: NSEvent) {
        guard !started else { return }
        started = true
        print("drag session starts")
        let provider = PictureProvider(fileType: UTType.png.identifier, delegate: promise)
        let item = NSDraggingItem(pasteboardWriter: provider)
        item.setDraggingFrame(NSRect(x: 28, y: 28, width: 64, height: 64), contents: NSImage(contentsOf: picture))
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
win.title = "web page"
let view = DragView(frame: NSRect(x: 0, y: 0, width: 120, height: 120))
win.contentView = view
win.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)

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
    let held = CGPoint(x: start.x + 300, y: start.y)
    mouse(.mouseMoved, start); usleep(200_000)
    mouse(.leftMouseDown, start); usleep(150_000)
    drag(from: start, to: held, steps: 30)
    usleep(400_000)
    shift(down: true); usleep(120_000)
    shift(down: false); usleep(500_000)
    let near = CGPoint(x: held.x, y: held.y - 30)
    drag(from: held, to: near, steps: 6); usleep(700_000)
    // petal i of n sits at -90° + i·360°/n, halfway across the ring (R1 64 … R2 170); y grows downwards here
    let a = -Double.pi / 2 + petal * 2 * Double.pi / petals
    let target = CGPoint(x: held.x + 117 * cos(a), y: held.y + 117 * sin(a))
    drag(from: near, to: target, steps: 20)
    usleep(800_000)
    if let shot {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-x", shot]
        try? p.run()
        p.waitUntilExit()
    }
    mouse(.leftMouseUp, target)
    print("dropped at \(target)")
    sleep(6) // keep the source alive while the promise is fulfilled
    DispatchQueue.main.async { app.terminate(nil) }
}
app.run()
