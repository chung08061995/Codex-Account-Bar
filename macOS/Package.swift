// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexAccountBar",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CodexAccountBar", targets: ["CodexAccountBar"])
    ],
    targets: [
        .executableTarget(
            name: "CodexAccountBar",
            path: "Sources/CodexAccountBar"
        ),
        .testTarget(
            name: "CodexAccountBarTests",
            dependencies: ["CodexAccountBar"],
            path: "Tests/CodexAccountBarTests"
        )
    ]
)
