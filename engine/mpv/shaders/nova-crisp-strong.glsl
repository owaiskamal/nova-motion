// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// NOVA Motion live enhance, "Crisp": contrast-adaptive sharpening after the upscale, written for
// NOVA Motion from the algorithm AMD published as FidelityFX CAS. Each pixel is sharpened by how
// much room its 3 x 3 neighbourhood leaves before clipping, so edges get crisper without halos and
// flat areas and noise are left alone.

//!HOOK SCALED
//!BIND HOOKED
//!DESC NOVA Motion crisp (adaptive sharpening, 0.85)

#define SHARPNESS 0.85

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

    // How far this pixel can be pushed before it clips, 0..1, smoothed
    vec3 amp = sqrt(clamp(min(mn, 2.0 - mx) / max(mx, vec3(1e-5)), 0.0, 1.0));
    vec3 w = amp * (-1.0 / mix(8.0, 5.0, SHARPNESS));
    vec3 outc = ((b + d + f + h) * w + e) / (1.0 + 4.0 * w);
    return vec4(clamp(outc, 0.0, 1.0), center.a);
}
