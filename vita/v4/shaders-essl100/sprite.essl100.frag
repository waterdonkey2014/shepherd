#version 100
precision highp float;
uniform sampler2D u_tex;
varying vec4 v_color;
varying vec2 v_uv;
void main()
{
    vec4 t = texture2D(u_tex, v_uv);
    gl_FragColor = t * v_color;
}
