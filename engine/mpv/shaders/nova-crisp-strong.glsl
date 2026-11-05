// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// NOVA Motion live enhance, "Extra crisp": adaptive sharpening of brightness, written for NOVA Motion
// from the contrast-adaptive weighting AMD published as FidelityFX CAS. Each pixel is pushed only as
// far as its 3 x 3 neighbourhood leaves room before clipping, so edges get crisper without halos
// while flat areas and noise stay put; working on brightness alone adds no colour fringes. Hooked at
// the video's own size, so it runs whether or not the picture is enlarged for the screen.

//!HOOK MAIN
//!BIND HOOKED
//!DESC NOVA Motion Extra crisp (adaptive sharpening x2.6)

#define GAIN 2.6

float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

vec4 hook() {
    vec3 a = HOOKED_texOff(vec2(-1.0, -1.0)).rgb;
    vec3 b = HOOKED_texOff(vec2( 0.0, -1.0)).rgb;
    vec3 c = HOOKED_texOff(vec2( 1.0, -1.0)).rgb;
    vec3 d = HOOKED_texOff(vec2(-1.0,  0.0)).rgb;
    vec4 center = HOOKED_texOff(vec2(0.0, 0.0));
    vec3 e = center.rgb;
    vec3 f = HOOKED_texOff(vec2( 1.0,  0.0)).rgb;
    vec3 g = HOOKED_texOff(vec2(-1.0,  1.0)).rgb;
    vec3 h = HOOKED_texOff(vec2( 0.0,  1.0)).rgb;
    vec3 i = HOOKED_texOff(vec2( 1.0,  1.0)).rgb;

    // Soft minimum and maximum: the cross plus the whole 3 x 3, each 0..2
    vec3 mn = min(min(min(d, e), min(f, b)), h);
    mn += min(mn, min(min(a, c), min(g, i)));
    vec3 mx = max(max(max(d, e), max(f, b)), h);
    mx += max(mx, max(max(a, c), max(g, i)));

    // How far this pixel can be pushed before it clips, 0..1, smoothed; the strongest stable kernel
    vec3 amp = sqrt(clamp(min(mn, 2.0 - mx) / max(mx, vec3(1e-5)), 0.0, 1.0));
    vec3 w = amp * (-1.0 / 5.0);
    vec3 sharp = ((b + d + f + h) * w + e) / (1.0 + 4.0 * w);

    // The change in brightness, made stronger, added evenly to all three channels
    float delta = (luma(sharp) - luma(e)) * GAIN;
    return vec4(clamp(e + delta, 0.0, 1.0), center.a);
}
