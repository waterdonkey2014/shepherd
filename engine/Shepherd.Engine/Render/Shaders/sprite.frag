// Shepherd GL backend: texture x vertex colour (tint and premultiplied alpha are already in the vertex colour).
// SDF variant: TextMeshPro distance-field atlas, alpha = smoothstep of the distance over [0.44, 0.56], white rgb:
// the same curve the SDL path bakes into the texture on upload (Gfx.BakeSdf), here applied after filtering.
// Prelude precision: highp (GlBackend.FragPrecision).
// ADD variant (fidelity round 1, the lit-shader program): the game is a linear-colour-space project, so its lit shaders work on
// linear colours: the texel is decoded from sRGB, multiplied by the vertex colour (a linear multiplier: light x tint), the added
// colour (linear: fog, fill, emission) is added -- times the fragment's alpha when v_add.a = 1 -- and the result is encoded back to
// sRGB for the sRGB-encoded RGBA8 target (exact sRGB curves; values above 1 clamp at the write).
uniform sampler2D u_tex;
varying vec4 v_color;
varying vec2 v_uv;
#ifdef ADD
varying vec4 v_add;
vec3 toLinear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
vec3 toSrgb(vec3 c) { c = max(c, vec3(0.0)); return mix(c * 12.92, 1.055 * pow(c, vec3(0.41666667)) - 0.055, step(vec3(0.0031308), c)); }
#endif
void main()
{
    vec4 t = texture2D(u_tex, v_uv);
#ifdef SDF
    float a = clamp((t.a - 0.44) / 0.12, 0.0, 1.0);
    t = vec4(1.0, 1.0, 1.0, a * a * (3.0 - 2.0 * a));
#endif
#ifdef ADD
    float a = t.a * v_color.a;
    vec3 o = toLinear(t.rgb) * v_color.rgb + v_add.rgb * mix(1.0, a, v_add.a);
    gl_FragColor = vec4(toSrgb(o), a);
#else
    gl_FragColor = t * v_color;
#endif
}
