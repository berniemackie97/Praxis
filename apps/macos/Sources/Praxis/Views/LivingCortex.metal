#include <metal_stdlib>
using namespace metal;

// Living Cortex: additive point sprites and lines. `along` is the position on a pathway (0..1)
// and `phase` offsets its travelling pulse; phase < 0 means no pulse or twinkle.
struct CortexVertex {
    float3 position;
    float4 color;
    float size;
    float along;
    float phase;
};

struct CortexUniforms {
    float4x4 viewProjection;
    float time;
    float opacity;
    float pointScale;
    float pulse;
};

struct CortexOutput {
    float4 position [[position]];
    float pointSize [[point_size]];
    float4 color;
    float along;
    float phase;
};

vertex CortexOutput cortexVertex(const device CortexVertex *vertices [[buffer(0)]],
                                 constant CortexUniforms &uniforms [[buffer(1)]],
                                 uint id [[vertex_id]]) {
    CortexVertex source = vertices[id];
    CortexOutput output;
    output.position = uniforms.viewProjection * float4(source.position, 1.0);
    output.pointSize = clamp(source.size * uniforms.pointScale / max(1.0, output.position.w), 1.0, 220.0);
    output.color = source.color;
    output.color.a *= uniforms.opacity;
    output.along = source.along;
    output.phase = source.phase;
    return output;
}

fragment float4 cortexPoint(CortexOutput input [[stage_in]],
                            constant CortexUniforms &uniforms [[buffer(1)]],
                            float2 coordinate [[point_coord]]) {
    float2 delta = coordinate * 2.0 - 1.0;
    float radius = dot(delta, delta);
    if (radius > 1.0) {
        discard_fragment();
    }
    float core = exp(-radius * 14.0);
    float halo = 0.35 * exp(-radius * 3.2);
    float twinkle = input.phase < 0.0 ? 1.0 : 0.78 + 0.22 * sin(uniforms.time * 1.7 + input.phase * 6.2831);
    float3 color = mix(input.color.rgb, float3(1.0), core * 0.55);
    return float4(color, min(1.0, core + halo) * input.color.a * twinkle);
}

fragment float4 cortexLine(CortexOutput input [[stage_in]],
                           constant CortexUniforms &uniforms [[buffer(1)]]) {
    float alpha = input.color.a;
    float3 color = input.color.rgb;
    if (input.phase >= 0.0 && uniforms.pulse > 0.0) {
        float head = fract(uniforms.time * 0.22 + input.phase);
        float distance = input.along - head;
        float glow = exp(-distance * distance * 700.0) * uniforms.pulse;
        alpha = min(1.0, alpha + glow);
        color = mix(color, float3(1.0), glow * 0.7);
    }
    return float4(color, alpha);
}
