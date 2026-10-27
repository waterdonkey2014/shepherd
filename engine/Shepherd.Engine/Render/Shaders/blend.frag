// Shepherd GL backend, polish round 2: the BlendModes package's "SelfWithScreen" draws (Hidden/BlendModes/*/Grab, UnifiedGrab,
// Framebuffer; Render/ShaderPolicy.cs). s = texture x vertex colour (straight alpha); b = the backdrop under this fragment, copied from
// the target just before the draw (u_grab: the target's size and top-down layout; v_screen = target pixel / size). The result is
// mode(b, s.rgb), with the separable / non-separable blend-mode formulas of the W3C Compositing and Blending spec (Darker/Lighter
// Color: the colour with the lower / higher luminance; Linear*, Vivid/Pin Light, Hard Mix, Subtract, Divide: the usual Photoshop
// definitions). Composition, u_mode.y: 0 = rgb result, alpha s.a, blended SrcAlpha OneMinusSrcAlpha (the draw lerps the target to the
// result by its alpha, as the package's own passes do); 1 = rgb result x s.a, blended One One (added: DecalSpriteLighting's pass).
// Prelude precision: highp (GlBackend.FragPrecision). No loops, no derivatives; the mode is a uniform branch.
uniform sampler2D u_tex;
uniform sampler2D u_grab;
uniform vec4 u_mode;   // x = BlendModes.BlendMode (0 Darken .. 24 Luminosity), y = 1 when the result is added
varying vec4 v_color;
varying vec2 v_uv;
varying vec2 v_screen;

float lum(vec3 c) { return dot(c, vec3(0.3, 0.59, 0.11)); }
vec3 clipColor(vec3 c)
{
    float l = lum(c);
    float n = min(min(c.r, c.g), c.b);
    float x = max(max(c.r, c.g), c.b);
    if (n < 0.0) c = l + (c - l) * l / max(l - n, 1e-5);
    if (x > 1.0) c = l + (c - l) * (1.0 - l) / max(x - l, 1e-5);
    return c;
}
vec3 setLum(vec3 c, float l) { return clipColor(c + (l - lum(c))); }
float satOf(vec3 c) { return max(max(c.r, c.g), c.b) - min(min(c.r, c.g), c.b); }
vec3 setSat(vec3 c, float s)
{
    float mx = max(max(c.r, c.g), c.b);
    float mn = min(min(c.r, c.g), c.b);
    return mx > mn ? (c - mn) * s / (mx - mn) : vec3(0.0);
}
float burn(float b, float s) { return b >= 1.0 ? 1.0 : (s <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - b) / s)); }
float dodge(float b, float s) { return b <= 0.0 ? 0.0 : (s >= 1.0 ? 1.0 : min(1.0, b / (1.0 - s))); }
float hard(float b, float s) { return s <= 0.5 ? 2.0 * b * s : 1.0 - 2.0 * (1.0 - b) * (1.0 - s); }
float soft(float b, float s)
{
    float d = b <= 0.25 ? ((16.0 * b - 12.0) * b + 4.0) * b : sqrt(b);
    return s <= 0.5 ? b - (1.0 - 2.0 * s) * b * (1.0 - b) : b + (2.0 * s - 1.0) * (d - b);
}
float vivid(float b, float s) { return s <= 0.5 ? burn(b, 2.0 * s) : dodge(b, 2.0 * (s - 0.5)); }
float pin(float b, float s) { return s <= 0.5 ? min(b, 2.0 * s) : max(b, 2.0 * (s - 0.5)); }
float divide(float b, float s) { return s <= 0.0 ? (b <= 0.0 ? 0.0 : 1.0) : min(1.0, b / s); }

vec3 blendMode(float m, vec3 b, vec3 s)
{
    if (m < 0.5) return min(b, s);
    if (m < 1.5) return b * s;
    if (m < 2.5) return vec3(burn(b.r, s.r), burn(b.g, s.g), burn(b.b, s.b));
    if (m < 3.5) return max(b + s - 1.0, 0.0);
    if (m < 4.5) return lum(s) < lum(b) ? s : b;
    if (m < 5.5) return max(b, s);
    if (m < 6.5) return b + s - b * s;
    if (m < 7.5) return vec3(dodge(b.r, s.r), dodge(b.g, s.g), dodge(b.b, s.b));
    if (m < 8.5) return min(b + s, 1.0);
    if (m < 9.5) return lum(s) > lum(b) ? s : b;
    if (m < 10.5) return vec3(hard(s.r, b.r), hard(s.g, b.g), hard(s.b, b.b));
    if (m < 11.5) return vec3(soft(b.r, s.r), soft(b.g, s.g), soft(b.b, s.b));
    if (m < 12.5) return vec3(hard(b.r, s.r), hard(b.g, s.g), hard(b.b, s.b));
    if (m < 13.5) return vec3(vivid(b.r, s.r), vivid(b.g, s.g), vivid(b.b, s.b));
    if (m < 14.5) return clamp(b + 2.0 * s - 1.0, 0.0, 1.0);
    if (m < 15.5) return vec3(pin(b.r, s.r), pin(b.g, s.g), pin(b.b, s.b));
    if (m < 16.5) return step(1.0, b + s);
    if (m < 17.5) return abs(b - s);
    if (m < 18.5) return b + s - 2.0 * b * s;
    if (m < 19.5) return max(b - s, 0.0);
    if (m < 20.5) return vec3(divide(b.r, s.r), divide(b.g, s.g), divide(b.b, s.b));
    if (m < 21.5) return setLum(setSat(s, satOf(b)), lum(b));
    if (m < 22.5) return setLum(setSat(b, satOf(s)), lum(b));
    if (m < 23.5) return setLum(s, lum(b));
    return setLum(b, lum(s));
}

void main()
{
    vec4 s = texture2D(u_tex, v_uv) * v_color;
    vec3 b = texture2D(u_grab, v_screen).rgb;
    vec3 r = clamp(blendMode(u_mode.x, b, s.rgb), 0.0, 1.0);
    if (u_mode.y > 0.5) r = r * s.a;
    gl_FragColor = vec4(r, s.a);
}
