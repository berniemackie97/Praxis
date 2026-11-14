// praxis-vision: on-device text and speech recognition for the Praxis worker.
//
//   praxis-vision ocr --kind image|pdf --max-pages N <file>
//   praxis-vision transcribe [--locale en-US] <file>
//
// Writes {"pages":["..."]}, {"cues":[{"start":0.0,"end":1.6,"text":"..."}]} or {"error":"code"}
// to stdout. OCR uses Apple Vision (accurate recognition, automatic language detection) and PDFKit
// to render PDF pages. Transcription uses SpeechAnalyzer on macOS 26 and later, which runs on
// device; the system installs its speech model once. Reads only the given file, sends none of it
// anywhere and keeps nothing.

import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import PDFKit
import Speech
import Vision

struct Cue: Encodable {
    var start: Double
    var end: Double
    var text: String
}

struct Output: Encodable {
    var pages: [String]?
    var cues: [Cue]?
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

@available(macOS 26, *)
func transcribe(_ url: URL, localeIdentifier: String) async -> Output {
    guard SpeechTranscriber.isAvailable else { return Output(error: "transcription_unavailable") }
    guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: localeIdentifier)) else {
        return Output(error: "unsupported_locale")
    }

    let transcriber = SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
    do {
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            try await request.downloadAndInstall()
        }
    } catch {
        return Output(error: "speech_model_unavailable")
    }

    let file: AVAudioFile
    do {
        file = try AVAudioFile(forReading: url)
    } catch {
        return Output(error: "media_unreadable")
    }

    let analyzer = SpeechAnalyzer(modules: [transcriber])
    let collector = Task { () throws -> [Cue] in
        var cues: [Cue] = []
        for try await result in transcriber.results {
            let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
            let start = result.range.start.seconds
            let end = result.range.end.seconds
            if !text.isEmpty, start.isFinite, end.isFinite, end >= start {
                cues.append(Cue(start: start, end: end, text: text))
            }
        }
        return cues
    }

    do {
        if let last = try await analyzer.analyzeSequence(from: file) {
            try await analyzer.finalizeAndFinish(through: last)
        } else {
            await analyzer.cancelAndFinishNow()
        }
        return Output(cues: try await collector.value)
    } catch {
        collector.cancel()
        return Output(error: "transcription_failed")
    }
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments.first == "transcribe" {
    guard arguments.count == 2 || (arguments.count == 4 && arguments[1] == "--locale"), let path = arguments.last else {
        finish(Output(error: "usage"))
    }

    let localeIdentifier = arguments.count == 4 ? arguments[2] : Locale.current.identifier
    guard #available(macOS 26, *) else { finish(Output(error: "transcription_unavailable")) }
    let done = DispatchSemaphore(value: 0)
    var output = Output(error: "transcription_failed")
    Task {
        output = await transcribe(URL(fileURLWithPath: path), localeIdentifier: localeIdentifier)
        done.signal()
    }
    done.wait()
    finish(output)
}

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
