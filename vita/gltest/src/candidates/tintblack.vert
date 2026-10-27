#version 100
precision highp float;
attribute vec2 a_pos;
attribute vec4 a_color;
attribute vec2 a_uv;
attribute vec4 a_color2;
uniform vec4 u_xform;
varying vec4 v_light;
varying vec4 v_dark;
varying vec2 v_uv;
void main()
{
    v_light = a_color;
    v_dark = a_color2;
    v_uv = a_uv;
    gl_Position = vec4(a_pos * u_xform.xy + u_xform.zw, 0.0, 1.0);
}
