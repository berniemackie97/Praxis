// swift-tools-version: 6.2
import PackageDescription

let strict: [SwiftSetting] = [
    .treatAllWarnings(as: .error),
    .enableUpcomingFeature("ExistentialAny"),
    .enableUpcomingFeature("InternalImportsByDefault")
]

let package = Package(
    name: "Praxis",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "Praxis", targets: ["Praxis"])
    ],
    targets: [
        .executableTarget(
            name: "Praxis",
            swiftSettings: strict
        ),
        .testTarget(
            name: "PraxisTests",
            dependencies: ["Praxis"],
            swiftSettings: strict
        )
    ],
    swiftLanguageModes: [.v6]
)
