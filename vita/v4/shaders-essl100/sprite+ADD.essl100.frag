#version 100
precision highp float;
uniform sampler2D u_tex;
varying vec4 v_color;
varying vec2 v_uv;
varying vec4 v_add;
vec3 toLinear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
vec3 toSrgb(vec3 c) { c = max(c, vec3(0.0)); return mix(c * 12.92, 1.055 * pow(c, vec3(0.41666667)) - 0.055, step(vec3(0.0031308), c)); }
void main()
{
    vec4 t = texture2D(u_tex, v_uv);
    float a = t.a * v_color.a;
    vec3 o = toLinear(t.rgb) * v_color.rgb + v_add.rgb * mix(1.0, a, v_add.a);
    gl_FragColor = vec4(toSrgb(o), a);
}
