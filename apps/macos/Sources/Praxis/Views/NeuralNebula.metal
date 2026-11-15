#include <metal_stdlib>
using namespace metal;

struct NebulaVertex {
    float3 position;
    float4 color;
    float size;
};

struct NebulaUniforms {
    float4x4 viewProjection;
    float time;
    float opacity;
    float pointScale;
    float padding;
};

struct NebulaOutput {
    float4 position [[position]];
    float pointSize [[point_size]];
    float4 color;
};

vertex NebulaOutput nebulaVertex(const device NebulaVertex *vertices [[buffer(0)]],
                                 constant NebulaUniforms &uniforms [[buffer(1)]],
                                 uint id [[vertex_id]]) {
    NebulaVertex source = vertices[id];
    NebulaOutput output;
    output.position = uniforms.viewProjection * float4(source.position, 1.0);
    output.pointSize = clamp(source.size * uniforms.pointScale /
                             max(1.0, output.position.w), 1.0, 96.0);
    output.color = source.color;
    output.color.a *= uniforms.opacity;
    return output;
}

fragment float4 nebulaPoint(NebulaOutput input [[stage_in]],
                            float2 position [[point_coord]]) {
    float2 delta = position * 2.0 - 1.0;
    float radiusSquared = dot(delta, delta);
    if (radiusSquared > 1.0) {
        discard_fragment();
    }
    float glow = exp(-radiusSquared * 8.5) +
                 0.24 * exp(-radiusSquared * 2.4);
    return float4(input.color.rgb, min(1.0, glow) * input.color.a);
}

fragment float4 nebulaLine(NebulaOutput input [[stage_in]]) {
    return input.color;
}
