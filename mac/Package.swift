// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "RaskladushkaMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "RaskladushkaMacCore", targets: ["RaskladushkaMacCore"]),
        .executable(name: "Raskladushka", targets: ["Raskladushka"])
    ],
    targets: [
        .target(name: "RaskladushkaMacCore"),
        .executableTarget(name: "Raskladushka", dependencies: ["RaskladushkaMacCore"],
                          linkerSettings: [.linkedFramework("AppKit"), .linkedFramework("AVFoundation"),
                                           .linkedFramework("CoreGraphics"), .linkedFramework("Security")]),
        .testTarget(name: "RaskladushkaMacCoreTests", dependencies: ["RaskladushkaMacCore"])
    ]
)
