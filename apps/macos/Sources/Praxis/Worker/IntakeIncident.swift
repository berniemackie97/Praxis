import Foundation

struct IntakeIncident: Decodable, Identifiable {
    let incidentId: Int
    let rootPath: String
    let state: String
    let startedUtc: String
    let resolvedUtc: String?
    var id: Int { incidentId }
}
