import Foundation

enum CodexConfigEditor {
    static func currentModel(in content: String) -> String? {
        for line in rootLines(in: content) {
            guard let match = line.range(
                of: #"^\s*model\s*=\s*[\"']([^\"']+)[\"']\s*(?:#.*)?$"#,
                options: .regularExpression
            ) else { continue }
            let matched = String(line[match])
            guard let firstQuote = matched.firstIndex(where: { $0 == "\"" || $0 == "'" }),
                  let lastQuote = matched.lastIndex(where: { $0 == "\"" || $0 == "'" }),
                  firstQuote < lastQuote else { continue }
            return String(matched[matched.index(after: firstQuote)..<lastQuote])
        }
        return nil
    }

    static func settingModel(_ model: String, in content: String) -> String {
        settingRootString("model", value: model, in: content)
    }

    static func settingDirectProvider(
        id: String,
        name: String,
        baseURL: String,
        bearerToken: String,
        model: String,
        in content: String
    ) -> String {
        var updated = settingRootString("model", value: model, in: content)
        updated = settingRootString("model_provider", value: id, in: updated)
        let block = [
            "[model_providers.\(id)]",
            "name = \(tomlString(name))",
            "base_url = \(tomlString(baseURL))",
            "experimental_bearer_token = \(tomlString(bearerToken))",
            "wire_api = \"responses\""
        ]
        return replacingSection("model_providers.\(id)", with: block, in: updated)
    }

    static func settingNativeModel(_ model: String, in content: String) -> String {
        let updated = settingRootString("model", value: model, in: content)
        return removingRootKey("model_provider", in: updated)
    }

    static func removingDirectProvider(_ id: String, in content: String) -> String {
        let withoutSelection = removingRootKey("model_provider", in: content)
        return replacingSection("model_providers.\(id)", with: [], in: withoutSelection)
    }

    static func setDirectProvider(
        id: String,
        name: String,
        baseURL: String,
        bearerToken: String,
        model: String,
        at url: URL
    ) throws {
        let original = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        let updated = settingDirectProvider(
            id: id,
            name: name,
            baseURL: baseURL,
            bearerToken: bearerToken,
            model: model,
            in: original
        )
        guard updated != original else { return }
        try updated.write(to: url, atomically: true, encoding: .utf8)
    }

    static func setNativeModel(_ model: String, at url: URL) throws {
        let original = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        let updated = settingNativeModel(model, in: original)
        guard updated != original else { return }
        try updated.write(to: url, atomically: true, encoding: .utf8)
    }

    private static func settingRootString(_ key: String, value: String, in content: String) -> String {
        let replacement = "\(key) = \(tomlString(value))"
        var lines = content.components(separatedBy: .newlines)
        let rootEnd = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("[") })
            ?? lines.endIndex

        if let index = lines[..<rootEnd].firstIndex(where: {
            $0.range(of: "^\\s*\(NSRegularExpression.escapedPattern(for: key))\\s*=", options: .regularExpression) != nil
        }) {
            lines[index] = replacement
        } else {
            lines.insert(replacement, at: 0)
        }
        return lines.joined(separator: "\n")
    }

    private static func removingRootKey(_ key: String, in content: String) -> String {
        var lines = content.components(separatedBy: .newlines)
        let rootEnd = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("[") })
            ?? lines.endIndex
        if let index = lines[..<rootEnd].firstIndex(where: {
            $0.range(of: "^\\s*\(NSRegularExpression.escapedPattern(for: key))\\s*=", options: .regularExpression) != nil
        }) {
            lines.remove(at: index)
        }
        return lines.joined(separator: "\n")
    }

    private static func replacingSection(_ section: String, with block: [String], in content: String) -> String {
        var lines = content.components(separatedBy: .newlines)
        let header = "[\(section)]"
        if let start = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces) == header }) {
            let end = lines[(start + 1)...].firstIndex(where: {
                $0.trimmingCharacters(in: .whitespaces).hasPrefix("[")
            }) ?? lines.endIndex
            lines.replaceSubrange(start..<end, with: block.isEmpty ? [] : block + [""])
        } else {
            guard !block.isEmpty else { return content }
            while lines.last?.isEmpty == true { lines.removeLast() }
            lines += [""] + block + [""]
        }
        return lines.joined(separator: "\n")
    }

    private static func tomlString(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }

    static func setModel(_ model: String, at url: URL) throws {
        let original = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        let updated = settingModel(model, in: original)
        guard updated != original else { return }
        try updated.write(to: url, atomically: true, encoding: .utf8)
    }

    private static func rootLines(in content: String) -> ArraySlice<String> {
        let lines = content.components(separatedBy: .newlines)
        let rootEnd = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("[") })
            ?? lines.endIndex
        return lines[..<rootEnd]
    }
}
