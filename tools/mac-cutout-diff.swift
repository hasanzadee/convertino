// Compare two cut-outs of the same picture (Core ML vs CPU): alpha difference stats plus a contact sheet
// (both over a checkerboard, then |Δα| × 8). Exits 1 when they differ visibly.
//   swiftc tools/mac-cutout-diff.swift -o cutdiff && ./cutdiff a.png b.png sheet.png [label-a] [label-b]
import AppKit

let args = CommandLine.arguments
guard args.count >= 4 else { print("usage: cutdiff <a.png> <b.png> <sheet.png> [label-a] [label-b]"); exit(2) }
let labels = [args.count > 4 ? args[4] : "A", args.count > 5 ? args[5] : "B", "|Δα| × 8"]
let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
let premul = CGImageAlphaInfo.premultipliedLast.rawValue

func load(_ path: String) -> CGImage {
    guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { print("can't read \(path)"); exit(2) }
    return img
}

func pixels(_ img: CGImage) -> [UInt8] {
    let w = img.width, h = img.height
    var px = [UInt8](repeating: 0, count: w * h * 4)
    px.withUnsafeMutableBytes { buf in
        let ctx = CGContext(data: buf.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: srgb, bitmapInfo: premul)!
        ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
    }
    return px
}

let a = load(args[1]), b = load(args[2])
guard a.width == b.width, a.height == b.height else {
    print("sizes differ: \(a.width)×\(a.height) vs \(b.width)×\(b.height)"); exit(1)
}
let w = a.width, h = a.height, n = w * h
let pa = pixels(a), pb = pixels(b)
var sum = 0, off = 0, worst = 0, fgA = 0, fgB = 0
var heat = [UInt8](repeating: 255, count: n * 4)
for i in 0..<n {
    let x = Int(pa[i * 4 + 3]), y = Int(pb[i * 4 + 3]), d = abs(x - y)
    sum += d
    worst = max(worst, d)
    if d > 32 { off += 1 }
    if x >= 128 { fgA += 1 }
    if y >= 128 { fgB += 1 }
    let v = UInt8(min(255, d * 8))
    heat[i * 4] = v; heat[i * 4 + 1] = v; heat[i * 4 + 2] = v
}
let mean = Double(sum) / Double(n), share = Double(off) * 100 / Double(n)
print(String(format: "%d×%d · foreground %@ %.1f %% / %@ %.1f %% · alpha |Δ| mean %.3f of 255, max %d · pixels off by > 32: %.3f %%",
             w, h, labels[0], Double(fgA) * 100 / Double(n), labels[1], Double(fgB) * 100 / Double(n), mean, worst, share))

// contact sheet
let heatImg = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4, space: srgb,
                      bitmapInfo: CGBitmapInfo(rawValue: premul), provider: CGDataProvider(data: Data(heat) as CFData)!,
                      decode: nil, shouldInterpolate: true, intent: .defaultIntent)!
let scale = min(420.0 / Double(h), 560.0 / Double(w))
let pw = max(1, Int(Double(w) * scale)), ph = max(1, Int(Double(h) * scale)), gap = 14, caption = 26
let sheet = CGContext(data: nil, width: pw * 3 + gap * 4, height: ph + gap * 2 + caption, bitsPerComponent: 8,
                      bytesPerRow: 0, space: srgb, bitmapInfo: premul)!
sheet.setFillColor(CGColor(srgbRed: 0.12, green: 0.12, blue: 0.18, alpha: 1))
sheet.fill(CGRect(x: 0, y: 0, width: sheet.width, height: sheet.height))
sheet.interpolationQuality = .high
NSGraphicsContext.current = NSGraphicsContext(cgContext: sheet, flipped: false)
for (k, img) in [a, b, heatImg].enumerated() {
    let r = CGRect(x: gap + k * (pw + gap), y: gap, width: pw, height: ph)
    if k < 2 {
        let t = 12
        for cy in 0..<(ph + t - 1) / t {
            for cx in 0..<(pw + t - 1) / t {
                let g: CGFloat = (cx + cy) % 2 == 0 ? 0.85 : 0.65
                sheet.setFillColor(CGColor(gray: g, alpha: 1))
                sheet.fill(CGRect(x: Int(r.minX) + cx * t, y: Int(r.minY) + cy * t, width: min(t, pw - cx * t), height: min(t, ph - cy * t)))
            }
        }
    }
    sheet.draw(img, in: r)
    (labels[k] as NSString).draw(at: NSPoint(x: r.minX, y: r.maxY + 6),
                                 withAttributes: [.font: NSFont.systemFont(ofSize: 14, weight: .semibold), .foregroundColor: NSColor.white])
}
let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: args[3]) as CFURL, "public.png" as CFString, 1, nil)!
CGImageDestinationAddImage(dest, sheet.makeImage()!, nil)
CGImageDestinationFinalize(dest)

// fp16 on the GPU / Neural Engine shifts soft edges a little; a different mask moves whole regions
let same = mean <= 2.0 && share <= 1.0
print(same ? "the cut-outs match" : "the cut-outs differ visibly")
exit(same ? 0 : 1)
