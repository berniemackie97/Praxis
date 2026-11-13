// praxis-vision: on-device text recognition for the Praxis worker.
//
//   praxis-vision ocr --kind image|pdf --max-pages N <file>
//
// Writes {"pages":["..."]} or {"error":"code"} to stdout. Uses Apple Vision (accurate
// recognition, automatic language detection) and PDFKit to render PDF pages. Reads only the
// given file, performs no network access and keeps nothing.

import CoreGraphics
import Foundation
import ImageIO
import PDFKit
import Vision

struct Output: Encodable {
    var pages: [String]?
    var error: String?
}

func finish(_ output: Output) -> Never {
    let data = (try? JSONEncoder().encode(output)) ?? Data("{\"error\":\"encoding\"}".utf8)
    FileHandle.standardOutput.write(data)
    exit(output.error == nil ? 0 : 2)
}

let maximumPixels = 4_096

func recognize(_ image: CGImage) -> String {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = true
    request.automaticallyDetectsLanguage = true
    let handler = VNImageRequestHandler(cgImage: image, options: [:])
    guard (try? handler.perform([request])) != nil, let observations = request.results else { return "" }

    // Group observations into visual lines (top to bottom, then left to right) so a receipt's
    // label and amount stay on one line.
    let sorted = observations.sorted { $0.boundingBox.midY > $1.boundingBox.midY }
    var lines: [[VNRecognizedTextObservation]] = []
    for observation in sorted {
        if let last = lines.last?.first,
           abs(last.boundingBox.midY - observation.boundingBox.midY) < min(last.boundingBox.height, observation.boundingBox.height) / 2 {
            lines[lines.count - 1].append(observation)
        } else {
            lines.append([observation])
        }
    }
    return lines.map { line in
        line.sorted { $0.boundingBox.minX < $1.boundingBox.minX }
            .compactMap { $0.topCandidates(1).first?.string }
            .joined(separator: "  ")
    }.joined(separator: "\n")
}

func loadImage(_ url: URL) -> CGImage? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
    let options: [CFString: Any] = [
        kCGImageSourceCreateThumbnailFromImageAlways: true,
        kCGImageSourceCreateThumbnailWithTransform: true,
        kCGImageSourceThumbnailMaxPixelSize: maximumPixels
    ]
    return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
}

func render(_ page: PDFPage) -> CGImage? {
    let bounds = page.bounds(for: .mediaBox)
    guard bounds.width > 0, bounds.height > 0 else { return nil }
    let scale = min(2.5, CGFloat(maximumPixels) / max(bounds.width, bounds.height))
    let width = Int(bounds.width * scale)
    let height = Int(bounds.height * scale)
    guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
    context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    context.scaleBy(x: scale, y: scale)
    context.translateBy(x: -bounds.minX, y: -bounds.minY)
    page.draw(with: .mediaBox, to: context)
    return context.makeImage()
}

let arguments = Array(CommandLine.arguments.dropFirst())
guard arguments.first == "ocr", let path = arguments.last, arguments.count >= 2 else { finish(Output(error: "usage")) }
var kind = "image"
var maximumPages = 1
var index = 1
while index < arguments.count - 1 {
    switch arguments[index] {
    case "--kind" where index + 1 < arguments.count - 1:
        kind = arguments[index + 1]
        index += 2
    case "--max-pages" where index + 1 < arguments.count - 1:
        maximumPages = max(1, min(500, Int(arguments[index + 1]) ?? 1))
        index += 2
    default:
        finish(Output(error: "usage"))
    }
}

let url = URL(fileURLWithPath: path)
switch kind {
case "image":
    guard let image = loadImage(url) else { finish(Output(error: "image_unreadable")) }
    finish(Output(pages: [recognize(image)]))
case "pdf":
    guard let document = PDFDocument(url: url) else { finish(Output(error: "pdf_unreadable")) }
    if document.isEncrypted && document.isLocked { finish(Output(error: "pdf_encrypted")) }
    var pages: [String] = []
    for number in 0..<min(document.pageCount, maximumPages) {
        guard let page = document.page(at: number), let image = render(page) else {
            pages.append("")
            continue
        }
        autoreleasepool { pages.append(recognize(image)) }
    }
    finish(Output(pages: pages))
default:
    finish(Output(error: "usage"))
}
