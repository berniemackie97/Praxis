import SwiftUI

struct CanvasGraphView: View {
    let fixture: GraphFixture
    let animated: Bool
    @Binding var selection: String?
    @State private var scale: CGFloat = 1
    @State private var offset: CGSize = .zero
    @State private var dragOrigin: CGSize?
    @State private var zoomOrigin: CGFloat?

    var body: some View {
        GeometryReader { geometry in
            TimelineView(.animation(paused: !animated)) { frame in
                Canvas(opaque: true) { context, size in
                    let nodes = GraphPositions.layout(fixture.nodes, in: size,
                                                      at: frame.date.timeIntervalSinceReferenceDate,
                                                      animated: animated)
                    context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Color(nsColor: .windowBackgroundColor)))
                    context.translateBy(x: offset.width, y: offset.height)
                    context.scaleBy(x: scale, y: scale)
                    let indices = Dictionary(uniqueKeysWithValues: fixture.nodes.enumerated().map { ($0.element.id, $0.offset) })
                    var path = Path()
                    for edge in fixture.edges {
                        guard let source = indices[edge.sourceId], let target = indices[edge.targetId] else { continue }
                        path.move(to: nodes[source])
                        path.addLine(to: nodes[target])
                    }
                    context.stroke(path, with: .color(.secondary.opacity(0.3)), lineWidth: 0.75 / scale)
                    for (index, node) in fixture.nodes.enumerated() {
                        let position = nodes[index]
                        let radius: CGFloat = selection == node.id ? 6 : 4
                        context.fill(Path(ellipseIn: CGRect(x: position.x - radius, y: position.y - radius,
                                                            width: radius * 2, height: radius * 2)),
                                     with: .color(selection == node.id ? .orange : .accentColor))
                        if fixture.nodes.count <= 200 || selection == node.id {
                            context.draw(Text(node.label).font(.system(size: 9)),
                                         at: CGPoint(x: position.x + 8, y: position.y), anchor: .leading)
                        }
                    }
                }
                .contentShape(Rectangle())
                .gesture(SpatialTapGesture().onEnded { event in
                    let points = GraphPositions.layout(fixture.nodes, in: geometry.size,
                                                       at: frame.date.timeIntervalSinceReferenceDate,
                                                       animated: animated)
                    let translated = CGPoint(x: (event.location.x - offset.width) / scale,
                                             y: (event.location.y - offset.height) / scale)
                    selection = zip(fixture.nodes, points).min(by: {
                        hypot($0.1.x - translated.x, $0.1.y - translated.y) <
                        hypot($1.1.x - translated.x, $1.1.y - translated.y)
                    }).flatMap { node, point in
                        hypot(point.x - translated.x, point.y - translated.y) <= 12 / scale ? node.id : nil
                    }
                })
                .simultaneousGesture(DragGesture(minimumDistance: 4).onChanged { value in
                    let origin = dragOrigin ?? offset
                    dragOrigin = origin
                    offset = CGSize(width: origin.width + value.translation.width,
                                    height: origin.height + value.translation.height)
                }.onEnded { _ in dragOrigin = nil })
                .simultaneousGesture(MagnificationGesture().onChanged { factor in
                    let origin = zoomOrigin ?? scale
                    zoomOrigin = origin
                    scale = max(0.5, min(origin * factor, 3))
                }.onEnded { _ in zoomOrigin = nil })
            }
        }
    }
}
